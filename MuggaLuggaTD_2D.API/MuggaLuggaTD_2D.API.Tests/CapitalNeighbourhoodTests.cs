using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// A seat's doorstep is gentle (Mike's first solo playthrough, 2026-10-05): a capital on the outer
/// edge of the seating band used to border tier-3 country and warband banners, so a level-1 company
/// had one easy region and then a cliff.
/// </summary>
public class CapitalNeighbourhoodTests
{
    public static IEnumerable<object[]> Seatings()
    {
        yield return new object[] { 1 };
        yield return new object[] { 4 };
        yield return new object[] { 10 };
    }

    [Theory]
    [MemberData(nameof(Seatings))]
    public void EveryRegionTouchingACapital_IsTierTwoAtMost_AndFlagless(int players)
    {
        var seats = Enumerable.Range(0, players)
            .Select(i => new WorldMapGenerator.PlayerSeat($"user-{i}", $"Herald {i}")).ToList();

        for (int seed = 1; seed <= 60; seed++)
        {
            var regions = WorldMapGenerator.Generate(seed, seats);
            var capitals = regions.Where(r => r.IsCapital).ToList();
            Assert.NotEmpty(capitals);

            foreach (var region in regions.Where(r => !r.IsCapital))
            {
                if (!capitals.Any(c => HexCoord.Distance(c.Hex, region.Hex) == 1)) continue;

                Assert.True(region.Tier <= WorldMapGenerator.CapitalNeighbourMaxTier,
                    $"seed {seed}: {region.RegionId} borders a capital at tier {region.Tier}");
                Assert.True(region.Faction != FactionId.Ashkin && region.Faction != FactionId.Grimjaw,
                    $"seed {seed}: {region.RegionId} borders a capital under {region.Faction}");
            }
        }
    }

    [Fact]
    public void TheRimStillHasWarbands()
    {
        var seats = new List<WorldMapGenerator.PlayerSeat> { new("user-a", "Aldric") };
        int flagged = Enumerable.Range(1, 20)
            .Sum(seed => WorldMapGenerator.Generate(seed, seats).Count(r => r.Faction is FactionId.Ashkin or FactionId.Grimjaw));
        Assert.True(flagged > 20 * 10, $"only {flagged} warband regions across 20 worlds");
    }
}
