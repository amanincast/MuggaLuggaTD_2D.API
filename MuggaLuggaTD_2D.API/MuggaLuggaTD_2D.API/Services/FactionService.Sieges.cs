using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// The factions' sieges (<c>docs/design/npc-factions.md</c> §5, phase 3).
///
/// <code>
/// DECLARE ──▶ MUSTER (8h) ──▶ settled by the server at its close
///             the defender reinforces, and may sally out once: BREAK THE SIEGE
/// </code>
///
/// <para>A faction declares on a bordering region worn to the resolve gate, a player's or (phase 4)
/// another faction's, marching
/// <see cref="FactionStrengthRules.SiegeShare"/> of its strength. Nobody fights its assault, so when the
/// muster closes the server settles it (<see cref="FactionSiegeRules.Settle"/>): falling, the region goes
/// to the faction wrecked with its garrison captured; failing, the faction loses the march and is
/// Bloodied. Settlement waits while a sortie the defender began is still being fought.</para>
///
/// <para>Players see these sieges as they see each other's: in the same list, broadcast as the same
/// <c>SiegeUpdated</c>, with <see cref="SiegeResponse.AttackerFaction"/> set.</para>
/// </summary>
public partial class FactionService
{
    /// <summary>The live siege states, for queries.</summary>
    private static bool Live(FactionSiege s) => s.State == SiegeState.Mustering;

    // -----------------------------------------------------------------
    // Declaring (from a faction's turn)
    // -----------------------------------------------------------------

    /// <summary>
    /// Picks a bordering region, a player's or another faction's, whose resolve is worn to the gate and
    /// whose gate the siege march clears, and declares on it. Null when nothing is ripe. Not saved; the
    /// caller saves and records.
    /// </summary>
    private async Task<FactionSiege?> BesiegeAsync(
        GameInstance instance, FactionState row, List<FactionState> rows, List<WorldRegionData> regions, DateTime now)
    {
        if (SiegeRules.SeasonIsClosing(now, instance.SeasonEndsAt)) return null;

        var besieged = await BesiegedRegionsAsync(instance.Id);
        var cooldownSince = now - SiegeRules.RedeclareCooldown;
        var cooling = await _context.FactionSieges
            .Where(s => s.GameInstanceId == instance.Id && s.Faction == row.Faction
                        && s.State == SiegeState.Repelled && s.ResolvedAt > cooldownSince)
            .Select(s => s.RegionId)
            .ToListAsync();

        double march = FactionSiegeRules.SiegeMarch(row.Strength);
        var candidates = new List<FactionRaidTarget>();
        foreach (var region in FactionDecisionRules.Bordering(row.Faction, regions))
        {
            if (!FactionSiegeRules.IsBesiegeable(row.Faction, region, now)) continue;
            if (besieged.Contains(region.RegionId) || cooling.Contains(region.RegionId)) continue;

            long hold = DefendingHold(region, regions, rows, march, now);
            candidates.Add(new FactionRaidTarget { Region = region, Hold = hold, March = march });
        }

        var pick = FactionSiegeRules.PickSiegeTarget(candidates, _random.NextDouble());
        if (pick == null) return null;

        var defender = pick.Value.Region;
        row.LastActedAtUtc = now;
        return new FactionSiege
        {
            GameInstanceId = instance.Id,
            SeasonNumber = instance.SeasonNumber,
            Faction = row.Faction,
            RegionId = pick.Value.Region.RegionId,
            DefenderUserId = string.IsNullOrEmpty(defender.OwnerUserId) ? WarLogPrefix + defender.Faction : defender.OwnerUserId,
            March = Math.Round(march),
            State = SiegeState.Mustering,
            DeclaredAt = now,
            MusterEndsAt = now + SiegeRules.Muster
        };
    }

    /// <summary>
    /// A region's hold against a faction's march: a Bloodied faction's land holds at a quarter less
    /// (<see cref="FactionDecisionRules.DefendingHold"/>), a player's as it stands.
    /// </summary>
    private static long DefendingHold(WorldRegionData region, IReadOnlyCollection<WorldRegionData> regions,
        List<FactionState> rows, double march, DateTime now)
    {
        long hold = RegionHoldCalculator.AssessRegion(region, regions, march).Hold;
        if (!string.IsNullOrEmpty(region.OwnerUserId)) return hold;
        var defender = rows.FirstOrDefault(r => r.Faction == region.Faction);
        return FactionDecisionRules.DefendingHold(hold, defender != null && FactionStrengthRules.IsBloodied(defender.BloodiedUntilUtc, now));
    }

    /// <summary>The name a siege's defender goes by in the war log: null for a player (the log looks them up).</summary>
    private string? DefenderName(FactionSiege siege) =>
        siege.DefenderFaction == FactionId.None ? null : TemperamentOf(siege.DefenderFaction).Name;

    /// <summary>Regions under any live siege, a player's or a faction's: one siege per region.</summary>
    private async Task<HashSet<string>> BesiegedRegionsAsync(Guid gameInstanceId)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        set.UnionWith(await _context.Sieges
            .Where(s => s.GameInstanceId == gameInstanceId && (s.State == SiegeState.Mustering || s.State == SiegeState.Assault))
            .Select(s => s.RegionId).ToListAsync());
        set.UnionWith(await _context.FactionSieges
            .Where(s => s.GameInstanceId == gameInstanceId && s.State == SiegeState.Mustering)
            .Select(s => s.RegionId).ToListAsync());
        return set;
    }

    /// <summary>Whether a faction's siege is mustering against this region (a player may not declare too).</summary>
    public Task<bool> IsBesiegedByFactionAsync(Guid gameInstanceId, string regionId) =>
        _context.FactionSieges.AnyAsync(s => s.GameInstanceId == gameInstanceId && s.RegionId == regionId
                                             && s.State == SiegeState.Mustering);

    /// <summary>Records and announces a siege a faction has just declared (after it is saved).</summary>
    private async Task AnnounceDeclaredAsync(FactionSiege siege)
    {
        await BroadcastSiegeAsync(siege);
        if (_warLog != null)
        {
            await _warLog.RecordAsync(siege.GameInstanceId, WarLogKind.SiegeDeclared, WarLogPrefix + siege.Faction,
                siege.DefenderUserId, siege.RegionId, $"an army of {siege.March:N0}", siege.DeclaredAt,
                actorName: TemperamentOf(siege.Faction).Name, subjectName: DefenderName(siege));
        }
        _sessionLog.Log("FACTION-SIEGE-DECLARE",
            $"instance={siege.GameInstanceId} siege={siege.Id} faction={siege.Faction} region={siege.RegionId} " +
            $"defender={siege.DefenderUserId} march={siege.March:F0} musterEnds={siege.MusterEndsAt:O}");
    }

    // -----------------------------------------------------------------
    // Settling at muster close
    // -----------------------------------------------------------------

    /// <summary>
    /// Settles every faction siege in one realm whose muster has closed (or, with
    /// <paramref name="force"/>, that faction's live siege now). A siege whose sortie is still being
    /// fought waits for its claim, or for the claim's grace to run out. Returns a line for each.
    /// </summary>
    public async Task<List<string>> SettleDueSiegesAsync(Guid gameInstanceId, DateTime utcNow, FactionId? force = null)
    {
        var done = new List<string>();
        var live = await _context.FactionSieges
            .Where(s => s.GameInstanceId == gameInstanceId && s.State == SiegeState.Mustering)
            .ToListAsync();

        var due = live.Where(s => (s.MusterEndsAt <= utcNow || s.Faction == force) && !SortiePending(s, utcNow)).ToList();
        if (due.Count == 0) return done;

        var worldRow = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (worldRow == null || string.IsNullOrEmpty(worldRow.GameData)) return done;
        var world = JsonNode.Parse(worldRow.GameData);
        var regions = WorldRegionBlob.ReadAllRegions(world);
        var rows = await SettleAsync(gameInstanceId, regions, utcNow);

        bool worldChanged = false;
        var settled = new List<(FactionSiege Siege, string? Detail, int ResolveBefore)>();

        foreach (var siege in due)
        {
            // Logged at the muster's close, however late the sweep noticed; a forced close is now.
            var at = siege.MusterEndsAt <= utcNow ? siege.MusterEndsAt : utcNow;
            if (siege.Faction == force && siege.MusterEndsAt > utcNow) siege.MusterEndsAt = utcNow;

            var region = regions.FirstOrDefault(r => r.RegionId == siege.RegionId);
            var node = WorldRegionBlob.FindRegion(world, siege.RegionId);
            bool stillHeld = region != null && (siege.DefenderFaction == FactionId.None
                ? region.IsOwnedByPlayer(siege.DefenderUserId)
                : FactionStrengthRules.Holds(siege.DefenderFaction, region));
            if (region == null || node == null || !stillHeld)
            {
                siege.State = SiegeState.Cancelled;
                siege.ResolvedAt = at;
                settled.Add((siege, "the region changed hands", 0));
                done.Add($"{TemperamentOf(siege.Faction).Name}'s siege of {siege.RegionId} was called off: it changed hands.");
                continue;
            }

            var row = rows.First(r => r.Faction == siege.Faction);
            long hold = DefendingHold(region, regions, rows, siege.March, at);
            var result = FactionSiegeRules.Settle(siege.March, hold, _random.Next(1, 21));
            siege.FrozenHold = hold;
            siege.D20Roll = result.D20Roll;
            siege.ResolvedAt = at;
            row.Strength = Math.Max(0, row.Strength - FactionSiegeRules.SiegeCost(siege.March, result.Fell));
            worldChanged = true;

            if (result.Fell)
            {
                siege.State = SiegeState.Won;
                siege.Captured = WorldRegionBlob.CaptureWreckedForFaction(node, siege.Faction, TemperamentOf(siege.Faction).Name, at);
                settled.Add((siege, siege.Captured > 0 ? $"{siege.Captured} champion{(siege.Captured == 1 ? "" : "s")} taken prisoner" : null, region.Resolve));
                done.Add($"{TemperamentOf(siege.Faction).Name} took {siege.RegionId} (march {siege.March:N0} vs hold {hold:N0}, " +
                         $"d20 {result.D20Roll}, {siege.Captured} captured).");
            }
            else
            {
                siege.State = SiegeState.Repelled;
                row.BloodiedUntilUtc = at + FactionStrengthRules.BloodiedFor;
                int after = WorldRegionBlob.SetResolve(node, region.Resolve + SiegeAssaultRules.RepelResolveBonus);
                string detail = result.BelowGate
                    ? $"the walls outgrew it; resolve {region.Resolve} → {after}"
                    : $"resolve {region.Resolve} → {after}";
                settled.Add((siege, detail, region.Resolve));
                done.Add($"{TemperamentOf(siege.Faction).Name}'s siege of {siege.RegionId} failed " +
                         $"(march {siege.March:N0} vs hold {hold:N0}{(result.BelowGate ? ", below the gate" : $", d20 {result.D20Roll}")}).");
            }

            // The region's map changed: the next siege in this pass reads it as it now is.
            regions = WorldRegionBlob.ReadAllRegions(world);
        }

        if (worldChanged)
        {
            worldRow.GameData = world!.ToJsonString();
            worldRow.UpdatedAt = DateTime.UtcNow;
        }
        await _context.SaveChangesAsync();
        if (worldChanged) await BroadcastWorldAsync(worldRow);

        foreach (var (siege, detail, _) in settled)
        {
            await BroadcastSiegeAsync(siege);
            var kind = siege.State switch
            {
                SiegeState.Won => WarLogKind.SiegeWon,
                SiegeState.Repelled => WarLogKind.SiegeRepelled,
                _ => WarLogKind.SiegeCancelled
            };
            if (_warLog != null)
            {
                await _warLog.RecordAsync(gameInstanceId, kind, WarLogPrefix + siege.Faction, siege.DefenderUserId,
                    siege.RegionId, detail, siege.ResolvedAt, actorName: TemperamentOf(siege.Faction).Name,
                    subjectName: DefenderName(siege));
            }
            if (siege.State == SiegeState.Repelled && siege.DefenderFaction == FactionId.None && _seasons != null)
                await _seasons.AwardAsync(gameInstanceId, siege.DefenderUserId, SeasonDeed.SiegeRepelled);

            _sessionLog.Log("FACTION-SIEGE-SETTLE",
                $"instance={gameInstanceId} siege={siege.Id} faction={siege.Faction} region={siege.RegionId} " +
                $"state={siege.State} march={siege.March:F0} frozenHold={siege.FrozenHold} d20={siege.D20Roll} captured={siege.Captured}");
        }

        return done;
    }

    /// <summary>The scheduler's minute sweep: settle every realm's faction sieges whose muster has closed.</summary>
    public async Task<int> SettleAllDueSiegesAsync(DateTime? utcNow = null, ILogger? logger = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var realms = await _context.FactionSieges
            .Where(s => s.State == SiegeState.Mustering && s.MusterEndsAt <= now)
            .Select(s => s.GameInstanceId).Distinct().ToListAsync();

        int settled = 0;
        foreach (var realm in realms)
        {
            try
            {
                settled += (await SettleDueSiegesAsync(realm, now)).Count;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Settling faction sieges failed in realm {Realm}.", realm);
                _context.ChangeTracker.Clear();
            }
        }
        return settled;
    }

    /// <summary>A sortie begun and not yet reported, still inside the grace a claim gets.</summary>
    private static bool SortiePending(FactionSiege siege, DateTime now) =>
        siege.SortieRunId != null && siege.SortieWon == null && siege.SortieStartedAt.HasValue
        && now < siege.SortieStartedAt.Value + SiegeAssaultRules.ClaimGrace;

    // -----------------------------------------------------------------
    // Break the siege: the defender's sortie
    // -----------------------------------------------------------------

    /// <summary>
    /// The defender sallies out against a faction's siege, once, during its muster. The server prices
    /// the party from the saved roster and hands back the fight (<see cref="FactionSiegeRules.SortieEncounter"/>).
    /// </summary>
    public async Task<(SiegeOutcome Outcome, SiegeAssaultResponse? Sortie)> BeginSortieAsync(
        Guid gameInstanceId, string userId, Guid siegeId, FactionSortieRequest request, DateTime? utcNow = null)
    {
        if (!string.Equals(request.SharedContractVersion, MuggaLuggaTD.Shared.SharedContract.Version, StringComparison.Ordinal))
        {
            return (new SiegeOutcome(SiegeError.ContractMismatch, Message:
                $"Client gameplay rules v{request.SharedContractVersion} do not match the server's " +
                $"v{MuggaLuggaTD.Shared.SharedContract.Version}. Update the game to break a siege."), null);
        }

        var now = utcNow ?? DateTime.UtcNow;
        await SettleDueSiegesAsync(gameInstanceId, now);

        var siege = await _context.FactionSieges.FirstOrDefaultAsync(s => s.Id == siegeId && s.GameInstanceId == gameInstanceId);
        if (siege == null)
            return (new SiegeOutcome(SiegeError.SiegeNotFound, Message: "No such siege in this realm."), null);
        if (!string.Equals(siege.DefenderUserId, userId, StringComparison.Ordinal))
            return (new SiegeOutcome(SiegeError.NotDefender, Message: "Only the defender may sally out."), null);
        if (!Live(siege))
            return (new SiegeOutcome(SiegeError.WrongState, Message: "This siege is over."), null);
        if (siege.SortieRunId != null)
            return (new SiegeOutcome(SiegeError.AssaultSpent, Message: "You have already sallied out against this siege. A siege gets one sortie."), null);
        if (_content == null)
            return (new SiegeOutcome(SiegeError.WorldNotFound, Message: "Game content is not loaded."), null);

        var world = JsonNode.Parse((await _context.WorldViewGameData.AsNoTracking()
            .FirstAsync(w => w.GameInstanceId == gameInstanceId)).GameData);
        var army = await MarchingArmy.MusterAsync(_context, _content, (ILogger?)_logger ?? NullLogger.Instance,
            gameInstanceId, userId, world, request.ArmyCharacterIds);
        if (army.CharacterIds.Count == 0)
        {
            return (new SiegeOutcome(SiegeError.NoArmy, Message:
                "None of those champions can sally out. They may be garrisoned, captured or besieging elsewhere."), null);
        }

        var encounter = FactionSiegeRules.SortieEncounter(army.Power, siege.March, army.CharacterIds.Count);
        siege.SortieRunId = Guid.NewGuid();
        siege.SortieStartedAt = now;
        siege.SortieArmyJson = MarchingArmy.WriteIds(army.CharacterIds);
        siege.SortiePower = Math.Round(army.Power);
        siege.SortieEnemyLevel = encounter.EnemyLevel;
        siege.SortieWaves = encounter.Waves;
        await _context.SaveChangesAsync();

        _sessionLog.Log("FACTION-SORTIE",
            $"instance={gameInstanceId} siege={siege.Id} faction={siege.Faction} defender={userId} region={siege.RegionId} " +
            $"run={siege.SortieRunId} power={army.Power:F0} march={siege.March:F0} level={encounter.EnemyLevel} " +
            $"waves={encounter.Waves} captains={encounter.EliteCount}");
        await BroadcastSiegeAsync(siege);

        return (new SiegeOutcome(SiegeError.None, ToSiegeResponse(siege)), new SiegeAssaultResponse(
            siege.Id, siege.SortieRunId.Value, siege.RegionId, encounter.EnemyLevel, encounter.Waves, encounter.EliteCount,
            (long)Math.Round(siege.March), Math.Round(army.Power), now + SiegeAssaultRules.ClaimGrace, army.CharacterIds));
    }

    /// <summary>
    /// The defender reports the sortie. Won, the siege is broken: the faction loses its march and is
    /// Bloodied, and the region's resolve rises as for any siege held. Lost, the party comes home
    /// Bloodied and the siege goes on to its muster's close.
    /// </summary>
    public async Task<(SiegeOutcome Outcome, SiegeAssaultResult? Result)> ClaimSortieAsync(
        Guid gameInstanceId, string userId, Guid siegeId, SiegeAssaultClaimRequest request, DateTime? utcNow = null)
    {
        if (!string.Equals(request.SharedContractVersion, MuggaLuggaTD.Shared.SharedContract.Version, StringComparison.Ordinal))
            return (new SiegeOutcome(SiegeError.ContractMismatch, Message: "Update the game to report a sortie."), null);

        var now = utcNow ?? DateTime.UtcNow;
        var siege = await _context.FactionSieges.FirstOrDefaultAsync(s => s.Id == siegeId && s.GameInstanceId == gameInstanceId);
        if (siege == null)
            return (new SiegeOutcome(SiegeError.SiegeNotFound, Message: "No such siege in this realm."), null);
        if (!string.Equals(siege.DefenderUserId, userId, StringComparison.Ordinal))
            return (new SiegeOutcome(SiegeError.NotDefender, Message: "Only the defender may report a sortie."), null);
        if (siege.SortieRunId == null || siege.SortieRunId != request.RunId)
            return (new SiegeOutcome(SiegeError.RunMismatch, Message: "That is not this siege's sortie."), null);
        if (!Live(siege) || siege.SortieWon != null)
            return (new SiegeOutcome(SiegeError.WrongState, Message: "This siege is already decided."), null);
        if (siege.SortieStartedAt.HasValue && now > siege.SortieStartedAt.Value + SiegeAssaultRules.ClaimGrace)
            return (new SiegeOutcome(SiegeError.WrongState, Message: "The sortie was reported too late to count."), null);
        if (request.Won && siege.SortieStartedAt.HasValue && now - siege.SortieStartedAt.Value < WorldPveService.MinimumRunDuration)
            return (new SiegeOutcome(SiegeError.TooFast, Message: "That sortie ended faster than it could be fought."), null);

        var army = MarchingArmy.ReadIds(siege.SortieArmyJson);
        if (!request.Won)
        {
            siege.SortieWon = false;
            await AutoFightService.BloodyAsync(_context, gameInstanceId, userId, army, now);
            await _context.SaveChangesAsync();
            _sessionLog.Log("FACTION-SORTIE-LOST", $"instance={gameInstanceId} siege={siege.Id} faction={siege.Faction} defender={userId}");
            await BroadcastSiegeAsync(siege);

            // The muster may already have closed while the party was out: settle it now rather than
            // leaving it to the next sweep.
            await SettleDueSiegesAsync(gameInstanceId, now);
            return (new SiegeOutcome(SiegeError.None, ToSiegeResponse(siege)),
                new SiegeAssaultResult(siege.Id, siege.RegionId, "Held", 0, 0));
        }

        var worldRow = await _context.WorldViewGameData.FirstAsync(w => w.GameInstanceId == gameInstanceId);
        var world = JsonNode.Parse(worldRow.GameData);
        var regions = WorldRegionBlob.ReadAllRegions(world);
        var rows = await SettleAsync(gameInstanceId, regions, now);
        var row = rows.First(r => r.Faction == siege.Faction);
        var node = WorldRegionBlob.FindRegion(world, siege.RegionId);

        siege.SortieWon = true;
        siege.State = SiegeState.Repelled;
        siege.ResolvedAt = now;
        row.Strength = Math.Max(0, row.Strength - FactionSiegeRules.SiegeCost(siege.March, fell: false));
        row.BloodiedUntilUtc = now + FactionStrengthRules.BloodiedFor;

        string? detail = null;
        if (node != null)
        {
            int before = WorldRegionBlob.ReadRegion(node).Resolve;
            int after = WorldRegionBlob.SetResolve(node, before + SiegeAssaultRules.RepelResolveBonus);
            detail = $"resolve {before} → {after}";
            worldRow.GameData = world!.ToJsonString();
            worldRow.UpdatedAt = DateTime.UtcNow;
        }
        await _context.SaveChangesAsync();
        if (node != null) await BroadcastWorldAsync(worldRow);
        await BroadcastSiegeAsync(siege);

        _sessionLog.Log("FACTION-SORTIE-WON",
            $"instance={gameInstanceId} siege={siege.Id} faction={siege.Faction} defender={userId} region={siege.RegionId} " +
            $"elapsed={(now - (siege.SortieStartedAt ?? now)).TotalSeconds:F0}s strength->{row.Strength:F0}");
        if (_warLog != null)
        {
            await _warLog.RecordAsync(gameInstanceId, WarLogKind.SiegeBroken, userId, WarLogPrefix + siege.Faction,
                siege.RegionId, detail, now, subjectName: TemperamentOf(siege.Faction).Name);
        }
        if (_seasons != null) await _seasons.AwardAsync(gameInstanceId, userId, SeasonDeed.SiegeRepelled);

        return (new SiegeOutcome(SiegeError.None, ToSiegeResponse(siege)),
            new SiegeAssaultResult(siege.Id, siege.RegionId, "Broken", SeasonScoreRules.PointsFor(SeasonDeed.SiegeRepelled), 0));
    }

    // -----------------------------------------------------------------
    // Reading and telling
    // -----------------------------------------------------------------

    /// <summary>Every live faction siege in the realm, settled to now, as players see sieges.</summary>
    public async Task<List<SiegeResponse>> LiveSiegesAsync(Guid gameInstanceId, DateTime? utcNow = null)
    {
        await SettleDueSiegesAsync(gameInstanceId, utcNow ?? DateTime.UtcNow);
        var live = await _context.FactionSieges.AsNoTracking()
            .Where(s => s.GameInstanceId == gameInstanceId && s.State == SiegeState.Mustering)
            .OrderBy(s => s.DeclaredAt)
            .ToListAsync();
        return live.Select(ToSiegeResponse).ToList();
    }

    /// <summary>
    /// A faction's siege in the shape of a player's: the attacker id is "faction:{name}", and with no
    /// assault window its assault "ends" when the muster does.
    /// </summary>
    public SiegeResponse ToSiegeResponse(FactionSiege siege) => new(
        siege.Id,
        siege.RegionId,
        WarLogPrefix + siege.Faction,
        TemperamentOf(siege.Faction).Name,
        siege.DefenderUserId,
        siege.State.ToString(),
        siege.DeclaredAt,
        siege.MusterEndsAt,
        siege.MusterEndsAt,
        Math.Round(siege.March),
        siege.FrozenHold,
        siege.ResolvedAt,
        new List<string>(),
        AssaultBegun: false,
        AttackerFaction: siege.Faction.ToString(),
        Broken: siege.Broken,
        SortieBegun: siege.SortieRunId != null);

    private async Task BroadcastSiegeAsync(FactionSiege siege)
    {
        if (_hub == null) return;
        await _hub.Clients.Group(siege.GameInstanceId.ToString()).SendAsync("SiegeUpdated", ToSiegeResponse(siege));
    }

    private async Task BroadcastWorldAsync(WorldViewGameData worldRow)
    {
        if (_hub == null) return;
        var payload = JsonSerializer.Deserialize<object>(worldRow.GameData) ?? new { };
        await _hub.Clients.Group(worldRow.GameInstanceId.ToString())
            .SendAsync("WorldViewGameDataUpdated", new WorldViewGameDataUpdated(worldRow.GameInstanceId, payload, worldRow.UpdatedAt));
    }
}
