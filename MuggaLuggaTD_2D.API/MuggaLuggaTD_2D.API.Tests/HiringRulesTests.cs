using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The Hiring Hall's rules (design 12e). What is pinned: the design's prices; each trait does what
/// its line says and nothing else; traits multiply; a worker cannot work a trade they do not have;
/// output is rate × time, with a Lucky worker's luck coming in whole hours that average one in ten;
/// and the roll deals traits by tier, never the same one twice.
/// </summary>
public class HiringRulesTests
{
    private static double Rate(WorkerTier tier = WorkerTier.Skilled, ResourceTrade trade = ResourceTrade.Miner,
        ResourceTrade? second = null, WorkerTrait[]? traits = null, BiomeType home = BiomeType.Highland,
        ResourceTrade site = ResourceTrade.Miner, BiomeType siteBiome = BiomeType.Volcanic, int siteTier = 1, int foremen = 0)
        => HiringRules.RateAt(tier, trade, second, traits ?? Array.Empty<WorkerTrait>(), home, site, siteBiome, siteTier, foremen);

    [Fact]
    public void TheDesignsPrices()
    {
        Assert.Equal(600, HiringRules.CostOf(WorkerTier.Local));
        Assert.Equal(1200, HiringRules.CostOf(WorkerTier.Skilled));
        Assert.Equal(2400, HiringRules.CostOf(WorkerTier.Master));
    }

    [Fact]
    public void EachTraitDoesWhatItsLineSays()
    {
        double plain = Rate();
        Assert.Equal(18, plain);
        Assert.Equal(plain * 1.15, Rate(traits: new[] { WorkerTrait.Steady }), 6);

        Assert.Equal(plain, Rate(traits: new[] { WorkerTrait.Hometown }), 6);
        Assert.Equal(plain * 1.40, Rate(traits: new[] { WorkerTrait.Hometown }, siteBiome: BiomeType.Highland), 6);

        Assert.Equal(plain, Rate(traits: new[] { WorkerTrait.Prospector }, siteTier: 2), 6);
        Assert.Equal(plain * 1.25, Rate(traits: new[] { WorkerTrait.Prospector }, siteTier: 3), 6);

        Assert.Equal(plain, Rate(traits: new[] { WorkerTrait.Foreman }), 6);
        Assert.Equal(plain * 1.20, Rate(foremen: 2), 6);
    }

    [Fact]
    public void TraitsMultiply()
    {
        double r = Rate(WorkerTier.Master, traits: new[] { WorkerTrait.Hometown, WorkerTrait.Steady }, siteBiome: BiomeType.Highland);
        Assert.Equal(26 * 1.40 * 1.15, r, 6);
    }

    [Fact]
    public void AWorkerCannotWorkATradeTheyDoNotHave_ButAVersatileOneWorksTheirSecondAtHalf()
    {
        Assert.Equal(0, Rate(site: ResourceTrade.Farmer));
        Assert.Equal(9, Rate(second: ResourceTrade.Farmer, traits: new[] { WorkerTrait.Versatile }, site: ResourceTrade.Farmer), 6);
    }

    [Fact]
    public void OutputIsRateTimesTime()
    {
        var from = new DateTime(2026, 10, 1, 9, 20, 0, DateTimeKind.Utc);
        Assert.Equal(18 * 2.5, HiringRules.Gathered(18, from, from.AddHours(2.5), lucky: false, "w"), 6);
        Assert.Equal(0, HiringRules.Gathered(18, from, from, lucky: false, "w"));
    }

    [Fact]
    public void LuckComesInWholeHours_AboutOneInTen()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double plain = HiringRules.Gathered(10, from, from.AddHours(5000), lucky: false, "lucky-worker");
        double lucky = HiringRules.Gathered(10, from, from.AddHours(5000), lucky: true, "lucky-worker");

        Assert.InRange(lucky / plain, 1.07, 1.13);

        // Within one hour, luck is all or nothing.
        double oneHour = HiringRules.Gathered(10, from, from.AddHours(1), lucky: true, "lucky-worker");
        Assert.True(Math.Abs(oneHour - 10) < 1e-9 || Math.Abs(oneHour - 20) < 1e-9);
    }

    [Fact]
    public void TheRollDealsTraitsByTier_NeverTwice()
    {
        var random = new Random(11);
        for (int i = 0; i < 3000; i++)
        {
            var roll = HiringRules.Roll(random, BiomeType.Forest);
            int expected = roll.Tier == WorkerTier.Master ? 2 : roll.Tier == WorkerTier.Skilled ? 1 : -1;
            if (expected >= 0) Assert.Equal(expected, roll.Traits.Count);
            else Assert.InRange(roll.Traits.Count, 0, 1);

            Assert.Equal(roll.Traits.Count, roll.Traits.Distinct().Count());
            Assert.Equal(roll.Traits.Contains(WorkerTrait.Versatile), roll.SecondTrade.HasValue);
            if (roll.SecondTrade.HasValue) Assert.NotEqual(roll.Trade, roll.SecondTrade.Value);
            Assert.False(string.IsNullOrWhiteSpace(roll.Name));
            Assert.Equal(BiomeType.Forest, roll.HomeBiome);
        }
    }

    [Fact]
    public void ARefreshDoublesAndStops()
    {
        Assert.Equal(100, HiringRules.RefreshCostFor(0));
        Assert.Equal(200, HiringRules.RefreshCostFor(1));
        Assert.Equal(HiringRules.RefreshCostFor(HiringRules.RefreshMaxDoublings), HiringRules.RefreshCostFor(50));
    }

    [Fact]
    public void BedsAreLand()
    {
        Assert.Equal(2, HiringRules.BedsFor(1));
        Assert.Equal(10, HiringRules.BedsFor(5));
    }
}
