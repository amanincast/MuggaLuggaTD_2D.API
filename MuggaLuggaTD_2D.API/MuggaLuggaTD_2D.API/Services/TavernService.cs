using Enums;
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
    ContentMismatch,
    LureAlreadyStanding,
    NoSuchCrystal,
    BoardIsFull,
    NoContent
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
    private readonly GoldService _gold;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<TavernService> _logger;

    public TavernService(
        ApplicationDbContext context,
        IGameContentProvider content,
        MaterialWalletService wallet,
        GoldService gold,
        ISessionLog sessionLog,
        ILogger<TavernService> logger)
    {
        _context = context;
        _content = content;
        _wallet = wallet;
        _gold = gold;
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

        await FillBoardAsync(gameInstanceId, userId, locationTier: 1, biome: null, reason: "first-visit");
        return await LoadBoardAsync(gameInstanceId, userId);
    }

    private async Task<List<TavernRecruit>> LoadBoardAsync(Guid gameInstanceId, string userId)
        => await _context.TavernRecruits
            .Where(r => r.GameInstanceId == gameInstanceId && r.UserId == userId)
            .OrderBy(r => r.Slot)
            .ToListAsync();

    /// <summary>
    /// Word reaches the Tavern and somebody new sits down — one recruit, into a free seat.
    ///
    /// <para><b>This is the change that makes saving up possible.</b> A clear used to roll six new
    /// faces and throw the old six away, which meant that farming the materials to afford a recruit
    /// was the very thing that took that recruit away. Now a clear only ever <i>adds</i>, so nothing
    /// a player is holding out for can vanish without their say-so.</para>
    ///
    /// <para>A full board takes nobody, and that is the point rather than a limitation: it is the
    /// situation the paid refresh exists for. The lure is <b>not</b> spent when there is no room —
    /// a crystal that bought nothing would be a crystal quietly lost.</para>
    /// </summary>
    public async Task<TavernOutcome> BringARecruitAsync(
        Guid gameInstanceId, string userId, int locationTier, BiomeType? biome, string reason)
    {
        // The clear resets the refresh price whatever else happens here. The player did the work, and
        // the escalation is a pull back toward playing rather than a toll on the board - so it must
        // reset even when the room turns out to be full and nobody can sit down.
        var state = await StateAsync(gameInstanceId, userId);
        state.RefreshesSinceClear = 0;
        state.UpdatedAt = DateTime.UtcNow;

        var board = await LoadBoardAsync(gameInstanceId, userId);

        int seat = FreeSeat(board);
        if (seat < 0)
        {
            await _context.SaveChangesAsync();

            _sessionLog.Log("TAVERN-ARRIVAL",
                $"user={userId} instance={gameInstanceId} reason={reason} FULL — nobody could sit down " +
                "(refresh price reset anyway)");

            return new TavernOutcome(TavernError.BoardIsFull,
                "The Tavern is full. Hire somebody, or pay for a fresh room.");
        }

        var lure = await StandingLureAsync(gameInstanceId, userId);
        double target = TargetFor(lure);

        var roll = RecruitRoller.Roll(
            _content.RecruitSheets, _content.Signatures, locationTier, biome, Random.Shared,
            lure?.Affinity, target);

        if (roll == null)
        {
            _logger.LogWarning("Tavern arrival produced nobody — content has no class with both a sheet and a signature.");
            return new TavernOutcome(TavernError.NoContent, "Nobody came.");
        }

        _context.TavernRecruits.Add(Seat(gameInstanceId, userId, seat, roll, DateTime.UtcNow));

        string lureNote = SettleLure(lure, new[] { roll }, target);

        await _context.SaveChangesAsync();

        _sessionLog.Log("TAVERN-ARRIVAL",
            $"user={userId} instance={gameInstanceId} reason={reason} tier={locationTier} " +
            $"biome={biome?.ToString() ?? "none"} lure={lureNote} seat={seat} " +
            $"recruit={roll.Class}/{roll.SignatureId}/{roll.Affinity}/{roll.Rarity}");

        return new TavernOutcome(TavernError.None);
    }

    /// <summary>
    /// Clears the room and rolls a full board, for gold.
    ///
    /// <para>The escape hatch from a board full of people you do not want. It is deliberately the
    /// <i>only</i> thing that removes a recruit you did not hire, so the player is always the one who
    /// decides that a face on the board is no longer worth waiting for.</para>
    /// </summary>
    public async Task<TavernOutcome> RefreshAsync(Guid gameInstanceId, string userId)
    {
        var state = await StateAsync(gameInstanceId, userId);
        long cost = TavernRules.RefreshCostFor(state.RefreshesSinceClear);

        var payment = await _gold.SpendAsync(gameInstanceId, userId, cost, "tavern-refresh");

        if (!payment.Succeeded)
            return new TavernOutcome(TavernError.CannotAfford, payment.Message);

        // Tier 1 and no biome: a refresh is bought rather than earned, so it buys the Tavern's
        // ordinary odds. Where you fought is what improves them, and that is still a clear's job.
        var outcome = await FillBoardAsync(gameInstanceId, userId, 1, null, "paid-refresh");

        if (!outcome.Succeeded)
        {
            // Nobody came, so the gold goes back. A refresh that charged for an empty room would be
            // a bug the player pays for - and it must not raise the price of the next one either.
            await _gold.GrantAsync(gameInstanceId, userId, cost, "tavern-refresh-refund");
            return outcome;
        }

        // Each refresh makes the next dearer, until a dungeon puts it back to nothing.
        state.RefreshesSinceClear++;
        state.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _sessionLog.Log("TAVERN-REFRESH",
            $"user={userId} instance={gameInstanceId} cost={cost} " +
            $"next={TavernRules.RefreshCostFor(state.RefreshesSinceClear)} " +
            $"sinceClear={state.RefreshesSinceClear}");

        return outcome;
    }

    /// <summary>What the player's next refresh would cost, for the board to quote.</summary>
    public async Task<long> RefreshCostAsync(Guid gameInstanceId, string userId)
    {
        var state = await _context.TavernStates.FirstOrDefaultAsync(
            t => t.GameInstanceId == gameInstanceId && t.UserId == userId);

        return TavernRules.RefreshCostFor(state?.RefreshesSinceClear ?? 0);
    }

    private async Task<TavernState> StateAsync(Guid gameInstanceId, string userId)
    {
        var state = await _context.TavernStates.FirstOrDefaultAsync(
            t => t.GameInstanceId == gameInstanceId && t.UserId == userId);

        if (state != null) return state;

        state = new TavernState { GameInstanceId = gameInstanceId, UserId = userId };
        _context.TavernStates.Add(state);
        return state;
    }

    /// <summary>
    /// Wipes whatever is there and rolls a full board. Used by a first visit and a paid refresh —
    /// the two moments a whole room is replaced at once.
    /// </summary>
    private async Task<TavernOutcome> FillBoardAsync(
        Guid gameInstanceId, string userId, int locationTier, BiomeType? biome, string reason)
    {
        var lure = await StandingLureAsync(gameInstanceId, userId);
        double target = TargetFor(lure);

        var rolls = RecruitRoller.RollBoard(
            _content.RecruitSheets, _content.Signatures, locationTier, biome, Random.Shared,
            TavernRules.BoardSize, lure?.Affinity, target);

        if (rolls.Count == 0)
        {
            // Content cannot make a recruit. Leave the old board rather than wiping it for nothing.
            _logger.LogWarning("Tavern board produced no recruits — content has no class with both a sheet and a signature.");
            return new TavernOutcome(TavernError.NoContent, "Nobody came.");
        }

        var existing = await LoadBoardAsync(gameInstanceId, userId);
        if (existing.Count > 0)
            _context.TavernRecruits.RemoveRange(existing);

        var rolledAt = DateTime.UtcNow;
        for (int slot = 0; slot < rolls.Count; slot++)
            _context.TavernRecruits.Add(Seat(gameInstanceId, userId, slot, rolls[slot], rolledAt));

        string lureNote = SettleLure(lure, rolls, target);

        await _context.SaveChangesAsync();

        _sessionLog.Log("TAVERN-BOARD",
            $"user={userId} instance={gameInstanceId} reason={reason} tier={locationTier} " +
            $"biome={biome?.ToString() ?? "none"} lure={lureNote} " +
            $"board={string.Join(",", rolls.Select(r => $"{r.Class}/{r.SignatureId}/{r.Affinity}/{r.Rarity}"))}");

        return new TavernOutcome(TavernError.None);
    }

    /// <summary>The lowest seat nobody is sitting in, or -1 when the room is full.</summary>
    private static int FreeSeat(List<TavernRecruit> board)
    {
        for (int slot = 0; slot < TavernRules.BoardSize; slot++)
        {
            if (!board.Any(r => r.Slot == slot))
                return slot;
        }

        return -1;
    }

    private static TavernRecruit Seat(
        Guid gameInstanceId, string userId, int slot, RecruitRoll roll, DateTime rolledAt)
        => new()
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
        };

    private static double TargetFor(TavernLure? lure)
        => lure == null ? 0 : TavernRules.EffectiveLureTarget(lure.PendingStrength, lure.MissedRestocks);

    /// <summary>
    /// Spends the standing lure against whoever just arrived, and records the pity if they were not
    /// what it asked for. The lure is spent whether or not it worked — that is what makes a miss
    /// worth something rather than a wasted crystal.
    /// </summary>
    private static string SettleLure(TavernLure? lure, IReadOnlyList<RecruitRoll> arrivals, double target)
    {
        if (lure == null)
            return "none";

        bool appeared = arrivals.Any(r => r.Affinity == lure.Affinity);

        string note = $"{lure.PendingStrength}/{lure.Affinity}@{target:P0}" +
                      (appeared ? " HIT" : $" MISS pity={lure.MissedRestocks + 1}");

        lure.MissedRestocks = appeared ? 0 : lure.MissedRestocks + 1;
        lure.PendingStrength = TavernRules.LureStrength.None;
        lure.PlacedAt = null;
        lure.UpdatedAt = DateTime.UtcNow;

        return note;
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

        // The recruit leaves the board rather than sitting there marked hired. The board is a set of
        // seats now, not a batch that gets wiped, so a hired card left in place would block its seat
        // for good — and hiring is the main way a seat comes free for the next arrival.
        _context.TavernRecruits.Remove(recruit);
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
    // -----------------------------------------------------------------
    // Lures and pity
    // -----------------------------------------------------------------

    /// <summary>Every affinity this player has a standing offer or a debt on, in affinity order.</summary>
    public async Task<List<TavernLure>> ReadLuresAsync(Guid gameInstanceId, string userId)
        => await _context.TavernLures
            .Where(l => l.GameInstanceId == gameInstanceId && l.UserId == userId)
            .OrderBy(l => l.Affinity)
            .ToListAsync();

    /// <summary>The offer waiting to be spent, if there is one.</summary>
    public async Task<TavernLure?> StandingLureAsync(Guid gameInstanceId, string userId)
        => await _context.TavernLures
            .FirstOrDefaultAsync(l => l.GameInstanceId == gameInstanceId && l.UserId == userId
                && l.PendingStrength != TavernRules.LureStrength.None);

    /// <summary>
    /// Offers a crystal against the next restock. The crystal is spent now, not when the board
    /// turns over, so the commitment is real before the player goes out — and so a restock, which
    /// happens inside a PvE claim, never has to take a payment that might fail.
    ///
    /// <para>Only one offer may stand at a time. A second would be silently unspent by the restock
    /// that takes the first, and quietly losing a crystal is worse than being told no.</para>
    /// </summary>
    public async Task<(TavernOutcome Outcome, TavernLure? Lure)> PlaceLureAsync(
        Guid gameInstanceId, string userId, AffinityTypes affinity, TavernRules.LureStrength strength)
    {
        if (strength == TavernRules.LureStrength.None)
            return (new TavernOutcome(TavernError.NoSuchCrystal, "No crystal was named."), null);

        var standing = await StandingLureAsync(gameInstanceId, userId);
        if (standing != null)
        {
            return (new TavernOutcome(TavernError.LureAlreadyStanding,
                $"A {standing.PendingStrength} {standing.Affinity} crystal is already offered. " +
                "Clear a dungeon to spend it."), null);
        }

        var cost = TavernRules.LureCost(affinity, strength);

        var payment = await _wallet.SpendAsync(
            gameInstanceId, userId, cost, $"tavern-lure {strength}/{affinity}");

        if (!payment.Succeeded)
            return (new TavernOutcome(TavernError.CannotAfford, payment.Message), null);

        var row = await _context.TavernLures.FirstOrDefaultAsync(
            l => l.GameInstanceId == gameInstanceId && l.UserId == userId && l.Affinity == affinity);

        if (row == null)
        {
            row = new TavernLure
            {
                GameInstanceId = gameInstanceId,
                UserId = userId,
                Affinity = affinity
            };
            _context.TavernLures.Add(row);
        }

        row.PendingStrength = strength;
        row.PlacedAt = DateTime.UtcNow;
        row.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _sessionLog.Log("TAVERN-LURE",
            $"user={userId} instance={gameInstanceId} affinity={affinity} strength={strength} " +
            $"pity={row.MissedRestocks} target={TavernRules.EffectiveLureTarget(strength, row.MissedRestocks):P0}");

        return (new TavernOutcome(TavernError.None), row);
    }

    public async Task<List<HiredCharacter>> ReadHiredAsync(Guid gameInstanceId, string userId)
        => await _context.HiredCharacters
            .Where(h => h.GameInstanceId == gameInstanceId && h.UserId == userId)
            .OrderBy(h => h.HiredAt)
            .ToListAsync();
}
