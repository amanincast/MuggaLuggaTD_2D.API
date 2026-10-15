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
///
/// <para><b>Sieges</b> (phase 3) are in <c>FactionService.Sieges.cs</c>. A faction leaning toward a
/// siege with nothing ripe on its border raids instead, which is what ripens one.</para>
///
/// <para><b>Growing</b> (phase 4): a faction may <b>expand</b> into wild hard country on its border,
/// or <b>fortify</b> its most threatened land (<see cref="FactionGrowthRules"/>). One with no wild land
/// left to claim fortifies instead. It raids and besieges other factions as it does players.</para>
/// </summary>
public partial class FactionService
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
    private readonly ILogger<FactionService>? _logger;

    public FactionService(
        ApplicationDbContext context,
        ISessionLog sessionLog,
        IGameContentProvider? content = null,
        WarLogService? warLog = null,
        SeasonScoreService? seasons = null,
        IHubContext<GameHub>? hub = null,
        Random? random = null,
        ILogger<FactionService>? logger = null)
    {
        _logger = logger;
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
        if (await RegionsAsync(gameInstanceId) == null) return null;
        await SettleDueSiegesAsync(gameInstanceId, now);
        var regions = (await RegionsAsync(gameInstanceId))!;

        var rows = await SettleAsync(gameInstanceId, regions, now);
        await _context.SaveChangesAsync();
        return Describe(rows, regions, now, await MusteringAsync(gameInstanceId));
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
        row.Strength = FactionStrengthRules.BankRansom(row.Strength, gold, CapOf(row, regions));
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
            foreach (var siege in await _context.FactionSieges
                         .Where(s => s.GameInstanceId == gameInstanceId && s.State == SiegeState.Mustering).ToListAsync())
            {
                siege.DeclaredAt -= span;
                siege.MusterEndsAt -= span;
                if (siege.SortieStartedAt.HasValue) siege.SortieStartedAt -= span;
            }
            await _context.SaveChangesAsync();

            for (var at = now - span + FactionDecisionRules.SweepInterval; at <= now; at += FactionDecisionRules.SweepInterval)
                report.AddRange(await ActAsync(gameInstanceId, at));

            regions = await RegionsAsync(gameInstanceId) ?? regions;
            rows = await SettleAsync(gameInstanceId, regions, now);
        }

        foreach (var row in rows.Where(r => all || r.Faction == only))
        {
            double cap = CapOf(row, regions);

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

        if (request.CloseMuster)
        {
            foreach (var faction in InPlay(regions).Where(f => all || f == only))
            {
                var lines = await SettleDueSiegesAsync(gameInstanceId, now, force: faction);
                report.AddRange(lines.Count > 0 ? lines : new List<string> { $"{TemperamentOf(faction).Name} has no siege mustering." });
            }
            regions = await RegionsAsync(gameInstanceId) ?? regions;
            rows = await SettleAsync(gameInstanceId, regions, now);
            await _context.SaveChangesAsync();
        }

        if (request.ForceAct)
        {
            var action = Enum.TryParse<FactionAction>(request.Action, ignoreCase: true, out var asked) && asked != FactionAction.None
                ? asked : FactionAction.Raid;
            bool signature = string.Equals(request.Action, "Signature", StringComparison.OrdinalIgnoreCase);
            foreach (var faction in InPlay(regions).Where(f => all || f == only))
                report.AddRange(signature
                    ? await FireSignatureAsync(gameInstanceId, faction, now)
                    : await ActAsync(gameInstanceId, now, force: faction, forceAction: action));
            regions = await RegionsAsync(gameInstanceId) ?? regions;
            rows = await SettleAsync(gameInstanceId, regions, now);
            await _context.SaveChangesAsync();
        }

        var response = Describe(rows, regions, now, await MusteringAsync(gameInstanceId));
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
    /// One turn for the factions of one realm, at <paramref name="utcNow"/>. Sieges whose muster has
    /// closed are settled first. Then each faction may act by chance and temperament;
    /// <paramref name="force"/> makes that one act now with <paramref name="forceAction"/> (a raid by
    /// default), whatever its readiness. Returns a line for each thing done.
    /// </summary>
    public async Task<List<string>> ActAsync(Guid gameInstanceId, DateTime utcNow, FactionId? force = null,
        FactionAction forceAction = FactionAction.Raid)
    {
        var done = await SettleDueSiegesAsync(gameInstanceId, utcNow);
        var instance = await _context.GameInstances.FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        var worldRow = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (instance == null || worldRow == null || string.IsNullOrEmpty(worldRow.GameData)) return done;

        var world = JsonNode.Parse(worldRow.GameData);
        var regions = WorldRegionBlob.ReadAllRegions(world);
        if (regions.Count == 0) return done;

        var rows = await SettleAsync(gameInstanceId, regions, utcNow);
        // Far enough back to tell whether the Horned Kin have stampeded today, as well as what is on cooldown.
        var since = utcNow - FactionDecisionRules.RaidCooldown - FactionSignatureRules.StampedeEvery;
        var recent = await _context.FactionRaids
            .Where(r => r.GameInstanceId == gameInstanceId && r.RaidedAt > since)
            .ToListAsync();

        bool worldChanged = false;
        var lines = new List<(WarLogKind Kind, FactionRaid Raid, string Detail)>();
        var grown = new List<(WarLogKind Kind, FactionId Faction, string RegionId, string? Detail)>();
        var declared = new List<FactionSiege>();
        var mustering = await MusteringAsync(gameInstanceId);
        var besieged = await BesiegedRegionsAsync(gameInstanceId);

        // A faction beside an open Great Rift strikes at it now and then (row 7), win or lose its own turn.
        await ChipTheRiftAsync(gameInstanceId, rows, regions, utcNow, done);

        // The Bone Synod's dead rise against whoever took their land (their signature), before anyone's turn.
        worldChanged |= RaiseTheDead(gameInstanceId, rows, regions, world, recent, lines, done, utcNow);

        foreach (var row in rows.OrderBy(_ => _random.Next()))
        {
            var temperament = TemperamentOf(row.Faction);
            double cap = CapOf(row, regions);
            if (cap <= 0) continue;

            bool forced = force == row.Faction;
            bool bloodied = FactionStrengthRules.IsBloodied(row.BloodiedUntilUtc, utcNow);
            if (mustering.TryGetValue(row.Faction, out var musteringAgainst))
            {
                // One army: while its siege musters, a faction does nothing else.
                if (forced) done.Add($"{temperament.Name} is mustering against {musteringAgainst}; it does nothing else.");
                continue;
            }

            FactionAction action;
            if (forced)
            {
                action = forceAction;
            }
            else
            {
                double chance = FactionDecisionRules.ChanceToAct(
                    FactionStrengthRules.Readiness(row.Strength, cap), temperament.Aggression, bloodied);
                if (_random.NextDouble() >= chance) continue;
                action = FactionDecisionRules.PickAction(temperament, _random.NextDouble());
            }

            if (action == FactionAction.Siege)
            {
                var siege = await BesiegeAsync(instance, row, rows, regions, utcNow);
                if (siege != null)
                {
                    _context.FactionSieges.Add(siege);
                    declared.Add(siege);
                    besieged.Add(siege.RegionId);
                    mustering[row.Faction] = siege.RegionId;
                    done.Add($"{temperament.Name} laid siege to {siege.RegionId} (march {siege.March:N0}; " +
                             $"the muster closes {siege.MusterEndsAt:HH:mm} UTC).");
                    continue;
                }

                // Nothing ripe for a siege: it raids instead, which is what ripens one.
                if (forced)
                {
                    done.Add($"{temperament.Name} found nothing on its border ripe for a siege " +
                             $"(resolve {SiegeRules.DeclareResolveThreshold} or below, and a gate its march clears).");
                    continue;
                }
                action = FactionAction.Raid;
            }

            if (action == FactionAction.Expand)
            {
                var claimed = Expand(row, temperament, regions, world, utcNow);
                if (claimed != null)
                {
                    worldChanged = true;
                    grown.Add((WarLogKind.Expanded, row.Faction, claimed.RegionId, null));
                    done.Add($"{temperament.Name} claimed {claimed.RegionId} (tier {claimed.Tier}; strength now {row.Strength:N0}).");
                    continue;
                }

                // No wild land left on its border: it tends what it has instead.
                if (forced)
                {
                    done.Add($"{temperament.Name} found no wild land on its border to claim " +
                             $"(tier {FactionGrowthRules.ExpandMinimumTier}+, touching no capital).");
                    continue;
                }
                action = FactionAction.Fortify;
            }

            if (action == FactionAction.Fortify)
            {
                var works = Fortify(row, regions, world, besieged, utcNow);
                if (works != null)
                {
                    var (region, resolveBefore) = works.Value;
                    worldChanged = true;
                    grown.Add((WarLogKind.Fortified, row.Faction, region.RegionId,
                        RegionHoldCalculator.EntrenchmentLabel(region.Entrenchment)));
                    done.Add($"{temperament.Name} fortified {region.RegionId} (walls {RegionHoldCalculator.EntrenchmentLabel(region.Entrenchment)}, " +
                             $"resolve {resolveBefore} → {region.Resolve}; strength now {row.Strength:N0}).");
                }
                else if (forced)
                {
                    done.Add($"{temperament.Name} has nothing to fortify: its land is whole, or under siege.");
                }
                continue;
            }

            if (action != FactionAction.Raid) continue;

            var struck = Raid(gameInstanceId, row, temperament, regions, rows, recent, world, utcNow);
            if (struck == null)
            {
                if (forced) done.Add($"{temperament.Name} found nobody on its border it could beat.");
                continue;
            }

            var (raid, stampede) = struck.Value;
            worldChanged |= Record(raid, temperament, stampede ? "a stampede" : null, recent, lines, done);

            // The Grimjaw's warband rides on from a raid that landed, at half its march (their signature).
            if (raid.AttackerWon && FactionSignatureRules.Has(row.Faction, FactionSignature.Warband))
            {
                var onward = Raid(gameInstanceId, row, temperament, regions, rows, recent, world, utcNow,
                    march: FactionSignatureRules.WarbandMarch(raid.March), exclude: raid.RegionId);
                if (onward != null)
                    worldChanged |= Record(onward.Value.Raid, temperament, "the warband rode on", recent, lines, done);
                else if (forced)
                    done.Add($"{temperament.Name}'s warband found nobody else on its border it could beat.");
            }
        }

        if (worldChanged)
        {
            worldRow.GameData = world!.ToJsonString();
            worldRow.UpdatedAt = DateTime.UtcNow;
        }
        await _context.SaveChangesAsync();

        // Land that changed hands, or walls raised, change what everyone earns: settle the scoreboard (and
        // gold) at the old rates first, as every other land change does. Without it a player away from the
        // game went on earning for a region a faction had taken until they next looked.
        if (worldChanged && _seasons != null) await _seasons.SettleAllAsync(gameInstanceId, world, utcNow);

        if (worldChanged && _hub != null)
        {
            var payload = JsonSerializer.Deserialize<object>(worldRow.GameData) ?? new { };
            await _hub.Clients.Group(gameInstanceId.ToString())
                .SendAsync("WorldViewGameDataUpdated", new WorldViewGameDataUpdated(gameInstanceId, payload, worldRow.UpdatedAt));
        }

        foreach (var siege in declared)
            await AnnounceDeclaredAsync(siege);

        foreach (var (kind, faction, regionId, detail) in grown)
        {
            if (_warLog != null)
            {
                await _warLog.RecordAsync(gameInstanceId, kind, WarLogPrefix + faction, null, regionId, detail, utcNow,
                    actorName: TemperamentOf(faction).Name);
            }
            _sessionLog.Log(kind == WarLogKind.Expanded ? "FACTION-EXPAND" : "FACTION-FORTIFY",
                $"instance={gameInstanceId} faction={faction} region={regionId} detail={detail}");
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
    /// <param name="march">What it marches with; its usual raid march when null (the Grimjaw's warband rides on with less).</param>
    /// <param name="exclude">A region it may not pick (the one the warband rode on from).</param>
    /// <returns>The raid, and whether it was a Horned Kin stampede (a region on cooldown, once a day).</returns>
    private (FactionRaid Raid, bool Stampede)? Raid(
        Guid gameInstanceId, FactionState row, FactionTemperament temperament,
        List<WorldRegionData> regions, List<FactionState> rows, List<FactionRaid> recent, JsonNode? world, DateTime now,
        double? march = null, string? exclude = null)
    {
        double marching = march ?? FactionDecisionRules.RaidMarch(row.Strength);
        bool mayStampede = FactionSignatureRules.Has(row.Faction, FactionSignature.Stampede) &&
                           FactionSignatureRules.MayStampede(OwnRaids(recent, row.Faction), now, FactionDecisionRules.RaidCooldown);
        var candidates = new List<FactionRaidTarget>();

        foreach (var region in FactionDecisionRules.Bordering(row.Faction, regions))
        {
            if (region.RegionId == exclude || !FactionDecisionRules.IsRaidable(row.Faction, region, now)) continue;
            if (!mayStampede && Cooling(recent, row.Faction, region.RegionId, now)) continue;

            candidates.Add(new FactionRaidTarget { Region = region, Hold = DefendingHold(region, regions, rows, marching, now), March = marching });
        }

        var pick = FactionDecisionRules.PickRaidTarget(candidates, _random.NextDouble());
        if (pick == null) return null;

        var target = pick.Value;
        bool stampede = Cooling(recent, row.Faction, target.Region.RegionId, now);
        return (Strike(gameInstanceId, row, target.Region, target.Hold, marching, world, now, charged: true), stampede);
    }

    /// <summary>Whether the faction raided this region less than a cooldown ago.</summary>
    private static bool Cooling(List<FactionRaid> recent, FactionId faction, string regionId, DateTime now) =>
        recent.Any(r => r.Faction == faction && r.RegionId == regionId && r.RaidedAt + FactionDecisionRules.RaidCooldown > now);

    private static List<(string RegionId, DateTime At)> OwnRaids(List<FactionRaid> recent, FactionId faction) =>
        recent.Where(r => r.Faction == faction).Select(r => (r.RegionId, r.RaidedAt)).ToList();

    /// <summary>
    /// One raid on <paramref name="region"/>: rolls it, applies resolve to the world node and the region, and, when
    /// <paramref name="charged"/>, its cost (and a loss's Bloodied) to the faction. The caller records it.
    /// </summary>
    private FactionRaid Strike(Guid gameInstanceId, FactionState row, WorldRegionData region, long hold, double march,
        JsonNode? world, DateTime now, bool charged)
    {
        var result = RaidResolver.Resolve(march, hold, region.Resolve, _random.Next(1, 21));

        if (result.ResolveDamage > 0)
        {
            var node = WorldRegionBlob.FindRegion(world, region.RegionId);
            if (node != null) WorldRegionBlob.SetResolve(node, result.ResolveAfter);
            region.Resolve = result.ResolveAfter;
        }

        if (charged)
        {
            row.Strength = Math.Max(0, row.Strength - FactionDecisionRules.RaidCost(march, result.AttackerWins));
            row.LastActedAtUtc = now;
            if (!result.AttackerWins)
                row.BloodiedUntilUtc = now + FactionStrengthRules.BloodiedFor;
        }

        return new FactionRaid
        {
            GameInstanceId = gameInstanceId,
            Faction = row.Faction,
            RegionId = region.RegionId,
            DefenderUserId = string.IsNullOrEmpty(region.OwnerUserId) ? null : region.OwnerUserId,
            DefenderFaction = string.IsNullOrEmpty(region.OwnerUserId) ? region.Faction : FactionId.None,
            RaidedAt = now,
            AttackerWon = result.AttackerWins,
            March = Math.Round(march),
            Hold = hold,
            ResolveDamage = result.ResolveDamage,
            ResolveAfter = result.ResolveAfter
        };
    }

    /// <summary>
    /// Records a raid for this turn: saved, on the cooldown list, in the war log (with <paramref name="how"/>, a
    /// signature's name, ahead of the resolve) and in the report. Returns whether the world changed.
    /// </summary>
    private bool Record(FactionRaid raid, FactionTemperament temperament, string? how, List<FactionRaid> recent,
        List<(WarLogKind Kind, FactionRaid Raid, string Detail)> lines, List<string> done)
    {
        recent.Add(raid);
        _context.FactionRaids.Add(raid);
        string resolve = raid.AttackerWon ? $"resolve {raid.ResolveAfter + raid.ResolveDamage} → {raid.ResolveAfter}" : string.Empty;
        string detail = string.Join("; ", new[] { how, resolve }.Where(s => !string.IsNullOrEmpty(s)));
        lines.Add((raid.AttackerWon ? WarLogKind.RaidLanded : WarLogKind.RaidRepelled, raid, detail));
        done.Add($"{temperament.Name} raided {raid.RegionId} ({(raid.AttackerWon ? "landed" : "repelled")}, " +
                 $"march {raid.March:F0} vs hold {raid.Hold:N0}{(string.IsNullOrEmpty(detail) ? "" : ", " + detail)}).");
        return raid.ResolveDamage > 0;
    }

    /// <summary>
    /// The Bone Synod's signature (<see cref="FactionSignature.TheDeadRise"/>): each region they lost in the last day
    /// raids its new holder once, with the march a raid of theirs would have, truce or cooldown or none. It costs them
    /// nothing and a loss does not leave them Bloodied: the dead were spent already. Marks too old, on land they have
    /// taken back, or of a faction no longer in play are cleared. Returns whether the world changed.
    /// </summary>
    private bool RaiseTheDead(Guid gameInstanceId, List<FactionState> rows, List<WorldRegionData> regions, JsonNode? world,
        List<FactionRaid> recent, List<(WarLogKind Kind, FactionRaid Raid, string Detail)> lines, List<string> done, DateTime now)
    {
        var risen = WorldRegionBlob.Risen(world);
        if (risen.Count == 0) return false;

        var dead = rows.FirstOrDefault(r => FactionSignatureRules.Has(r.Faction, FactionSignature.TheDeadRise));
        foreach (var (regionId, node, lostAt) in risen)
        {
            WorldRegionBlob.ClearRisen(node);
            var region = regions.FirstOrDefault(r => r.RegionId == regionId);
            if (dead == null || region == null || !FactionSignatureRules.MayRise(lostAt, now) ||
                !FactionSignatureRules.MayRaidBack(dead.Faction, region))
                continue;

            double march = FactionDecisionRules.RaidMarch(dead.Strength);
            long hold = DefendingHold(region, regions, rows, march, now);
            var raid = Strike(gameInstanceId, dead, region, hold, march, world, now, charged: false);
            Record(raid, TemperamentOf(dead.Faction), "the dead rose", recent, lines, done);
        }
        return true;
    }

    /// <summary>
    /// Claims wild land on the faction's border (<see cref="FactionGrowthRules.PickExpansion"/>) and
    /// charges it. Applies to the world node and to <paramref name="regions"/>, so the rest of this
    /// turn sees the new border. Null when there is none to claim.
    /// </summary>
    private WorldRegionData? Expand(FactionState row, FactionTemperament temperament, List<WorldRegionData> regions,
        JsonNode? world, DateTime now)
    {
        var pick = FactionGrowthRules.PickExpansion(row.Faction, regions, _random.NextDouble());
        var node = pick == null ? null : WorldRegionBlob.FindRegion(world, pick.RegionId);
        if (pick == null || node == null) return null;

        WorldRegionBlob.ClaimForFaction(node, row.Faction, temperament.Name, now);
        pick.Ownership = LocationOwnership.Enemy;
        pick.OwnerUserId = string.Empty;
        pick.OwnerDisplayName = temperament.Name;
        pick.Faction = row.Faction;

        row.Strength = Math.Max(0, row.Strength - FactionGrowthRules.ExpandCost(row.Strength));
        row.LastActedAtUtc = now;
        return pick;
    }

    /// <summary>
    /// Fortifies the faction's most threatened region (<see cref="FactionGrowthRules.PickFortify"/>):
    /// walls up a level, resolve restored by a step. Returns the region and its resolve before, or null
    /// when nothing needs it.
    /// </summary>
    private (WorldRegionData Region, int ResolveBefore)? Fortify(FactionState row, List<WorldRegionData> regions,
        JsonNode? world, ICollection<string> besieged, DateTime now)
    {
        var pick = FactionGrowthRules.PickFortify(row.Faction, regions, besieged);
        var node = pick == null ? null : WorldRegionBlob.FindRegion(world, pick.RegionId);
        if (pick == null || node == null) return null;

        int resolveBefore = pick.Resolve;
        var (entrenchment, resolve) = FactionGrowthRules.Fortified(pick, FactionSignatureRules.FortifyResolveBonus(row.Faction));
        WorldRegionBlob.SetEntrenchment(node, entrenchment);
        pick.Entrenchment = entrenchment;
        pick.Resolve = WorldRegionBlob.SetResolve(node, resolve);

        row.Strength = Math.Max(0, row.Strength - FactionGrowthRules.FortifyCost(row.Strength));
        row.LastActedAtUtc = now;
        return (pick, resolveBefore);
    }

    /// <summary>A new season's map seats its factions anew, so their rows and raids go with the old one.</summary>
    public static Task ResetRealmAsync(ApplicationDbContext context, Guid realmId)
    {
        context.FactionStates.RemoveRange(context.FactionStates.Where(f => f.GameInstanceId == realmId));
        context.FactionRaids.RemoveRange(context.FactionRaids.Where(f => f.GameInstanceId == realmId));
        context.FactionSieges.RemoveRange(context.FactionSieges.Where(f => f.GameInstanceId == realmId));
        return Task.CompletedTask;
    }

    /// <summary>The faction's temperament from content, or a plain one if content has none for it.</summary>
    public FactionTemperament TemperamentOf(FactionId faction) =>
        _content?.Factions?.FirstOrDefault(f => f.Id == faction)
        ?? new FactionTemperament { Id = faction, Name = FactionCatalog.TitleOf(faction) };

    /// <summary>A faction's cap: what its land supports, raised by any surges (the Riftborn's, Invasions R8).</summary>
    private static double CapOf(FactionState row, IReadOnlyCollection<WorldRegionData> regions) =>
        FactionStrengthRules.Cap(row.Faction, regions, row.Surges);

    /// <summary>The world's cast, as the last <see cref="RegionsAsync"/> read it; the old pair until then.</summary>
    private IReadOnlyList<FactionId>? _cast;

    /// <summary>The factions in play: the world's cast, and any other holding land (<see cref="FactionCatalog.InPlay"/>).</summary>
    private IReadOnlyList<FactionId> InPlay(IReadOnlyCollection<WorldRegionData>? regions) => FactionCatalog.InPlay(_cast, regions);

    /// <summary>Each faction's row, made if missing and settled to <paramref name="now"/>. Not saved.</summary>
    private async Task<List<FactionState>> SettleAsync(Guid gameInstanceId, IReadOnlyCollection<WorldRegionData> regions, DateTime now)
    {
        var rows = await _context.FactionStates.Where(f => f.GameInstanceId == gameInstanceId).ToListAsync();

        foreach (var faction in InPlay(regions))
        {
            var row = rows.FirstOrDefault(r => r.Faction == faction);
            double cap = row == null ? FactionStrengthRules.Cap(faction, regions) : CapOf(row, regions);
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

    /// <summary>The region each faction's live siege musters against.</summary>
    private async Task<Dictionary<FactionId, string>> MusteringAsync(Guid gameInstanceId)
    {
        var live = await _context.FactionSieges
            .Where(s => s.GameInstanceId == gameInstanceId && s.State == SiegeState.Mustering)
            .Select(s => new { s.Faction, s.RegionId })
            .ToListAsync();
        var map = new Dictionary<FactionId, string>();
        foreach (var s in live) map[s.Faction] = s.RegionId;
        return map;
    }

    private FactionsResponse Describe(List<FactionState> rows, IReadOnlyCollection<WorldRegionData> regions, DateTime now,
        IReadOnlyDictionary<FactionId, string> mustering)
    {
        var list = new List<FactionStrengthResponse>();
        foreach (var faction in InPlay(regions))
        {
            var row = rows.First(r => r.Faction == faction);
            double cap = CapOf(row, regions);
            bool bloodied = FactionStrengthRules.IsBloodied(row.BloodiedUntilUtc, now);
            list.Add(new FactionStrengthResponse(
                faction.ToString(),
                (long)Math.Round(Math.Min(row.Strength, cap)),
                (long)Math.Round(cap),
                Math.Round(FactionStrengthRules.Readiness(row.Strength, cap), 3),
                FactionStrengthRules.Word(row.Strength, cap, bloodied, mustering.ContainsKey(faction)).ToString(),
                FactionStrengthRules.RegionsHeld(faction, regions),
                bloodied ? row.BloodiedUntilUtc : null,
                TemperamentOf(faction).Lean,
                mustering.TryGetValue(faction, out var against) ? against : null));
        }
        return new FactionsResponse(list, now);
    }

    private async Task<List<WorldRegionData>?> RegionsAsync(Guid gameInstanceId)
    {
        var row = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (row == null || string.IsNullOrEmpty(row.GameData)) return null;
        var world = JsonNode.Parse(row.GameData);
        _cast = WorldRegionBlob.ReadCast(world);
        var regions = WorldRegionBlob.ReadAllRegions(world);
        return regions.Count == 0 ? null : regions;
    }
}
