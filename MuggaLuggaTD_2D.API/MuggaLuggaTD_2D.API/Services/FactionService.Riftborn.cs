using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// The Riftborn (Invasions spec R8, phase 6), and the factions' part in a Great Rift (row 7).
///
/// <para><b>Seated by an eruption.</b> No world is generated with them: a Great Rift that nobody seals hands them its
/// region (<see cref="EruptAsync"/>). Wild land becomes theirs at once; held land, a player's or a faction's, gets a
/// Riftborn siege at once, through the same muster and settlement as any faction siege, its army sized by the Breach
/// left. Eruptions hand them land <see cref="RiftRules.EruptionLandCap"/> times a season; past that, an eruption is a
/// <b>surge</b>, raising their cap by a quarter for the rest of the season.</para>
///
/// <para><b>From then on</b> they are a faction like the others (<c>FactionData.json</c>), leaning hard toward
/// expanding into wild land of any tier: a plague, with no cap on their own spreading. Expanding costs them strength,
/// so fighting them back slows it. They are not scored, and a season's reset takes them with the map.</para>
///
/// <para><b>The chip.</b> On every faction turn, a faction holding land on or beside an open Great Rift strikes at it
/// with a chance (<see cref="RiftRules.FactionChipChance"/>), taking a little Breach and paying in strength. It never
/// takes the last point: a rift is sealed by a player's win.</para>
/// </summary>
public partial class FactionService
{
    /// <summary>
    /// Erupts a Great Rift in <paramref name="regionId"/>: seizes, besieges or surges (<see cref="RiftRules.EruptionFor"/>).
    /// <paramref name="landErupted"/> is how many eruptions this season already handed the Riftborn land. Saves,
    /// broadcasts and records what it did; returns it.
    /// </summary>
    public async Task<RiftEruption> EruptAsync(Guid gameInstanceId, string regionId, int breachLeft, int breachMax,
        int landErupted, DateTime at)
    {
        var instance = await _context.GameInstances.FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        var worldRow = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (instance == null || worldRow == null || string.IsNullOrEmpty(worldRow.GameData)) return RiftEruption.None;

        var world = JsonNode.Parse(worldRow.GameData);
        _cast = WorldRegionBlob.ReadCast(world);
        var regions = WorldRegionBlob.ReadAllRegions(world);
        var region = regions.FirstOrDefault(r => r.RegionId == regionId);
        var node = region == null ? null : WorldRegionBlob.FindRegion(world, regionId);
        if (region == null || node == null) return RiftEruption.None;

        var besieged = await BesiegedRegionsAsync(gameInstanceId);
        bool besiegeable = !besieged.Contains(regionId) && !SiegeRules.IsUnderTruce(region, at) &&
                           !SiegeRules.SeasonIsClosing(at, instance.SeasonEndsAt);
        var eruption = RiftRules.EruptionFor(region, landErupted, besiegeable);
        if (eruption == RiftEruption.None) return eruption;

        var rows = await SettleAsync(gameInstanceId, regions, at);
        var row = rows.FirstOrDefault(r => r.Faction == FactionId.Riftborn);
        if (row == null)
        {
            // The Riftborn's row is made by the eruption that brings them, empty: what they have comes with the land.
            row = new FactionState { GameInstanceId = gameInstanceId, Faction = FactionId.Riftborn, Strength = 0, SettledAtUtc = at };
            _context.FactionStates.Add(row);
            rows.Add(row);
        }

        string name = TemperamentOf(FactionId.Riftborn).Name;
        string? previousHolder = string.IsNullOrEmpty(region.OwnerUserId)
            ? (FactionStrengthRules.IsFaction(region.Faction) ? WarLogPrefix + region.Faction : null)
            : region.OwnerUserId;
        FactionSiege? siege = null;
        bool worldChanged = false;

        switch (eruption)
        {
            case RiftEruption.Seized:
            {
                WorldRegionBlob.ClaimForFaction(node, FactionId.Riftborn, name);
                region.Ownership = LocationOwnership.Enemy;
                region.OwnerUserId = string.Empty;
                region.OwnerDisplayName = name;
                region.Faction = FactionId.Riftborn;
                // They come through in force: the land arrives with the strength it supports.
                row.Strength = Math.Min(CapOf(row, regions), row.Strength + FactionStrengthRules.CapOf(region));
                row.SettledAtUtc = at;
                worldChanged = true;
                break;
            }
            case RiftEruption.Besieged:
            {
                long hold = DefendingHold(region, regions, rows, 0, at);
                double march = RiftRules.EruptionMarch(hold, breachLeft, breachMax);
                siege = new FactionSiege
                {
                    GameInstanceId = gameInstanceId,
                    SeasonNumber = instance.SeasonNumber,
                    Faction = FactionId.Riftborn,
                    RegionId = regionId,
                    DefenderUserId = previousHolder!,
                    March = Math.Round(march),
                    State = SiegeState.Mustering,
                    DeclaredAt = at,
                    MusterEndsAt = at + SiegeRules.Muster
                };
                _context.FactionSieges.Add(siege);
                row.LastActedAtUtc = at;
                break;
            }
            case RiftEruption.Surged:
            {
                row.Surges++;
                row.Strength = Math.Min(CapOf(row, regions),
                    row.Strength + FactionStrengthRules.Cap(FactionId.Riftborn, regions) * FactionStrengthRules.SurgeBonus);
                row.SettledAtUtc = at;
                break;
            }
        }

        if (worldChanged)
        {
            worldRow.GameData = world!.ToJsonString();
            worldRow.UpdatedAt = DateTime.UtcNow;
        }
        await _context.SaveChangesAsync();

        if (worldChanged)
        {
            // Land changed hands: settle what everyone earned at the old rates first (see ActAsync).
            if (_seasons != null) await _seasons.SettleAllAsync(gameInstanceId, world, at);
            await BroadcastWorldAsync(worldRow);
        }
        if (siege != null) await AnnounceDeclaredAsync(siege);

        _sessionLog.Log("RIFTBORN-ERUPT",
            $"instance={gameInstanceId} region={regionId} eruption={eruption} breach={breachLeft}/{breachMax} " +
            $"landErupted={landErupted} strength={row.Strength:F0} surges={row.Surges} march={siege?.March:F0}");
        return eruption;
    }

    /// <summary>
    /// The factions beside an open Great Rift strike at it (row 7): each that holds land on or beside it, with
    /// <see cref="RiftRules.FactionChipChance"/>, takes <see cref="RiftRules.FactionChip"/> off the Breach and loses
    /// <see cref="RiftRules.FactionChipCost"/> of its strength. Saved with the turn; a rift a player moved at the same
    /// moment keeps the player's move.
    /// </summary>
    private async Task ChipTheRiftAsync(Guid gameInstanceId, List<FactionState> rows, IReadOnlyCollection<WorldRegionData> regions,
        DateTime now, List<string> done)
    {
        var rift = await _context.Rifts.FirstOrDefaultAsync(r =>
            r.GameInstanceId == gameInstanceId && r.Kind == RiftKind.Great &&
            (r.Stage == RiftStage.Open || r.Stage == RiftStage.Omen) && r.OpensAt <= now && r.ClosesAt > now);
        if (rift == null) return;
        var at = regions.FirstOrDefault(r => r.RegionId == rift.RegionId);
        if (at == null) return;

        int chipped = 0;
        foreach (var row in rows)
        {
            if (!RiftRules.Chips(row.Faction, at, regions)) continue;
            if (_random.NextDouble() >= RiftRules.FactionChipChance) continue;

            int before = rift.Breach;
            rift.Breach = RiftRules.Chipped(rift.Breach, RiftRules.FactionChip(rift.BreachMax));
            if (rift.Breach == before) continue;

            chipped += before - rift.Breach;
            row.Strength = Math.Max(0, row.Strength * (1.0 - RiftRules.FactionChipCost));
            done.Add($"{TemperamentOf(row.Faction).Name} struck at the rift in {rift.RegionId} (Breach {before} → {rift.Breach}).");
            _sessionLog.Log("FACTION-RIFT-CHIP",
                $"instance={gameInstanceId} faction={row.Faction} rift={rift.Id} breach={before}->{rift.Breach} strength={row.Strength:F0}");
        }
        if (chipped == 0) return;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // A player's win moved the rift first: theirs stands, and the strike is lost (the strength is still spent).
            await _context.Entry(rift).ReloadAsync();
            await _context.SaveChangesAsync();
            return;
        }

        if (_hub != null)
        {
            await _hub.Clients.Group(gameInstanceId.ToString())
                .SendAsync("RiftUpdated", new RiftUpdatedNotification(rift.Id, rift.Kind, rift.Stage, rift.RegionId));
        }
    }
}
