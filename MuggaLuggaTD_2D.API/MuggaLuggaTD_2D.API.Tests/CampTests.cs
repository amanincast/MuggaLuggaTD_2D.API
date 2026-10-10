using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Camps and fightable ruins (Mike, playtest 2026-10-09: a first region held two caves and little else to do).
/// Every region gains two camps, placed after every other site so a world made before them keeps every site
/// where it stood.
/// </summary>
public class CampTests
{
    private static IEnumerable<(BiomeType Biome, int Tier, int Seed)> Regions()
    {
        foreach (BiomeType biome in Enum.GetValues(typeof(BiomeType)))
            for (int tier = 1; tier <= 4; tier++)
                for (int seed = 1; seed <= 12; seed++)
                    yield return (biome, tier, seed * 7919);
    }

    [Fact]
    public void EveryRegionHasTwoCamps_AfterEveryOtherSite()
    {
        foreach (var (biome, tier, seed) in Regions())
        {
            var sites = RegionGenerator.Generate("r1", seed, biome, tier).Sites;
            var camps = sites.Where(s => s.Type == LocationType.Camp).ToList();
            Assert.Equal(2, camps.Count);
            // Last in the list: placed after the rest, so nothing older moved to make room for them.
            Assert.All(sites.Skip(sites.Count - 2), s => Assert.Equal(LocationType.Camp, s.Type));
        }
    }

    [Fact]
    public void ACampIsAQuickFight_AtTheRegionsLevel()
    {
        foreach (var (biome, tier, seed) in Regions())
        {
            var sites = RegionGenerator.Generate("r1", seed, biome, tier).Sites;
            int lowestCave = sites.Where(s => s.Type == LocationType.Dungeon).Min(s => s.Level);
            foreach (var camp in sites.Where(s => s.Type == LocationType.Camp))
            {
                Assert.Equal(1, camp.Tier);                               // tier 1: the fewest waves, no boss
                Assert.True(camp.Level >= (tier * 5) - 4, $"{biome} t{tier}: camp level {camp.Level}");
                Assert.True(camp.Level <= lowestCave + 3);
                Assert.True(WavePlan.IsWaveArena(camp.Type));             // fought in the open
            }
        }
    }

    [Fact]
    public void CampsAndRuinsAreFought_OnlyARuinBringsARecruit()
    {
        Assert.True(SiteSpec.IsFightableType(LocationType.Camp));
        Assert.True(SiteSpec.IsFightableType(LocationType.Ruin));
        Assert.False(SiteSpec.IsFightableType(LocationType.ResourceNode));
        Assert.Equal(ConquestOutcome.RemoveLocation, ConquestResolver.ResolveOnPlayerVictory(LocationType.Camp));
        Assert.Equal(ConquestOutcome.RemoveLocation, ConquestResolver.ResolveOnPlayerVictory(LocationType.Ruin));
        Assert.True(TavernRules.BringsARecruit(LocationType.Ruin));
        Assert.False(TavernRules.BringsARecruit(LocationType.Camp));   // plentiful: a recruit each would flood the board
        Assert.True(RegionResolveRules.RestoredByClearing(LocationType.Camp) > 0);
    }

    [Fact]
    public void TheClientsGoldenRegion_KeepsItsSites_AndGainsTwoCamps()
    {
        // The Unity suite's RegionGeneratorTests golden ("r5", 987654, Grassland, tier 2), run here on
        // .NET so both runtimes are seen to agree: the nine sites from before camps, then the two camps.
        var sites = RegionGenerator.Generate("r5", 987654, BiomeType.Grassland, 2).Sites;
        var expected = new (LocationType Type, int X, int Y)[]
        {
            (LocationType.Castle, 18, 10), (LocationType.Dungeon, 31, 4), (LocationType.Dungeon, 16, 1),
            (LocationType.Dungeon, 0, 16), (LocationType.NeutralHome, 28, 13), (LocationType.NeutralHome, 6, 16),
            (LocationType.ResourceNode, 8, 10), (LocationType.ResourceNode, 25, 16), (LocationType.ResourceNode, 11, 3),
            (LocationType.Camp, 19, 4), (LocationType.Camp, 0, 1),
        };
        Assert.Equal(expected.Length, sites.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal($"r5:{i}", sites[i].SiteId);
            Assert.Equal(expected[i], (sites[i].Type, sites[i].Cell.X, sites[i].Cell.Y));
        }
    }

    [Fact]
    public void CampsAreNamedLikeCamps_AndUniqueInTheirRegion()
    {
        var region = new WorldRegionData { RegionId = "r1", Seed = 7919, Biome = BiomeType.Grassland, Tier = 1 };
        var layout = RegionGenerator.Generate(region);
        var names = Naming.SiteNames(region, "Oakvale", layout.Sites);
        var campNames = layout.Sites.Where(s => s.Type == LocationType.Camp).Select(s => names[s.SiteId]).ToList();
        Assert.Equal(2, campNames.Distinct().Count());
        Assert.All(campNames, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(names.Count, names.Values.Distinct().Count());
    }
}
