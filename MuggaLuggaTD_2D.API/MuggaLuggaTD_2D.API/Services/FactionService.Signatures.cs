using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Signature acts (Factions 2 spec §2.2, step 3). The rules are in <see cref="FactionSignatureRules"/>, and each is wired
/// into the turn it belongs to: the warband and the stampede into <see cref="Raid"/>'s caller, deep roots into
/// <see cref="Fortify"/>, the tide into the siege settlement, overgrowth into the catalogue's expand tier, and the
/// risen dead into the start of every turn (<see cref="RaiseTheDead"/>). This file holds the debug hook that fires one.
/// </summary>
public partial class FactionService
{
    /// <summary>
    /// Makes <paramref name="faction"/> do its signature act now (Combat Debug "Signature now"), through the same turn a
    /// sweep would run:
    /// <list type="bullet">
    /// <item>the Grimjaw and the Horned Kin raid (the warband rides on if it lands; a stampede needs a region raided
    /// in the last hours, so raid twice);</item>
    /// <item>the Ashkin fortify, the Thornwood expand, the Drowned Court lay a siege (the tide follows its win: close
    /// the muster);</item>
    /// <item>the Bone Synod lose a border region to the neighbour beside it, and their dead rise against it.</item>
    /// </list>
    /// </summary>
    private async Task<List<string>> FireSignatureAsync(Guid gameInstanceId, FactionId faction, DateTime now)
    {
        var signature = FactionSignatureRules.Of(faction);
        string name = TemperamentOf(faction).Name;
        var report = new List<string>();
        if (signature != FactionSignature.None) report.Add($"{name}: {FactionSignatureRules.Describe(signature)}");

        switch (signature)
        {
            case FactionSignature.Warband:
            case FactionSignature.Stampede:
                report.AddRange(await ActAsync(gameInstanceId, now, faction, FactionAction.Raid));
                break;
            case FactionSignature.DeepRoots:
                report.AddRange(await ActAsync(gameInstanceId, now, faction, FactionAction.Fortify));
                break;
            case FactionSignature.Overgrowth:
                report.AddRange(await ActAsync(gameInstanceId, now, faction, FactionAction.Expand));
                break;
            case FactionSignature.TheTide:
                report.AddRange(await ActAsync(gameInstanceId, now, faction, FactionAction.Siege));
                report.Add("The tide follows a won siege: close the muster.");
                break;
            case FactionSignature.TheDeadRise:
                string? lost = await LoseBorderRegionAsync(gameInstanceId, faction, now);
                if (lost == null)
                {
                    report.Add($"{name} hold nothing beside a player or another faction to lose.");
                    break;
                }
                report.Add($"{name} lost {lost} to its neighbour.");
                report.AddRange(await ActAsync(gameInstanceId, now, faction, FactionAction.None));
                break;
            default:
                report.Add($"{name} have no signature act.");
                break;
        }
        return report;
    }

    /// <summary>
    /// Debug only: hands one of the faction's regions to a neighbour, a player (not by their capital) or another
    /// faction, as a conquest would. Returns its id, or null when nothing it holds borders anyone.
    /// </summary>
    private async Task<string?> LoseBorderRegionAsync(Guid gameInstanceId, FactionId faction, DateTime now)
    {
        var worldRow = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (worldRow == null || string.IsNullOrEmpty(worldRow.GameData)) return null;
        var world = JsonNode.Parse(worldRow.GameData);
        var regions = WorldRegionBlob.ReadAllRegions(world);
        var byHex = regions.ToDictionary(r => r.Hex);

        foreach (var region in regions.Where(r => FactionStrengthRules.Holds(faction, r)).OrderBy(r => r.RegionId, StringComparer.Ordinal))
        {
            foreach (var hex in region.Hex.Neighbours())
            {
                if (!byHex.TryGetValue(hex, out var next) || FactionStrengthRules.Holds(faction, next)) continue;
                var node = WorldRegionBlob.FindRegion(world, region.RegionId);
                if (node == null) continue;

                if (!string.IsNullOrEmpty(next.OwnerUserId))
                    WorldRegionBlob.CaptureRegion(node, next.OwnerUserId, next.OwnerDisplayName, now);
                else if (FactionStrengthRules.IsFaction(next.Faction))
                    WorldRegionBlob.CaptureWreckedForFaction(node, next.Faction, TemperamentOf(next.Faction).Name, now);
                else
                    continue;

                worldRow.GameData = world!.ToJsonString();
                worldRow.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return region.RegionId;
            }
        }
        return null;
    }
}
