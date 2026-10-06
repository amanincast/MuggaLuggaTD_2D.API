using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Planned waves in the open field (Mike, 2026-10-04): each wave a roster that grows by wave and tier,
/// the next on a clock or once the field is clear, and a run that pays a modest 1.75x the old one
/// however many more enemies it holds.
/// </summary>
public class WavePlanTests
{
    private static readonly RunTuning Tuning = new();

    [Fact]
    public void OnlyTheOpenFieldFightsInWaves()
    {
        Assert.True(WavePlan.IsWaveArena(LocationType.Portal));
        Assert.True(WavePlan.IsWaveArena(LocationType.ResourceNode));
        Assert.False(WavePlan.IsWaveArena(LocationType.Dungeon));
        Assert.False(WavePlan.IsWaveArena(LocationType.Ruin));
        Assert.False(WavePlan.IsWaveArena(LocationType.Outpost));
        Assert.False(WavePlan.IsWaveArena(LocationType.Castle));
    }

    [Theory]
    [InlineData(1, 5, 12, 28, 100)]
    [InlineData(2, 6, 15, 40, 165)]
    [InlineData(3, 8, 18, 60, 312)]
    [InlineData(4, 10, 24, 96, 600)]
    public void WavesGrowByWaveAndByTier(int tier, int waves, int first, int last, int total)
    {
        Assert.Equal(waves, WavePlan.Waves(Tuning, tier));
        Assert.Equal(first, WavePlan.EnemiesInWave(Tuning, tier, 1));
        Assert.Equal(last, WavePlan.EnemiesInWave(Tuning, tier, waves));
        Assert.Equal(total, WavePlan.TotalEnemies(Tuning, tier));
    }

    [Fact]
    public void TheNextWaveComesOnTheClock_OrSoonAfterTheFieldIsClear()
    {
        Assert.False(WavePlan.NextWaveDue(Tuning, sinceWaveBegan: 10, clearFor: -1));
        Assert.True(WavePlan.NextWaveDue(Tuning, sinceWaveBegan: 30, clearFor: -1));   // swamped
        Assert.False(WavePlan.NextWaveDue(Tuning, sinceWaveBegan: 10, clearFor: 1));
        Assert.True(WavePlan.NextWaveDue(Tuning, sinceWaveBegan: 10, clearFor: 2));    // cleared fast
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AnOpenFieldRunPaysAModestStepUp_NotOnePerEnemy(int tier)
    {
        // Experience is not rolled, so it is the clean measure of the whole run's price.
        long before = RunRewardCalculator.Calculate(5, tier, Tuning, null, new Random(1)).Experience;
        long open = RunRewardCalculator.Calculate(5, tier, Tuning, null, new Random(1), site: LocationType.Portal).Experience;

        double growth = (double)open / before;
        Assert.InRange(growth, 1.6, 2.3);   // 1.75x the enemies' worth, a little more for their later levels
    }

    [Fact]
    public void PlacedFightsAreStillPricedAsBefore()
    {
        long plain = RunRewardCalculator.Calculate(5, 3, Tuning, null, new Random(1)).Experience;
        long dungeon = RunRewardCalculator.Calculate(5, 3, Tuning, null, new Random(1), site: LocationType.Dungeon).Experience;

        Assert.Equal(plain, dungeon);
    }

    [Fact]
    public void MaterialsGrowModestlyToo()
    {
        var materials = new List<MaterialTemplate>
        {
            new() { MaterialName = "Essence", Category = Enums.MaterialCategory.Essence, Tier = Enums.MaterialTier.Tier1 },
            new() { MaterialName = "Crystal", Category = Enums.MaterialCategory.AffinityCrystal, Tier = Enums.MaterialTier.Tier1 },
            new() { MaterialName = "Shard", Category = Enums.MaterialCategory.RarityShard, Tier = Enums.MaterialTier.Tier1 },
        };
        int Count(LocationType? site)
        {
            int sum = 0;
            for (int seed = 0; seed < 200; seed++)
                sum += MaterialRewardCalculator.Calculate(5, 4, Tuning, materials, new Random(seed), site).Sum(m => m.Quantity);
            return sum;
        }

        double growth = (double)Count(LocationType.Portal) / Count(null);
        Assert.InRange(growth, 1.4, 2.4);
    }
}
