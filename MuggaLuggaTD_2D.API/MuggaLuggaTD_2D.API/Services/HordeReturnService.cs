using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Hubs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// The horde returns (Reclaiming phase 3a, spec §6, Mike 2026-11-16). Swept every minute with the sieges: it stamps
/// when land became unruly, starts a gathering where a cause has stood 12 hours, scatters one whose cause was fixed,
/// rolls the garrison's hold when its 8 hours are up, and sends fallen land nobody reclaimed back to the wild.
/// <see cref="HordeReturnRules"/> decides; this applies it to the world blob and tells the holder by letter.
///
/// <para>Like the factions' sieges, the roll is made at the gathering's close by the sweep, so the hold is the one the
/// holder left standing then. If the sweep stops, nothing gathers and nothing falls: it is the only driver.</para>
/// </summary>
public class HordeReturnService
{
    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<HordeReturnService> _logger;
    private readonly IHubContext<GameHub>? _hub;
    private readonly LetterService? _letters;
    private readonly SeasonScoreService? _seasons;
    private readonly Random _random;

    public HordeReturnService(ApplicationDbContext context, IGameContentProvider content, ISessionLog sessionLog,
        ILogger<HordeReturnService> logger, IHubContext<GameHub>? hub = null, LetterService? letters = null,
        SeasonScoreService? seasons = null, Random? random = null)
    {
        _context = context;
        _content = content;
        _sessionLog = sessionLog;
        _logger = logger;
        _hub = hub;
        _letters = letters;
        _seasons = seasons;
        _random = random ?? Random.Shared;
    }

    /// <summary>What one sweep of one realm did, for the log and for Combat Debug.</summary>
    public sealed record Outcome(List<string> Done, bool WorldChanged);

    /// <summary>The scheduler's minute sweep: every realm with a world.</summary>
    public async Task<int> SweepAllAsync(DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var realms = await _context.WorldViewGameData.AsNoTracking().Select(w => w.GameInstanceId).ToListAsync();
        int done = 0;
        foreach (var realm in realms)
        {
            try
            {
                done += (await SweepAsync(realm, now)).Done.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The horde sweep failed in realm {Realm}; the next one will try again.", realm);
                _context.ChangeTracker.Clear();
            }
        }
        return done;
    }

    /// <summary>
    /// One realm's sweep. <paramref name="force"/> (Combat Debug) acts on one region now: "gather", "scatter",
    /// "hold", "fall" or "lapse".
    /// </summary>
    public async Task<Outcome> SweepAsync(Guid gameInstanceId, DateTime now, string? forceRegionId = null, string? force = null)
    {
        var done = new List<string>();
        var row = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (row == null || string.IsNullOrEmpty(row.GameData)) return new Outcome(done, false);
        var instance = await _context.GameInstances.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        if (instance == null) return new Outcome(done, false);

        var world = JsonNode.Parse(row.GameData);
        var regions = WorldRegionBlob.ReadAllRegions(world);
        var invaded = await InvadedAsync(gameInstanceId, regions);
        var letters = new List<(string UserId, LetterKind Kind, string RegionId, string Subject, string? Detail)>();
        bool changed = false, holdings = false;

        // 1. The unruly stamp (row P3-1): when land lost its steward, kept while it stays so.
        foreach (var region in regions)
        {
            long stamp = HordeReturnRules.UnrulyStamp(region, now);
            if (stamp == region.UnrulySinceUtcTicks) continue;
            var node = WorldRegionBlob.FindRegion(world, region.RegionId);
            if (node == null) continue;
            WorldRegionBlob.SetUnrulySince(node, stamp);
            region.UnrulySinceUtcTicks = stamp;
            changed = true;
        }

        // 2. Live gatherings: called off, scattered by a fix, or rolled at their close.
        foreach (var region in regions.Where(HordeReturnRules.IsGathering).ToList())
        {
            var node = WorldRegionBlob.FindRegion(world, region.RegionId);
            if (node == null) continue;
            var gathering = region.Gathering!;
            string subject = gathering.StartedAtUtcTicks.ToString();
            bool forced = forceRegionId == region.RegionId;

            if (HordeReturnRules.CalledOff(region))
            {
                WorldRegionBlob.EndGathering(node);
                changed = true;
                done.Add($"The gathering at {region.RegionId} was called off: the land changed hands.");
                continue;
            }

            bool stands = HordeReturnRules.CauseStands(region, invaded.ContainsKey(region.RegionId));
            if ((!stands && !(forced && (force == "hold" || force == "fall"))) || (forced && force == "scatter"))
            {
                WorldRegionBlob.EndGathering(node);
                changed = true;
                letters.Add((gathering.UserId!, LetterKind.HordeScattered, region.RegionId, subject, "fixed"));
                done.Add($"The horde at {region.RegionId} scattered: its cause was put right.");
                _sessionLog.Log("HORDE-SCATTER", $"instance={gameInstanceId} region={region.RegionId} user={gathering.UserId} cause={gathering.Cause} why=fixed");
                continue;
            }

            bool due = HordeReturnRules.StrikesAt(region) <= now || (forced && (force == "hold" || force == "fall"));
            if (!due) continue;

            double patrol = await PatrolPowerAsync(gameInstanceId, gathering.UserId!, region.RegionId, world);
            long hold = HordeReturnRules.Hold(region, regions, patrol);
            double strength = HordeReturnRules.Strength(region.Tier);
            var result = HordeReturnRules.Settle(strength, hold, _random.Next(1, 21));
            bool fell = forced && force == "fall" || !(forced && force == "hold") && result.Fell;
            var at = forced ? now : HordeReturnRules.StrikesAt(region);

            if (fell)
            {
                int home = WorldRegionBlob.Fall(node, region.Tier, at);
                letters.Add((gathering.UserId!, LetterKind.RegionFell, region.RegionId, at.Ticks.ToString(), $"{home}"));
                done.Add($"{region.RegionId} fell to the horde (strength {strength:N0} vs hold {hold:N0}, patrol {patrol:N0}, " +
                         $"{(result.BelowGate ? "below the gate" : $"d20 {result.D20Roll}")}); {home} came home.");
                holdings = true;
            }
            else
            {
                WorldRegionBlob.EndGathering(node, at + HordeReturnRules.SafeFor);
                letters.Add((gathering.UserId!, LetterKind.HordeScattered, region.RegionId, subject, "held"));
                done.Add($"{region.RegionId} held against the horde (strength {strength:N0} vs hold {hold:N0}, patrol {patrol:N0}, " +
                         $"{(result.BelowGate ? "below the gate" : $"d20 {result.D20Roll}")}).");
            }
            changed = true;
            _sessionLog.Log("HORDE-ROLL", $"instance={gameInstanceId} region={region.RegionId} user={gathering.UserId} cause={gathering.Cause} " +
                                          $"strength={strength:F0} hold={hold} patrol={patrol:F0} d20={result.D20Roll} belowGate={result.BelowGate} fell={fell}");
        }

        // 3. Fallen land nobody reclaimed in three days goes back to the wild (row P3-6).
        regions = WorldRegionBlob.ReadAllRegions(world);
        foreach (var region in regions)
        {
            bool forced = forceRegionId == region.RegionId && force == "lapse";
            if (!HordeReturnRules.ShouldLapse(region, now) && !(forced && HordeRules.IsReturned(region) && HordeRules.IsOverrun(region))) continue;
            var node = WorldRegionBlob.FindRegion(world, region.RegionId);
            if (node == null) continue;
            string owner = region.OwnerUserId ?? string.Empty;
            WorldRegionBlob.Lapse(node, region.Tier);
            letters.Add((owner, LetterKind.RegionLapsed, region.RegionId, region.Horde!.FellAtUtcTicks.ToString(), null));
            done.Add($"{region.RegionId} went back to the wild: nobody reclaimed it.");
            _sessionLog.Log("HORDE-LAPSE", $"instance={gameInstanceId} region={region.RegionId} user={owner}");
            changed = holdings = true;
        }

        // 4. New gatherings (rows P3-1, P3-9): one per player, none in the season's last day.
        regions = WorldRegionBlob.ReadAllRegions(world);
        var starts = HordeReturnRules.ToGather(regions, now, invaded, FactionSiegeRules.SeasonIsClosing(now, instance.SeasonEndsAt));
        if (force == "gather" && forceRegionId != null && starts.All(s => s.Region.RegionId != forceRegionId))
        {
            // Combat Debug: gather here now, whatever the cause and the limits, as long as the land can be struck.
            var target = regions.FirstOrDefault(r => r.RegionId == forceRegionId);
            if (target != null && HordeReturnRules.CanBeStruck(target) && !HordeReturnRules.IsGathering(target))
            {
                var cause = invaded.ContainsKey(target.RegionId) ? HordeCause.Invaded : HordeCause.Unruly;
                starts.RemoveAll(s => s.Region.OwnerUserId == target.OwnerUserId);
                starts.Add((target, cause));
            }
        }
        foreach (var (region, cause) in starts)
        {
            var node = WorldRegionBlob.FindRegion(world, region.RegionId);
            if (node == null) continue;
            var gathering = HordeReturnRules.NewGathering(region.OwnerUserId!, cause, now);
            WorldRegionBlob.StartGathering(node, gathering);
            letters.Add((region.OwnerUserId!, LetterKind.HordeGathers, region.RegionId, gathering.StartedAtUtcTicks.ToString(), cause.ToString()));
            done.Add($"A horde gathers at {region.RegionId} ({cause}); it strikes at {HordeReturnRules.StrikesAt(WorldRegionBlob.ReadRegion(node)):HH:mm} UTC.");
            _sessionLog.Log("HORDE-GATHER", $"instance={gameInstanceId} region={region.RegionId} user={region.OwnerUserId} cause={cause}");
            changed = true;
        }

        if (!changed) return new Outcome(done, false);

        row.GameData = world!.ToJsonString();
        row.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        // Fallen or lapsed land changes what its holder earns (as a siege's fall does).
        if (holdings && _seasons != null) await _seasons.SettleAllAsync(gameInstanceId, world, now);
        await BroadcastWorldAsync(row);

        if (_letters != null)
        {
            foreach (var (userId, kind, regionId, subject, detail) in letters)
            {
                if (string.IsNullOrEmpty(userId)) continue;
                await _letters.SendAsync(gameInstanceId, userId, kind, now, $"horde:{kind}:{regionId}:{subject}",
                    regionId: regionId, subjectId: subject, detail: detail);
            }
        }
        foreach (var line in done) _logger.LogInformation("Horde sweep, realm {Realm}: {Line}", gameInstanceId, line);
        return new Outcome(done, true);
    }

    /// <summary>
    /// The regions a Lesser rift still invades, and since when (row P3-1). Only an invasion of the land's present holder
    /// counts: a rift that invaded someone else's land before it changed hands is not this holder's neglect.
    /// </summary>
    private async Task<Dictionary<string, DateTime>> InvadedAsync(Guid gameInstanceId, IReadOnlyCollection<WorldRegionData> regions)
    {
        var rifts = await _context.Rifts.AsNoTracking()
            .Where(r => r.GameInstanceId == gameInstanceId && r.Kind == RiftKind.Lesser && r.Stage == RiftStage.Invaded && r.InvadedAt != null)
            .Select(r => new { r.RegionId, r.TargetUserId, r.InvadedAt })
            .ToListAsync();
        var byId = regions.ToDictionary(r => r.RegionId, StringComparer.Ordinal);
        var invaded = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var rift in rifts)
        {
            if (!byId.TryGetValue(rift.RegionId, out var region) || !region.IsOwnedByPlayer(rift.TargetUserId)) continue;
            var at = DateTime.SpecifyKind(rift.InvadedAt!.Value, DateTimeKind.Utc);
            if (!invaded.TryGetValue(rift.RegionId, out var earlier) || at < earlier) invaded[rift.RegionId] = at;
        }
        return invaded;
    }

    /// <summary>
    /// The power of the holder's companies patrolling the region (row P3-10), priced from the roster as a march is. A
    /// patrol that has stopped (out of provisions, outmatched) guards nothing.
    /// </summary>
    private async Task<double> PatrolPowerAsync(Guid gameInstanceId, string userId, string regionId, JsonNode? world)
    {
        var patrols = await _context.PlayerParties.AsNoTracking()
            .Where(p => p.GameInstanceId == gameInstanceId && p.UserId == userId && p.AutoMode
                        && p.AutoOrder == AutoOrder.Patrol && p.AutoRegionId == regionId)
            .ToListAsync();
        var ids = patrols.Where(p => AutoFightRules.Guards(p.AutoOrder, p.AutoStatus))
            .SelectMany(p => MarchingArmy.ReadIds(p.CharacterIdsJson)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return 0;
        var muster = await MarchingArmy.MusterAsync(_context, _content, _logger, gameInstanceId, userId, world, ids);
        return muster.Power;
    }

    private async Task BroadcastWorldAsync(WorldViewGameData row)
    {
        if (_hub == null) return;
        var payload = JsonSerializer.Deserialize<object>(row.GameData) ?? new { };
        await _hub.Clients.Group(row.GameInstanceId.ToString())
            .SendAsync("WorldViewGameDataUpdated", new WorldViewGameDataUpdated(row.GameInstanceId, payload, row.UpdatedAt));
    }
}
