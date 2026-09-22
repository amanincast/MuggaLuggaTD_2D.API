using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>Why a raid was refused, so the controller can pick the right status code.</summary>
public enum RaidError
{
    None = 0,
    WorldNotFound,
    RegionNotFound,
    NotRaidable,
    NoAttackers,
    BelowRaidBar,
    OnCooldown,
    ContractMismatch
}

/// <summary>
/// The resolved raid, plus who was defending it. The defender is carried here rather than in
/// the response because the client does not need it and the scoreboard does: a repelled raid
/// pays the holder, who is very likely not the person who made this request.
/// </summary>
public record RaidOutcome(
    RaidError Error, RegionRaidResponse? Response, string? Message = null, string? DefenderUserId = null)
{
    public bool Succeeded => Error == RaidError.None;
}

/// <summary>
/// Raiding a rival region, server-side (see <c>docs/design/siege.md</c> §3).
///
/// <para>This replaces the passive PvP that preceded it, and the replacement is not a port. The old
/// service resolved one d20 and handed over the holding — which, once the world became regions,
/// would have meant a single roll taking a region. The design rules that out in one sentence:
/// <b>no single fight may be worth a region</b>, because the server cannot verify real-time combat
/// and taking a sleeping player's territory on a forged win is not tolerable.</para>
///
/// <para>So a raid <b>takes nothing</b>. It wears the region's resolve down, resolve multiplies
/// hold, and a worn-down region is cheaper to besiege later. Conquest becomes a campaign of many
/// small, individually cheap, rate-limited actions, every one of them visible to the defender before
/// anything is at stake — and a forged win buys one cooldown's worth of progress.</para>
///
/// <para>What the server owns here is everything: the target's eligibility, the marching party
/// validated against the persisted roster, both power values, the dice, the damage, and the
/// cooldown. The request carries only a region id and a list of character ids.</para>
/// </summary>
public class WorldRaidService
{
    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly ILogger<WorldRaidService> _logger;

    public WorldRaidService(ApplicationDbContext context, IGameContentProvider content, ILogger<WorldRaidService> logger)
    {
        _context = context;
        _content = content;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a raid and, on success, returns the mutated world for the caller to persist and
    /// broadcast — so persistence stays in one place, as it does for PvE.
    /// </summary>
    public async Task<(RaidOutcome Outcome, JsonNode? UpdatedWorld)> RaidAsync(
        Guid gameInstanceId,
        string attackerUserId,
        RegionRaidRequest request)
    {
        if (!string.Equals(request.SharedContractVersion, MuggaLuggaTD.Shared.SharedContract.Version, StringComparison.Ordinal))
        {
            return (Refuse(RaidError.ContractMismatch,
                $"Client gameplay rules v{request.SharedContractVersion} do not match the server's " +
                $"v{MuggaLuggaTD.Shared.SharedContract.Version}. Update the game to raid."), null);
        }

        var worldRow = await _context.WorldViewGameData
            .FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);

        if (worldRow == null)
            return (Refuse(RaidError.WorldNotFound, "World view data not found."), null);

        var world = JsonNode.Parse(worldRow.GameData);
        var regionNode = WorldRegionBlob.FindRegion(world, request.RegionId);
        if (regionNode == null)
            return (Refuse(RaidError.RegionNotFound, "Region not found in this world."), null);

        var allRegions = WorldRegionBlob.ReadAllRegions(world);
        var region = WorldRegionBlob.ReadRegion(regionNode);

        var eligibility = CheckTarget(region, attackerUserId);
        if (!eligibility.Succeeded) return (eligibility, null);

        // The cooldown is checked before the fight, not after, so a player on cooldown is told so
        // rather than marching and losing the attempt.
        var cooldownEndsAt = await CooldownEndsAtAsync(gameInstanceId, attackerUserId, request.RegionId);
        if (cooldownEndsAt > DateTime.UtcNow)
        {
            return (Refuse(RaidError.OnCooldown,
                $"Your forces have raided this region too recently. They can march again at " +
                $"{cooldownEndsAt:HH:mm} UTC."), null);
        }

        // The marching party is validated against the attacker's *persisted* roster, so a client
        // cannot march with champions it does not own, or with ones already committed elsewhere.
        var attackerSave = await LoadPlayerSaveAsync(gameInstanceId, attackerUserId);
        var owned = attackerSave?.Characters?
            .Where(c => c?.Id != null).Select(c => c.Id).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

        var committed = WorldRegionBlob.CollectCommittedCharacterIds(world, attackerUserId);
        var marching = (request.AttackerCharacterIds ?? new List<string>())
            .Where(id => !string.IsNullOrEmpty(id) && owned.Contains(id) && !committed.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (marching.Count == 0)
        {
            return (Refuse(RaidError.NoAttackers,
                "None of the marching characters are available — they may be garrisoned or captured."), null);
        }

        var marchingPower = PartyPowerCalculator.CalculatePartyPower(attackerSave, marching, _content.AbilityTemplates);

        // Hold is recomputed from the live world under the shared rules, so it is the same number the
        // dossier showed the attacker when they decided to march.
        var assessment = RegionHoldCalculator.AssessRegion(region, allRegions, marchingPower);

        if (!RaidResolver.ClearsBar(marchingPower, assessment.Hold))
        {
            return (Refuse(RaidError.BelowRaidBar,
                $"Your march of {marchingPower:F0} falls short of the raid bar of {assessment.RaidBar:N0}."), null);
        }

        // Server-side dice, as everywhere else. The client renders what comes back.
        int roll = Random.Shared.Next(1, 21);
        var result = RaidResolver.Resolve(marchingPower, assessment.Hold, region.Resolve, roll);

        if (result.ResolveDamage > 0)
            WorldRegionBlob.SetResolve(regionNode, result.ResolveAfter);

        var raid = new RegionRaid
        {
            GameInstanceId = gameInstanceId,
            UserId = attackerUserId,
            RegionId = request.RegionId,
            DefenderUserId = region.OwnerUserId,
            RaidedAt = DateTime.UtcNow,
            AttackerWon = result.AttackerWins,
            ResolveDamage = result.ResolveDamage,
            ResolveAfter = result.ResolveAfter
        };

        _context.RegionRaids.Add(raid);
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Raid {Result}: {Attacker} vs region {Region} (march {March:F0} vs hold {Hold}, roll {Roll}{Mod:+#;-#;+0} = {Total}) — resolve {Before} to {After}.",
            result.AttackerWins ? "WIN" : "REPELLED", attackerUserId, request.RegionId,
            marchingPower, assessment.Hold, roll, result.Modifier, result.Total,
            region.Resolve, result.ResolveAfter);

        var response = new RegionRaidResponse(
            request.RegionId,
            result.AttackerWins,
            result.D20Roll,
            result.Modifier,
            result.Total,
            marchingPower,
            assessment.Hold,
            assessment.RaidBar,
            result.ResolveDamage,
            region.Resolve,
            result.ResolveAfter,
            raid.RaidedAt + RaidResolver.Cooldown);

        return (new RaidOutcome(RaidError.None, response, null, region.OwnerUserId), world);
    }

    /// <summary>
    /// When this attacker may next raid this region. In the past — or <see cref="DateTime.MinValue"/>
    /// — when they may raid now.
    /// </summary>
    public async Task<DateTime> CooldownEndsAtAsync(Guid gameInstanceId, string userId, string regionId)
    {
        var last = await _context.RegionRaids
            .Where(r => r.GameInstanceId == gameInstanceId && r.UserId == userId && r.RegionId == regionId)
            .OrderByDescending(r => r.RaidedAt)
            .FirstOrDefaultAsync();

        return last == null ? DateTime.MinValue : last.RaidedAt + RaidResolver.Cooldown;
    }

    /// <summary>
    /// A region may be raided when it belongs to another player and is not their seat.
    ///
    /// <para>Capitals are off the board entirely (design §7). A seat cannot be besieged, so raiding
    /// one could never lead anywhere — wearing down resolve is only worth doing because it makes a
    /// siege possible, and against a capital no siege will ever come. Refusing it here keeps the
    /// cooldown for targets where it means something.</para>
    ///
    /// <para>Neutral and NPC-held land is PvE: those regions are taken by clearing their keep, which
    /// the player can simply go and do.</para>
    /// </summary>
    private static RaidOutcome CheckTarget(WorldRegionData region, string attackerUserId)
    {
        if (region.Ownership != LocationOwnership.Player || string.IsNullOrEmpty(region.OwnerUserId))
        {
            return Refuse(RaidError.NotRaidable,
                "That region is not held by another player — take its keep instead.");
        }

        if (string.Equals(region.OwnerUserId, attackerUserId, StringComparison.Ordinal))
            return Refuse(RaidError.NotRaidable, "That region is already yours.");

        if (region.IsCapital)
            return Refuse(RaidError.NotRaidable, "A player's seat cannot be raided.");

        return new RaidOutcome(RaidError.None, null);
    }

    private static RaidOutcome Refuse(RaidError error, string message) => new(error, null, message);

    private async Task<UserSaveData?> LoadPlayerSaveAsync(Guid gameInstanceId, string userId)
    {
        var row = await _context.PlayerGameData
            .FirstOrDefaultAsync(p => p.GameInstanceId == gameInstanceId && p.UserId == userId);

        if (row == null)
            return null;

        try
        {
            // Parsed with Newtonsoft because the client wrote it with Newtonsoft: System.Text.Json
            // disagrees about values this format legitimately contains (e.g. `50.0` for a long field).
            return Newtonsoft.Json.JsonConvert.DeserializeObject<UserSaveData>(row.GameData);
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "Could not read player save for {UserId} in instance {Instance}.", userId, gameInstanceId);
            return null;
        }
    }
}
