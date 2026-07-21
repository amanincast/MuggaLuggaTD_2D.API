using System;
using System.Collections.Generic;
using Enums;
using Items.Models;
using Items.Utilities;
using StateManagement.Models;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>What a completed PvE run pays out.</summary>
    public class RunRewards
    {
        public long Experience { get; set; }
        public List<ItemSaveData> Items { get; set; } = new List<ItemSaveData>();
    }

    /// <summary>A base item the drop roll can pick from, taken from the ItemData content.</summary>
    public class ItemTemplate
    {
        public string ItemName { get; set; }
        public ItemTypes ItemType { get; set; }
        public List<ItemImplicitTypes> ImplicitPool { get; set; }
        public List<ItemExplicitTypes> ExplicitPool { get; set; }
    }

    /// <summary>
    /// Decides what clearing a PvE location is worth.
    ///
    /// The server pays out from this rather than from a total the client reports, because a
    /// self-reported total is unbounded: fabricated XP and gear inflate a party's power, and PvP
    /// power is computed from that same persisted roster — so trusting it would quietly undo the
    /// server-authoritative PvP work.
    ///
    /// The budget is derived from the location itself: tier decides how many waves must be survived,
    /// each wave a fixed number of enemies, and enemy level climbs on the same interval the combat
    /// scene uses. Rewards therefore reflect the fight the location demands, not the client's account
    /// of it. This intentionally does not try to match a particular playthrough kill-for-kill — the
    /// server defines what the clear is worth.
    /// </summary>
    public static class RunRewardCalculator
    {
        /// <summary>Mirrors the client's GameCharacter.GetExperienceWorth so a kill is valued the same.</summary>
        public static long ExperienceForEnemy(long enemyLevel, long enemyMaxHealth)
            => (enemyLevel * 50) + (enemyMaxHealth / 10);

        /// <summary>
        /// Rolls the rewards for clearing a location. <paramref name="random"/> is injected so the
        /// caller owns the entropy and tests can be deterministic.
        /// </summary>
        public static RunRewards Calculate(
            int locationLevel,
            int locationTier,
            RunTuning tuning,
            IReadOnlyList<ItemTemplate> itemTemplates,
            Random random)
        {
            var rewards = new RunRewards();
            if (tuning == null)
                return rewards;

            int waves = Math.Max(1, tuning.GetWavesRequiredForTier(locationTier));
            int perWave = Math.Max(1, tuning.EnemiesRequiredPerWave);
            int levelInterval = Math.Max(1, tuning.EnemyLevelIncreaseInterval);
            int baseLevel = Math.Max(1, locationLevel);

            for (int wave = 0; wave < waves; wave++)
            {
                // Enemy level climbs on the same cadence the combat scene uses.
                int enemyLevel = baseLevel + (wave / levelInterval);
                long enemyHealth = ScaledEnemyHealth(tuning, enemyLevel);

                for (int i = 0; i < perWave; i++)
                {
                    rewards.Experience += ExperienceForEnemy(enemyLevel, enemyHealth);

                    if (itemTemplates != null && itemTemplates.Count > 0
                        && ItemDropCalculator.ShouldDropItem(enemyLevel))
                    {
                        var drop = RollItem(itemTemplates[random.Next(itemTemplates.Count)], enemyLevel);
                        if (drop != null) rewards.Items.Add(drop);
                    }
                }
            }

            return rewards;
        }

        private static long ScaledEnemyHealth(RunTuning tuning, int enemyLevel)
        {
            var scaled = tuning.BaseEnemyHealth * (1f + tuning.EnemyHealthMultiplierPerLevel * (enemyLevel - 1));
            return (long)Math.Max(1, scaled);
        }

        /// <summary>
        /// Builds a persisted item from a content template, running the same rarity/tier/attribute
        /// rolls the client's drop path uses.
        /// </summary>
        private static ItemSaveData RollItem(ItemTemplate template, int enemyLevel)
        {
            if (template == null)
                return null;

            var item = new ItemSaveData
            {
                Id = Guid.NewGuid().ToString(),
                ItemName = template.ItemName,
                ItemType = template.ItemType,
                ItemCount = 1
            };

            ItemDropCalculator.ApplyDropProperties(item, enemyLevel, template.ImplicitPool, template.ExplicitPool);
            return item;
        }
    }
}
