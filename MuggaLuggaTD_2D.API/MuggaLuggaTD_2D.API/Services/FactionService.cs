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
/// The NPC factions (<c>docs/design/npc-factions.md</c>): their manpower, and what they do with it.
///
/// <para><b>Strength</b> (phase 1). A faction's row is made the first time anything reads it, <b>at
/// full strength</b>: a faction seated by world generation has stood on its land since before anyone
/// arrived. From then on it is settled lazily against the cap its land supports now
/// (<see cref="FactionStrengthRules.Settle"/>), so a faction that has lost land is no stronger than
/// what is left. A ransom paid to a faction is banked as its strength.</para>
///
/// <para><b>Acting</b> (phase 2). <see cref="SiegeScheduler"/> gives every realm's factions a turn
/// every <see cref="FactionDecisionRules.SweepInterval"/> (<see cref="ActAllAsync"/>). A faction that
/// is ready and not Bloodied may act, by chance and temperament (<c>GameContent/Server/FactionData.json</c>).
/// So far the one action built is the <b>raid</b>: on a region bordering its land, held by a player
/// or by another faction, through the same <see cref="RaidResolver"/> a player's raid uses. It takes
/// resolve only, costs the faction a tenth of its march, or half and Bloodied if repelled.</para>
/// </summary>
public class FactionService
{
    /// <summary>The prefix a faction's id wears in the war log, where a user id would be.</summary>
    public const string WarLogPrefix = "faction:";

    private readonly ApplicationDbContext _context;
    private readonly ISessionLog _sessionLog;
    private readonly IGameContentProvider? _content;
    private readonly WarLogService? _warLog;
    private readonly SeasonScoreService? _seasons;
    private readonly IHubContext<GameHub>? _hub;
    private readonly Random _random;

    public FactionService(
        ApplicationDbContext context,
        ISessionLog sessionLog,
        IGameContentProvider? content = null,
        WarLogService? warLog = null,
        SeasonScoreService? seasons = null,
        IHubContext<GameHub>? hub = null,
        Random? random = null)
    {
        _context = context;
        _sessionLog = sessionLog;
        _content = content;
        _warLog = warLog;
        _seasons = seasons;
        _hub = hub;
        _random = random ?? Random.Shared;
    }

    /// <summary>Every faction in the realm, settled to now. Null when the realm has no world.</summary>
    public async Task<FactionsResponse?> ReadAsync(Guid gameInstanceId, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var regions = await RegionsAsync(gameInstanceId);
        if (regions == null) return null;

        var rows = await SettleAsync(gameInstanceId, regions, now);
        await _context.SaveChangesAsync();
        return Describe(rows, regions, now);
    }

    /// <summary>
    /// A ransom paid for heroes <paramref name="faction"/> holds, banked as its strength (Mike,
    /// 2026-10-06). Returns the strength after, or null when the realm has no world.
    /// </summary>
    public async Task<double?> BankRansomAsync(Guid gameInstanceId, FactionId faction, long gold, DateTime? utcNow = null)
    {
        if (!FactionStrengthRules.IsFaction(faction) || gold <= 0) return null;
        var now = utcNow ?? DateTime.UtcNow;
        var regions = await RegionsAsync(gameInstanceId);
        if (regions == null) return null;

        var rows = await SettleAsync(gameInstanceId, regions, now);
        var row = rows.First(r => r.Faction == faction);
        double before = row.Strength;
        row.Strength = FactionStrengthRules.BankRansom(row.Strength, gold, FactionStrengthRules.Cap(faction, regions));
        await _context.SaveChangesAsync();

        _sessionLog.Log("FACTION-RANSOM",
            $"instance={gameInstanceId} faction={faction} gold={gold} strength={before:F0}->{row.Strength:F0}");
        return row.Strength;
    }

    /// <summary>
    /// The Combat Debug window's faction controls. An empty or unknown <c>Faction</c> applies to every
    /// faction. <c>SimulateHours</c> lets that much time pass, as if nobody had looked, <b>with the
    /// factions taking their turns</b> through it; <c>ForceAct</c> makes the faction act now, whatever
    /// its readiness and lean.
    /// </summary>
    public async Task<FactionsResponse?> DebugAsync(Guid gameInstanceId, FactionDebugRequest request, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var regions = await RegionsAsync(gameInstanceId);
        if (regions == null) return null;

        var rows = await SettleAsync(gameInstanceId, regions, now);
        bool all = !Enum.TryParse<FactionId>(request.Faction, ignoreCase: true, out var only) || !FactionStrengthRules.IsFaction(only);
        var report = new List<string>();

        if (request.SimulateHours is > 0)
        {
            // Wind every clock back rather than forward, then play the sweeps from there to now: the
            // factions refill and act through those hours, and the war log reads as if they had.
            var span = TimeSpan.FromHours(request.SimulateHours.Value);
            foreach (var row in rows)
            {
                row.SettledAtUtc -= span;
                if (row.BloodiedUntilUtc.HasValue) row.BloodiedUntilUtc -= span;
            }
            await _context.SaveChangesAsync();

            for (var at = now - span + FactionDecisionRules.SweepInterval; at <= now; at += FactionDecisionRules.SweepInterval)
                report.AddRange(await ActAsync(gameInstanceId, at));

            regions = await RegionsAsync(gameInstanceId) ?? regions;
            rows = await SettleAsync(gameInstanceId, regions, now);
        }

        foreach (var row in rows.Where(r => all || r.Faction == only))
        {
            double cap = FactionStrengthRules.Cap(row.Faction, regions);

            if (request.Readiness.HasValue)
                row.Strength = Math.Clamp(request.Readiness.Value, 0, 1) * cap;

            if (request.Bloody)
                row.BloodiedUntilUtc = now + FactionStrengthRules.BloodiedFor;

            if (request.ClearBloodied)
                row.BloodiedUntilUtc = null;

            _sessionLog.Log("FACTION-DEBUG",
                $"instance={gameInstanceId} faction={row.Faction} strength={row.Strength:F0}/{cap:F0} " +
                $"bloodiedUntil={row.BloodiedUntilUtc:O} request={request}");
        }
        await _context.SaveChangesAsync();

        if (request.ForceAct)
        {
            foreach (var faction in FactionStrengthRules.All.Where(f => all || f == only))
                report.AddRange(await ActAsync(gameInstanceId, now, force: faction));
            regions = await RegionsAsync(gameInstanceId) ?? regions;
            rows = await SettleAsync(gameInstanceId, regions, now);
            await _context.SaveChangesAsync();
        }

        var response = Describe(rows, regions, now);
        return response with { Report = report.Count > 0 ? report : new List<string> { "Nothing happened." } };
    }

    /// <summary>
    /// Every realm's factions take their turn (<see cref="SiegeScheduler"/>). Returns how many acted.
    /// One realm failing is logged and does not stop the rest.
    /// </summary>
    public async Task<int> ActAllAsync(DateTime? utcNow = null, ILogger? logger = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var realms = await _context.WorldViewGameData.AsNoTracking().Select(w => w.GameInstanceId).ToListAsync();

        int acted = 0;
        foreach (var realm in realms)
        {
            try
            {
                acted += (await ActAsync(realm, now)).Count;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Faction turn failed in realm {Realm}.", realm);
                _context.ChangeTracker.Clear();
            }
        }
        return acted;
    }

    /// <summary>
    /// One turn for the factions of one realm, at <paramref name="utcNow"/>. Each faction may act by
    /// chance and temperament; <paramref name="force"/> makes that one act now (a raid, if it can
    /// beat anyone) whatever its readiness. Returns a line for each thing done.
    /// </summary>
    public async Task<List<string>> ActAsync(Guid gameInstanceId, DateTime utcNow, FactionId? force = null)
    {
        var done = new List<string>();
        var worldRow = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (worldRow == null || string.IsNullOrEmpty(worldRow.GameData)) return done;

        var world = JsonNode.Parse(worldRow.GameData);
        var regions = WorldRegionBlob.ReadAllRegions(world);
        if (regions.Count == 0) return done;

        var rows = await SettleAsync(gameInstanceId, regions, utcNow);
        var since = utcNow - RaidResolver.Cooldown;
        var recent = await _context.FactionRaids
            .Where(r => r.GameInstanceId == gameInstanceId && r.RaidedAt > since)
            .ToListAsync();

        bool worldChanged = false;
        var lines = new List<(WarLogKind Kind, FactionRaid Raid, string Detail)>();

        foreach (var row in rows.OrderBy(_ => _random.Next()))
        {
            var temperament = TemperamentOf(row.Faction);
            double cap = FactionStrengthRules.Cap(row.Faction, regions);
            if (cap <= 0) continue;

            bool forced = force == row.Faction;
            bool bloodied = FactionStrengthRules.IsBloodied(row.BloodiedUntilUtc, utcNow);
            FactionAction action;
            if (forced)
            {
                action = FactionAction.Raid;
            }
            else
            {
                double chance = FactionDecisionRules.ChanceToAct(
                    FactionStrengthRules.Readiness(row.Strength, cap), temperament.Aggression, bloodied);
                if (_random.NextDouble() >= chance) continue;
                action = FactionDecisionRules.PickAction(temperament, _random.NextDouble());
            }

            if (action != FactionAction.Raid)
            {
                // A lean toward something not built yet: the turn passes, and it still counts as acting.
                row.LastActedAtUtc = utcNow;
                done.Add($"{temperament.Name} kept to its own land ({action}, not built yet).");
                continue;
            }

            var raid = Raid(gameInstanceId, row, temperament, regions, rows, recent, world, utcNow);
            if (raid == null)
            {
                if (forced) done.Add($"{temperament.Name} found nobody on its border it could beat.");
                continue;
            }

            recent.Add(raid);
            _context.FactionRaids.Add(raid);
            worldChanged |= raid.ResolveDamage > 0;
            string detail = raid.AttackerWon ? $"resolve {raid.ResolveAfter + raid.ResolveDamage} → {raid.ResolveAfter}" : string.Empty;
            lines.Add((raid.AttackerWon ? WarLogKind.RaidLanded : WarLogKind.RaidRepelled, raid, detail));
            done.Add($"{temperament.Name} raided {raid.RegionId} ({(raid.AttackerWon ? "landed" : "repelled")}, " +
                     $"march {raid.March:F0} vs hold {raid.Hold:N0}{(raid.AttackerWon ? ", " + detail : "")}).");
        }

        if (worldChanged)
        {
            worldRow.GameData = world!.ToJsonString();
            worldRow.UpdatedAt = DateTime.UtcNow;
        }
        await _context.SaveChangesAsync();

        if (worldChanged && _hub != null)
        {
            var payload = JsonSerializer.Deserialize<object>(worldRow.GameData) ?? new { };
            await _hub.Clients.Group(gameInstanceId.ToString())
                .SendAsync("WorldViewGameDataUpdated", new WorldViewGameDataUpdated(gameInstanceId, payload, worldRow.UpdatedAt));
        }

        foreach (var (kind, raid, detail) in lines)
        {
            string? subject = raid.DefenderUserId ?? WarLogPrefix + raid.DefenderFaction;
            string? subjectName = raid.DefenderUserId == null ? TemperamentOf(raid.DefenderFaction).Name : null;
            if (_warLog != null)
            {
                await _warLog.RecordAsync(gameInstanceId, kind, WarLogPrefix + raid.Faction, subject, raid.RegionId,
                    string.IsNullOrEmpty(detail) ? null : detail, raid.RaidedAt,
                    actorName: TemperamentOf(raid.Faction).Name, subjectName: subjectName);
            }

            // A player who held off a warband earns what holding off a rival does.
            if (!raid.AttackerWon && raid.DefenderUserId != null && _seasons != null)
                await _seasons.AwardAsync(gameInstanceId, raid.DefenderUserId, SeasonDeed.RaidRepelled);

            _sessionLog.Log("FACTION-RAID",
                $"instance={gameInstanceId} faction={raid.Faction} region={raid.RegionId} " +
                $"defender={raid.DefenderUserId ?? raid.DefenderFaction.ToString()} won={raid.AttackerWon} " +
                $"march={raid.March:F0} hold={raid.Hold} resolve->{raid.ResolveAfter}");
        }

        return done;
    }

    /// <summary>
    /// Picks a bordering region the faction can beat and raids it. Applies resolve to the world
    /// node and the cost to the faction; the caller records it. Null when nothing on the border
    /// clears the raid bar, or every such region is on cooldown.
    /// </summary>
    private FactionRaid? Raid(
        Guid gameInstanceId, FactionState row, FactionTemperament temperament,
        List<WorldRegionData> regions, List<FactionState> rows, List<FactionRaid> recent, JsonNode? world, DateTime now)
    {
        double march = FactionDecisionRules.RaidMarch(row.Strength);
        var candidates = new List<FactionRaidTarget>();

        foreach (var region in FactionDecisionRules.Bordering(row.Faction, regions))
        {
            if (!FactionDecisionRules.IsRaidable(row.Faction, region, now)) continue;
            if (recent.Any(r => r.Faction == row.Faction && r.RegionId == region.RegionId && r.RaidedAt + RaidResolver.Cooldown > now))
                continue;

            long hold = RegionHoldCalculator.AssessRegion(region, regions, march).Hold;
            var defender = string.IsNullOrEmpty(region.OwnerUserId) ? rows.FirstOrDefault(r => r.Faction == region.Faction) : null;
            hold = FactionDecisionRules.DefendingHold(hold, defender != null && FactionStrengthRules.IsBloodied(defender.BloodiedUntilUtc, now));
            candidates.Add(new FactionRaidTarget { Region = region, Hold = hold, March = march });
        }

        var pick = FactionDecisionRules.PickRaidTarget(candidates, _random.NextDouble());
        if (pick == null) return null;

        var target = pick.Value;
        var result = RaidResolver.Resolve(march, target.Hold, target.Region.Resolve, _random.Next(1, 21));

        if (result.ResolveDamage > 0)
        {
            var node = WorldRegionBlob.FindRegion(world, target.Region.RegionId);
            if (node != null) WorldRegionBlob.SetResolve(node, result.ResolveAfter);
            target.Region.Resolve = result.ResolveAfter;
        }

        row.Strength = Math.Max(0, row.Strength - FactionDecisionRules.RaidCost(march, result.AttackerWins));
        row.LastActedAtUtc = now;
        if (!result.AttackerWins)
            row.BloodiedUntilUtc = now + FactionStrengthRules.BloodiedFor;

        return new FactionRaid
        {
            GameInstanceId = gameInstanceId,
            Faction = row.Faction,
            RegionId = target.Region.RegionId,
            DefenderUserId = string.IsNullOrEmpty(target.Region.OwnerUserId) ? null : target.Region.OwnerUserId,
            DefenderFaction = string.IsNullOrEmpty(target.Region.OwnerUserId) ? target.Region.Faction : FactionId.None,
            RaidedAt = now,
            AttackerWon = result.AttackerWins,
            March = Math.Round(march),
            Hold = target.Hold,
            ResolveDamage = result.ResolveDamage,
            ResolveAfter = result.ResolveAfter
        };
    }

    /// <summary>A new season's map seats its factions anew, so their rows and raids go with the old one.</summary>
    public static Task ResetRealmAsync(ApplicationDbContext context, Guid realmId)
    {
        context.FactionStates.RemoveRange(context.FactionStates.Where(f => f.GameInstanceId == realmId));
        context.FactionRaids.RemoveRange(context.FactionRaids.Where(f => f.GameInstanceId == realmId));
        return Task.CompletedTask;
    }

    /// <summary>The faction's temperament from content, or a plain one if content has none for it.</summary>
    public FactionTemperament TemperamentOf(FactionId faction) =>
        _content?.Factions?.FirstOrDefault(f => f.Id == faction)
        ?? new FactionTemperament { Id = faction, Name = faction.ToString() };

    /// <summary>Each faction's row, made if missing and settled to <paramref name="now"/>. Not saved.</summary>
    private async Task<List<FactionState>> SettleAsync(Guid gameInstanceId, IReadOnlyCollection<WorldRegionData> regions, DateTime now)
    {
        var rows = await _context.FactionStates.Where(f => f.GameInstanceId == gameInstanceId).ToListAsync();

        foreach (var faction in FactionStrengthRules.All)
        {
            double cap = FactionStrengthRules.Cap(faction, regions);
            var row = rows.FirstOrDefault(r => r.Faction == faction);
            if (row == null)
            {
                row = new FactionState
                {
                    GameInstanceId = gameInstanceId,
                    Faction = faction,
                    Strength = cap,
                    SettledAtUtc = now
                };
                _context.FactionStates.Add(row);
                rows.Add(row);
                continue;
            }

            if (now <= row.SettledAtUtc) continue;
            row.Strength = FactionStrengthRules.Settle(row.Strength, row.SettledAtUtc, now, cap, row.BloodiedUntilUtc);
            row.SettledAtUtc = now;
        }

        return rows;
    }

    private FactionsResponse Describe(List<FactionState> rows, IReadOnlyCollection<WorldRegionData> regions, DateTime now)
    {
        var list = new List<FactionStrengthResponse>();
        foreach (var faction in FactionStrengthRules.All)
        {
            var row = rows.First(r => r.Faction == faction);
            double cap = FactionStrengthRules.Cap(faction, regions);
            bool bloodied = FactionStrengthRules.IsBloodied(row.BloodiedUntilUtc, now);
            list.Add(new FactionStrengthResponse(
                faction.ToString(),
                (long)Math.Round(Math.Min(row.Strength, cap)),
                (long)Math.Round(cap),
                Math.Round(FactionStrengthRules.Readiness(row.Strength, cap), 3),
                FactionStrengthRules.Word(row.Strength, cap, bloodied).ToString(),
                FactionStrengthRules.RegionsHeld(faction, regions),
                bloodied ? row.BloodiedUntilUtc : null,
                TemperamentOf(faction).Lean));
        }
        return new FactionsResponse(list, now);
    }

    private async Task<List<WorldRegionData>?> RegionsAsync(Guid gameInstanceId)
    {
        var row = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (row == null || string.IsNullOrEmpty(row.GameData)) return null;
        var regions = WorldRegionBlob.ReadAllRegions(JsonNode.Parse(row.GameData));
        return regions.Count == 0 ? null : regions;
    }
}
