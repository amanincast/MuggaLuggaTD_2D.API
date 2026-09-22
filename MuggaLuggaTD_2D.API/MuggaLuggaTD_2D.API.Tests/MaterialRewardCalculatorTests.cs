using Enums;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// What a cleared run pays in materials. The odds mirror the client's in-run drops, so what the
/// server grants still resembles what the player watched fall during the fight.
/// </summary>
public class MaterialRewardCalculatorTests
{
    [Fact]
    public void ATierOneRunPaysSomething_ButNotAFortune()
    {
        var granted = MaterialRewardCalculator.Calculate(
            locationLevel: 1, locationTier: 1, new RunTuning(), Catalogue(), new Random(1234));

        int total = granted.Sum(g => g.Quantity);

        // 3 waves x 10 enemies at a 16% chance: a handful, not a haul.
        Assert.InRange(total, 1, 12);
    }

    [Fact]
    public void AHigherTierRunPaysMore_BecauseItIsLongerAndItsEnemiesAreWorseNews()
    {
        long tier1 = 0, tier4 = 0;

        // Averaged over many rolls: one seed says nothing about odds.
        for (int seed = 0; seed < 200; seed++)
        {
            tier1 += MaterialRewardCalculator
                .Calculate(5, 1, new RunTuning(), Catalogue(), new Random(seed)).Sum(g => g.Quantity);
            tier4 += MaterialRewardCalculator
                .Calculate(5, 4, new RunTuning(), Catalogue(), new Random(seed)).Sum(g => g.Quantity);
        }

        Assert.True(tier4 > tier1, $"tier 4 paid {tier4}, tier 1 paid {tier1}");
    }

    [Fact]
    public void TheSameSeedPaysTheSameThing()
    {
        var first = MaterialRewardCalculator.Calculate(8, 3, new RunTuning(), Catalogue(), new Random(99));
        var second = MaterialRewardCalculator.Calculate(8, 3, new RunTuning(), Catalogue(), new Random(99));

        Assert.Equal(
            first.Select(g => (g.MaterialName, g.Quantity)),
            second.Select(g => (g.MaterialName, g.Quantity)));
    }

    [Fact]
    public void LowLevelEnemiesNeverDropTheBestTiers()
    {
        var granted = new List<MaterialGrant>();
        for (int seed = 0; seed < 100; seed++)
            granted.AddRange(MaterialRewardCalculator.Calculate(1, 4, new RunTuning(), Catalogue(), new Random(seed)));

        // Tier 2 opens at enemy level 5 and tier 3 at 10; a level-1 site never climbs that far.
        Assert.DoesNotContain(granted, g => g.MaterialName.StartsWith("Greater") || g.MaterialName.StartsWith("Supreme"));
    }

    [Fact]
    public void AHighLevelRunReachesTheBetterTiers()
    {
        var granted = new List<MaterialGrant>();
        for (int seed = 0; seed < 100; seed++)
            granted.AddRange(MaterialRewardCalculator.Calculate(12, 4, new RunTuning(), Catalogue(), new Random(seed)));

        Assert.Contains(granted, g => g.MaterialName.StartsWith("Supreme"));
    }

    [Fact]
    public void DropChanceClimbsWithLevel_AndIsCapped()
    {
        Assert.Equal(0.16f, MaterialRewardCalculator.DropChanceFor(1), 3);
        Assert.Equal(0.25f, MaterialRewardCalculator.DropChanceFor(10), 3);
        Assert.Equal(0.50f, MaterialRewardCalculator.DropChanceFor(100), 3);
    }

    [Fact]
    public void NoContentMeansNoPayout_RatherThanACrash()
    {
        Assert.Empty(MaterialRewardCalculator.Calculate(5, 2, new RunTuning(), Array.Empty<MaterialTemplate>(), new Random(1)));
        Assert.Empty(MaterialRewardCalculator.Calculate(5, 2, null!, Catalogue(), new Random(1)));
    }

    [Fact]
    public void EachMaterialIsReportedOnce_WithItsTotal()
    {
        var granted = MaterialRewardCalculator.Calculate(10, 4, new RunTuning(), Catalogue(), new Random(7));

        Assert.Equal(granted.Select(g => g.MaterialName).Distinct().Count(), granted.Count);
        Assert.All(granted, g => Assert.True(g.Quantity > 0));
    }

    /// <summary>A stand-in for MaterialData: three categories across three tiers.</summary>
    private static List<MaterialTemplate> Catalogue() => new()
    {
        new() { MaterialName = "Lesser Essence",  Category = MaterialCategory.Essence, Tier = MaterialTier.Tier1 },
        new() { MaterialName = "Greater Essence", Category = MaterialCategory.Essence, Tier = MaterialTier.Tier2 },
        new() { MaterialName = "Supreme Essence", Category = MaterialCategory.Essence, Tier = MaterialTier.Tier3 },
        new() { MaterialName = "Minor Fire Crystal",   Category = MaterialCategory.AffinityCrystal, Tier = MaterialTier.Tier1, AffinityType = AffinityTypes.Fire },
        new() { MaterialName = "Greater Fire Crystal", Category = MaterialCategory.AffinityCrystal, Tier = MaterialTier.Tier2, AffinityType = AffinityTypes.Fire },
        new() { MaterialName = "Supreme Fire Crystal", Category = MaterialCategory.AffinityCrystal, Tier = MaterialTier.Tier3, AffinityType = AffinityTypes.Fire },
        new() { MaterialName = "Common Shard",    Category = MaterialCategory.RarityShard, Tier = MaterialTier.Tier1 },
        new() { MaterialName = "Greater Shard",   Category = MaterialCategory.RarityShard, Tier = MaterialTier.Tier2 },
        new() { MaterialName = "Supreme Shard",   Category = MaterialCategory.RarityShard, Tier = MaterialTier.Tier3 }
    };
}
