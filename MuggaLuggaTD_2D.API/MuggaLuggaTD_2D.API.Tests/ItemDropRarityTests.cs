using Enums;
using Items.Models;
using Items.Utilities;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using Xunit;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The drop roll's rarity by creature level (Mike, 2026-11-12): a first fight once dropped a Celestial, because
/// every rarity could roll at any level. Legendary now drops from level 8, Celestial from 18, GodLike from 26.
/// </summary>
public class ItemDropRarityTests
{
    [Theory]
    [InlineData(1, ItemRarityTypes.Rare)]
    [InlineData(7, ItemRarityTypes.Rare)]
    [InlineData(8, ItemRarityTypes.Legendary)]
    [InlineData(17, ItemRarityTypes.Legendary)]
    [InlineData(18, ItemRarityTypes.Celestial)]
    [InlineData(25, ItemRarityTypes.Celestial)]
    [InlineData(26, ItemRarityTypes.GodLike)]
    [InlineData(30, ItemRarityTypes.GodLike)]
    public void EachLevelHasABestRarity(int level, ItemRarityTypes best) =>
        Assert.Equal(best, ItemDropCalculator.MaxRarityFor(level));

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(17)]
    [InlineData(25)]
    public void NoRollGoesAboveTheLevelsBest(int level)
    {
        var random = new Random(level);
        var best = ItemDropCalculator.MaxRarityFor(level);
        var seen = new HashSet<ItemRarityTypes>();
        for (int i = 0; i < 50_000; i++)
        {
            var rarity = ItemDropCalculator.CalculateRarity(level, random);
            Assert.True(rarity <= best, $"level {level} rolled {rarity}");
            seen.Add(rarity);
        }
        Assert.Contains(best, seen);   // the best is still reachable, not just allowed
    }

    [Fact]
    public void TheSameRandom_RollsTheSameClear()
    {
        var tuning = new RunTuning { BaseEnemyHealth = 50, EnemyHealthMultiplierPerLevel = 0.15f, EnemiesRequiredPerWave = 10, EnemyLevelIncreaseInterval = 3 };
        var templates = new List<ItemTemplate> { new() { ItemName = "Sword", ItemType = ItemTypes.Weapon } };

        string Roll() => string.Join(",", RunRewardCalculator.Calculate(9, 2, tuning, templates, new Random(7))
            .Items.Select(i => $"{i.Rarity}/{i.PowerTier}"));

        Assert.Equal(Roll(), Roll());
    }

    [Fact]
    public void ADropsRollsCanRunOnManyThreadsAtOnce()
    {
        // One shared Random used from several threads can break and roll the same value forever. Each thread
        // has its own now, so a burst of parallel claims still rolls a spread of rarities.
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<ItemRarityTypes, int>();
        Parallel.For(0, 200_000, _ => counts.AddOrUpdate(ItemDropCalculator.CalculateRarity(10), 1, (_, n) => n + 1));
        Assert.True(counts.Count >= 4, string.Join(" ", counts));
        Assert.InRange(counts[ItemRarityTypes.Common] / 200_000.0, 0.40, 0.48);   // ~44% at level 10
    }
}
