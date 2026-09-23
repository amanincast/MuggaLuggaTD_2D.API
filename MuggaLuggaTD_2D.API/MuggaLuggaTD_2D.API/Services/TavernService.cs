using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

public enum TavernError
{
    None,
    NoBoard,
    NoSuchSlot,
    AlreadyHired,
    RosterFull,
    CannotAfford,
    ContentMismatch
}

public record TavernOutcome(TavernError Error, string? Message = null)
{
    public bool Succeeded => Error == TavernError.None;
}

/// <summary>
/// The Tavern: who is drinking there, and what it costs to take them on.
///
/// <para>Design doc 05 §4. Two rules shape everything here:</para>
///
/// <para><b>The server rolls.</b> A board the client could roll is a board it could roll again until
/// it liked one, and a character is the most valuable thing in the game. The client asks for the
/// board and asks to hire slot N; it never sends a recruit.</para>
///
/// <para><b>The restock is paid for with a dungeon.</b> There is no timer and no refresh button, so
/// the only way to see new faces is to go and clear something - which is what keeps the Tavern
/// attached to the game rather than being a menu you poll. The dungeon's tier raises the rarity odds
/// and its region's biome nudges the affinities, so <i>where</i> you cleared shows up on the board.</para>
/// </summary>
public class TavernService
{
    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly MaterialWalletService _wallet;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<TavernService> _logger;

    public TavernService(
        ApplicationDbContext context,
        IGameContentProvider content,
        MaterialWalletService wallet,
        ISessionLog sessionLog,
        ILogger<TavernService> logger)
    {
        _context = context;
        _content = content;
        _wallet = wallet;
        _sessionLog = sessionLog;
        _logger = logger;
    }

    /// <summary>
    /// This player's board in this realm, in slot order.
    ///
    /// <para>A player who has never cleared a dungeon has no board, and rather than show them an
    /// empty room the first read rolls one at tier 1 with no biome. That is the Tavern's opening
    /// hand: the worst odds it offers, and no reason not to go and improve them.</para>
    /// </summary>
    public async Task<List<TavernRecruit>> ReadBoardAsync(Guid gameInstanceId, string userId)
    {
        var board = await LoadBoardAsync(gameInstanceId, userId);
        if (board.Count > 0)
            return board;

        await RestockAsync(gameInstanceId, userId, locationTier: 1, biome: null, reason: "first-visit");
        return await LoadBoardAsync(gameInstanceId, userId);
    }

    private async Task<List<TavernRecruit>> LoadBoardAsync(Guid gameInstanceId, string userId)
        => await _context.TavernRecruits
            .Where(r => r.GameInstanceId == gameInstanceId && r.UserId == userId)
            .OrderBy(r => r.Slot)
            .ToListAsync();

    /// <summary>
    /// Replaces this player's board. Called on a claimed dungeon clear, and once on a first visit.
    ///
    /// <para>Everything goes, hired slots included: the board is what walked in tonight, not a
    /// running tally. A hire is already recorded elsewhere, so nothing is lost with it.</para>
    /// </summary>
    public async Task RestockAsync(
        Guid gameInstanceId, string userId, int locationTier, BiomeType? biome, string reason)
    {
        var rolls = RecruitRoller.RollBoard(
            _content.RecruitSheets, _content.Signatures, locationTier, biome, Random.Shared);

        if (rolls.Count == 0)
        {
            // Content cannot make a recruit. Leave the old board rather than wiping it for nothing.
            _logger.LogWarning("Tavern restock produced no recruits — content has no class with both a sheet and a signature.");
            return;
        }

        var existing = await LoadBoardAsync(gameInstanceId, userId);
        if (existing.Count > 0)
            _context.TavernRecruits.RemoveRange(existing);

        var rolledAt = DateTime.UtcNow;
        for (int slot = 0; slot < rolls.Count; slot++)
        {
            var roll = rolls[slot];
            _context.TavernRecruits.Add(new TavernRecruit
            {
                GameInstanceId = gameInstanceId,
                UserId = userId,
                Slot = slot,
                Name = roll.Name,
                Sheet = roll.Sheet,
                CharacterClass = roll.Class,
                SignatureId = roll.SignatureId,
                Affinity = roll.Affinity,
                Rarity = roll.Rarity,
                RolledAt = rolledAt
            });
        }

        await _context.SaveChangesAsync();

        _sessionLog.Log("TAVERN-RESTOCK",
            $"user={userId} instance={gameInstanceId} reason={reason} tier={locationTier} " +
            $"biome={biome?.ToString() ?? "none"} " +
            $"board={string.Join(",", rolls.Select(r => $"{r.Class}/{r.SignatureId}/{r.Affinity}/{r.Rarity}"))}");
    }

    /// <summary>
    /// Hires the recruit in <paramref name="slot"/>: charges the wallet, writes the entitlement
    /// record, and returns it so the client can add the character to its roster with the id the
    /// server chose.
    ///
    /// <para>The order matters. The cost is taken before the record is written, so a hire that
    /// cannot be paid for leaves nothing behind; and the slot is marked hired in the same save as
    /// the record, so two requests for one slot cannot both succeed.</para>
    /// </summary>
    public async Task<(TavernOutcome Outcome, HiredCharacter? Hired)> HireAsync(
        Guid gameInstanceId, string userId, int slot)
    {
        var recruit = await _context.TavernRecruits
            .FirstOrDefaultAsync(r => r.GameInstanceId == gameInstanceId && r.UserId == userId && r.Slot == slot);

        if (recruit == null)
            return (new TavernOutcome(TavernError.NoSuchSlot, "No recruit in that slot."), null);

        if (recruit.HiredAt != null)
            return (new TavernOutcome(TavernError.AlreadyHired, "That recruit has already been hired."), null);

        // Re-check the roll against live content: a signature removed since the board was rolled
        // would hire a character with no kit at all.
        var signature = SignatureRules.Find(_content.Signatures, recruit.SignatureId);
        if (signature == null || !SignatureRules.IsAffinityAllowed(signature, recruit.Affinity))
        {
            return (new TavernOutcome(TavernError.ContentMismatch,
                "That recruit is no longer possible — clear a dungeon for a new board."), null);
        }

        int roster = await _context.HiredCharacters
            .CountAsync(h => h.GameInstanceId == gameInstanceId && h.UserId == userId);

        if (roster >= TavernRules.RosterCap)
        {
            return (new TavernOutcome(TavernError.RosterFull,
                $"Your roster is full ({TavernRules.RosterCap})."), null);
        }

        var cost = TavernRules.HireCost(recruit.Rarity);
        var payment = await _wallet.SpendAsync(gameInstanceId, userId, cost, $"tavern-hire slot={slot}");
        if (!payment.Succeeded)
            return (new TavernOutcome(TavernError.CannotAfford, payment.Message), null);

        var hired = new HiredCharacter
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            // Server-generated. An id the client chose would be an id the client could change, and
            // this id is the only thing tying a save's character to its entitlement.
            CharacterId = Guid.NewGuid().ToString(),
            Name = recruit.Name,
            Sheet = recruit.Sheet,
            CharacterClass = recruit.CharacterClass,
            SignatureId = recruit.SignatureId,
            Affinity = recruit.Affinity,
            Rarity = recruit.Rarity
        };

        recruit.HiredAt = hired.HiredAt;
        _context.HiredCharacters.Add(hired);
        await _context.SaveChangesAsync();

        _sessionLog.Log("TAVERN-HIRE",
            $"user={userId} instance={gameInstanceId} slot={slot} character={hired.CharacterId} " +
            $"{hired.CharacterClass}/{hired.SignatureId}/{hired.Affinity}/{hired.Rarity} " +
            $"cost={string.Join(",", cost.Select(c => $"{c.MaterialName}x{c.Quantity}"))} roster={roster + 1}");

        _logger.LogInformation(
            "Tavern hire by {User}: {Class} {Signature}/{Affinity} ({Rarity}) as {CharacterId}.",
            userId, hired.CharacterClass, hired.SignatureId, hired.Affinity, hired.Rarity, hired.CharacterId);

        return (new TavernOutcome(TavernError.None), hired);
    }

    /// <summary>Every character this player is entitled to in this realm.</summary>
    public async Task<List<HiredCharacter>> ReadHiredAsync(Guid gameInstanceId, string userId)
        => await _context.HiredCharacters
            .Where(h => h.GameInstanceId == gameInstanceId && h.UserId == userId)
            .OrderBy(h => h.HiredAt)
            .ToListAsync();
}
