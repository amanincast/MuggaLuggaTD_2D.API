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

        /// <summary>
        /// What the clear pays in gold. Derived from <see cref="Experience"/> rather than counted
        /// separately, so the two can never disagree about how long or how hard the run was.
        /// </summary>
        public long Gold { get; set; }

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

                // Some of the wave is elite, on the same schedule the combat scene spawns them - an
                // elite is tougher and worth more, and a run priced as if every enemy were a trash
                // mob would pay for an easier fight than the one the location demands.
                int elites = Math.Min(perWave, Math.Max(0, EliteRules.ElitesInWave(tuning, wave + 1)));

                for (int i = 0; i < perWave; i++)
                {
                    bool isElite = i < elites;

                    long health = isElite ? EliteRules.EliteHealth(enemyHealth) : enemyHealth;
                    long experience = ExperienceForEnemy(enemyLevel, health);
                    rewards.Experience += isElite ? EliteRules.EliteExperience(experience) : experience;

                    if (itemTemplates != null && itemTemplates.Count > 0
                        && ItemDropCalculator.ShouldDropItem(enemyLevel))
                    {
                        var drop = RollItem(itemTemplates[random.Next(itemTemplates.Count)], enemyLevel);
                        if (drop != null) rewards.Items.Add(drop);
                    }
                }
            }

            // From tier 3 up the run ends on a boss, and the wave does not end until it is dead. It
            // is most of the last wave's difficulty, so it has to be most of its price too.
            if (BossRules.HasBoss(tuning, locationTier))
            {
                int finalLevel = baseLevel + ((waves - 1) / levelInterval);
                long bossHealth = BossRules.BossHealth(ScaledEnemyHealth(tuning, finalLevel));

                rewards.Experience += BossRules.BossExperience(ExperienceForEnemy(finalLevel, bossHealth));
            }

            // Gold is priced last, off the experience the whole clear came to. It is not a drop and
            // never was one - see GoldRules - so there is nothing to roll here, only to convert.
            rewards.Gold = GoldRules.GoldForClear(rewards.Experience);

            return rewards;
        }

        /// <summary>
        /// The enemy the reward is priced from. Via <see cref="EnemyStatScaling"/> so this is the same
        /// health the client gives that enemy - the two used to differ by one level of scaling, and the
        /// payout was 7-15% light as a result.
        /// </summary>
        private static long ScaledEnemyHealth(RunTuning tuning, int enemyLevel)
        {
            long scaled = EnemyStatScaling.ScaledHealth(
                tuning.BaseEnemyHealth, enemyLevel, tuning.EnemyHealthMultiplierPerLevel);

            return scaled < 1 ? 1 : scaled;
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
