using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The seventh biome. Arcane had been nobody's home ground; the desert is it now, so a clear there
/// is where a player goes to lean the Tavern toward an Arcane recruit.
/// </summary>
public class DesertBiomeTests
{
    [Fact]
    public void TheDesertFavoursArcane()
    {
        Assert.Equal(AffinityTypes.Arcane, TavernRules.FavouredAffinity(BiomeType.Desert));
    }

    [Fact]
    public void EveryBiomeFavoursADifferentAffinity()
    {
        var favoured = Enum.GetValues<BiomeType>().Select(TavernRules.FavouredAffinity).ToList();

        Assert.DoesNotContain(null, favoured);
        Assert.Equal(favoured.Count, favoured.Distinct().Count());
    }

    [Fact]
    public void NewWorldsHaveDesert_ButNeverBesideTheStart()
    {
        var seats = new List<WorldMapGenerator.PlayerSeat> { new("user-a", "Aldric") };
        int deserts = 0;

        for (int seed = 1; seed <= 20; seed++)
        {
            foreach (var region in WorldMapGenerator.Generate(seed, seats))
            {
                if (region.Biome != BiomeType.Desert) continue;

                deserts++;
                Assert.True(HexCoord.Distance(new HexCoord(0, 0), region.Hex) > 1,
                    $"seed {seed}: desert at {region.Hex}");
            }
        }

        Assert.True(deserts > 0, "twenty worlds and not one desert");
    }

    [Fact]
    public void ADesertKeepsItsRuinsAndItsPortalFromTierTwo()
    {
        var sites = RegionGenerator.Generate("r1", 4242, BiomeType.Desert, 2).Sites;

        Assert.Contains(sites, s => s.Type == LocationType.Ruin);
        Assert.Contains(sites, s => s.Type == LocationType.Portal);

        // Elsewhere a ruin waits for tier 3, so this is the desert's doing and not the tier's.
        Assert.DoesNotContain(RegionGenerator.Generate("r1", 4242, BiomeType.Grassland, 2).Sites,
            s => s.Type == LocationType.Ruin);
    }
}
