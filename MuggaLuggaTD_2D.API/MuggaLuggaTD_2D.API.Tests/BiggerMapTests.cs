using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Reclaiming phase 2b, the bigger map (row P2-11, Mike 2026-11-12: "expand the number of regions that separate
/// players"). With wild land worth cleansing, the ground between seats is content, so a new world is a whole hexagon
/// of 127 regions and its players sit further apart.
/// </summary>
public class BiggerMapTests
{
    private static List<WorldMapGenerator.PlayerSeat> SeatsFor(int players) => Enumerable.Range(0, players)
        .Select(i => new WorldMapGenerator.PlayerSeat($"user-{i}", $"Herald {i}")).ToList();

    [Fact]
    public void ANewWorld_IsAWholeHexagonOfRadiusSix()
    {
        var regions = WorldMapGenerator.Generate(1, SeatsFor(2));

        Assert.Equal(127, regions.Count);
        Assert.Equal(6, regions.Max(r => HexCoord.Distance(new HexCoord(0, 0), r.Hex)));
        // Every ring complete: 6n hexes on ring n, so no side of the world is shallower than another.
        for (int ring = 1; ring <= 6; ring++)
            Assert.Equal(6 * ring, regions.Count(r => HexCoord.Distance(new HexCoord(0, 0), r.Hex) == ring));
    }

    [Fact]
    public void Seed2024_SeatsItsTwoPlayersWhereTheClientExpects()
    {
        // The Unity side pins the same world (WorldMapGeneratorTests.Generate_MatchesTheWorldProducedOnDotNet).
        var seats = new List<WorldMapGenerator.PlayerSeat> { new("user-a", "Aldric"), new("user-b", "Morwen") };
        var capitals = WorldMapGenerator.Generate(2024, seats).Where(r => r.IsCapital).Select(r => r.RegionId).OrderBy(id => id);
        Assert.Equal(new[] { "r23", "r81" }, capitals);
    }

    [Fact]
    public void TiersRunOutward_HeartToRim()
    {
        // Seatless, so nothing is softened around a capital.
        var regions = WorldMapGenerator.Generate(3, SeatsFor(0));
        foreach (var region in regions)
        {
            int ring = HexCoord.Distance(new HexCoord(0, 0), region.Hex);
            int expected = ring <= 1 ? 1 : ring <= 3 ? 2 : ring <= 5 ? 3 : 4;
            Assert.Equal(expected, region.Tier);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void EverySeat_IsInRingsThreeToFive(int players)
    {
        for (int seed = 1; seed <= 60; seed++)
            foreach (var capital in WorldMapGenerator.Generate(seed, SeatsFor(players)).Where(r => r.IsCapital))
                Assert.True(WorldMapGenerator.InSettlementBand(capital.Hex),
                    $"seed {seed}: {capital.RegionId} seated {HexCoord.Distance(new HexCoord(0, 0), capital.Hex)} rings out");
    }

    [Fact]
    public void FourPlayers_SitAtLeastFourRegionsApart()
    {
        // On the 78-region map four seats in rings 2-4 could be three apart; the wider band leaves room between.
        for (int seed = 1; seed <= 60; seed++)
        {
            var capitals = WorldMapGenerator.Generate(seed, SeatsFor(4)).Where(r => r.IsCapital).ToList();
            Assert.Equal(4, capitals.Count);
            int nearest = capitals.SelectMany(a => capitals.Where(b => b != a), (a, b) => HexCoord.Distance(a.Hex, b.Hex)).Min();
            Assert.True(nearest >= 4, $"seed {seed}: two seats only {nearest} apart");
        }
    }

    [Fact]
    public void EachFactionHoldsThreeToSix_AndTheRestOfTheHardCountryIsWild()
    {
        // The realm at 127 (spec §8, row S-1): half the hard country was 30-45 regions; a faction now starts with at most
        // MaximumFactionRegions, and the banners past it stay wild for players to cleanse.
        var seats = SeatsFor(4);
        foreach (int seed in Enumerable.Range(1, 40))
        {
            var regions = WorldMapGenerator.Generate(seed, seats);
            var held = regions.Where(r => FactionStrengthRules.IsFaction(r.Faction)).GroupBy(r => r.Faction).ToList();
            Assert.Equal(3, held.Count);
            Assert.All(held, g => Assert.InRange(g.Count(), WorldMapGenerator.MinimumFactionRegions, WorldMapGenerator.MaximumFactionRegions));
            Assert.True(regions.Count(r => r.Tier >= 3 && r.Ownership == LocationOwnership.Neutral) > 40, $"seed {seed}: little wild hard country");
        }
    }
}
