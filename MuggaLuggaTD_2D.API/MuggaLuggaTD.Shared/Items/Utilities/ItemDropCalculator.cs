using System;
using System.Collections.Generic;
using Enums;
using Items.Models;

namespace Items.Utilities
{
    /// <summary>
    /// Calculates item drop chances, rarity, tier, and influence based on creature level.
    /// Uses weighted random selection to favor common items at lower levels.
    /// </summary>
    public static class ItemDropCalculator
    {
        /// <summary>
        /// The rolls' entropy when the caller passes none. One <see cref="Random"/> per thread: the server rolls
        /// several claims at once, and a single shared <see cref="Random"/> is not thread-safe (used from two
        /// threads at once it can break and roll the same value every time).
        /// </summary>
        internal static Random Shared => _threadRandom ?? (_threadRandom = NewRandom());

        [ThreadStatic] private static Random _threadRandom;
        private static readonly Random _seeds = new Random();

        private static Random NewRandom()
        {
            lock (_seeds) return new Random(_seeds.Next());
        }

        #region Drop Chance

        /// <summary>
        /// Base drop chance (5%)
        /// </summary>
        private const float BaseDropChance = 0.05f;

        /// <summary>
        /// Bonus drop chance per creature level (0.5%)
        /// </summary>
        private const float LevelDropBonus = 0.005f;

        /// <summary>
        /// Maximum drop chance cap
        /// </summary>
        private const float MaxDropChance = 0.25f;

        /// <summary>
        /// Calculates the drop chance for an item based on creature level.
        /// </summary>
        /// <param name="creatureLevel">The level of the creature being killed.</param>
        /// <returns>Drop chance as a value between 0 and 1.</returns>
        public static float CalculateDropChance(int creatureLevel)
        {
            float chance = BaseDropChance + (creatureLevel * LevelDropBonus);
            return Math.Min(chance, MaxDropChance);
        }

        /// <summary>
        /// Determines if an item should drop based on creature level.
        /// </summary>
        /// <param name="creatureLevel">The level of the creature being killed.</param>
        /// <returns>True if an item should drop.</returns>
        public static bool ShouldDropItem(int creatureLevel, Random random = null)
        {
            float dropChance = CalculateDropChance(creatureLevel);
            return (random ?? Shared).NextDouble() < dropChance;
        }

        #endregion

        #region Rarity Calculation

        /// <summary>
        /// The least creature level each rarity drops from (Mike, 2026-11-12): a first fight once dropped a
        /// Celestial, since every rarity could roll at any level. Below its level a rarity's weight is
        /// nothing; at or above it the weights are unchanged. Chests and the Forge set their own rarities.
        /// </summary>
        public const int LegendaryFromLevel = 8, CelestialFromLevel = 18, GodLikeFromLevel = 26;

        /// <summary>The best rarity a creature of <paramref name="creatureLevel"/> can drop.</summary>
        public static ItemRarityTypes MaxRarityFor(int creatureLevel) =>
            creatureLevel >= GodLikeFromLevel ? ItemRarityTypes.GodLike
            : creatureLevel >= CelestialFromLevel ? ItemRarityTypes.Celestial
            : creatureLevel >= LegendaryFromLevel ? ItemRarityTypes.Legendary
            : ItemRarityTypes.Rare;

        /// <summary>
        /// Calculates the rarity of a dropped item based on creature level.
        /// Uses weighted random selection that favors common items, with higher levels increasing rare drop chances.
        /// </summary>
        /// <param name="creatureLevel">The level of the creature.</param>
        /// <returns>The rolled rarity.</returns>
        public static ItemRarityTypes CalculateRarity(int creatureLevel, Random random = null)
        {
            var weights = new Dictionary<ItemRarityTypes, float>
            {
                { ItemRarityTypes.Common,    Math.Max(100, 1000f - (creatureLevel * 20f)) },
                { ItemRarityTypes.Uncommon,  500f + (creatureLevel * 10f) },
                { ItemRarityTypes.Magic,     200f + (creatureLevel * 8f) },
                { ItemRarityTypes.Rare,      50f + (creatureLevel * 5f) },
                { ItemRarityTypes.Legendary, 10f + (creatureLevel * 2f) },
                { ItemRarityTypes.Celestial, 2f + (creatureLevel * 0.5f) },
                { ItemRarityTypes.GodLike,   0.1f + (creatureLevel * 0.1f) }
            };

            var best = MaxRarityFor(creatureLevel);
            foreach (var rarity in new List<ItemRarityTypes>(weights.Keys))
                if (rarity > best) weights.Remove(rarity);

            return WeightedRandomSelect(weights, random);
        }

        #endregion

        #region Power Tier Calculation

        /// <summary>
        /// Calculates the power tier of a dropped item based on creature level.
        /// Higher levels unlock higher tiers, weighted towards lower tiers.
        /// </summary>
        /// <param name="creatureLevel">The level of the creature.</param>
        /// <returns>The rolled power tier.</returns>
        public static ItemPowerTier CalculatePowerTier(int creatureLevel, Random random = null)
        {
            // Max tier increases with level: Level 1-3: max T1, Level 4-6: max T2, etc.
            int maxTierValue = Math.Min(10, 1 + (creatureLevel / 3));

            var weights = new Dictionary<ItemPowerTier, float>();

            for (int tier = 1; tier <= maxTierValue; tier++)
            {
                var tierEnum = (ItemPowerTier)tier;
                // Weight decreases for higher tiers (lower tiers more common)
                float weight = 100f / tier;
                weights[tierEnum] = weight;
            }

            return WeightedRandomSelect(weights, random);
        }

        #endregion

        #region Influence Calculation

        /// <summary>
        /// Base influence chance (5%)
        /// </summary>
        private const float BaseInfluenceChance = 0.05f;

        /// <summary>
        /// Bonus influence chance per creature level (1%)
        /// </summary>
        private const float LevelInfluenceBonus = 0.01f;

        /// <summary>
        /// Calculates the influence chance based on creature level.
        /// </summary>
        /// <param name="creatureLevel">The level of the creature.</param>
        /// <returns>Influence chance as a value between 0 and 1.</returns>
        public static float CalculateInfluenceChance(int creatureLevel)
        {
            return BaseInfluenceChance + (creatureLevel * LevelInfluenceBonus);
        }

        #endregion

        #region Full Drop Generation

        /// <summary>
        /// Result of a drop calculation including all item properties.
        /// </summary>
        public class DropResult
        {
            public bool ShouldDrop { get; set; }
            public ItemRarityTypes Rarity { get; set; }
            public ItemPowerTier PowerTier { get; set; }
            public float InfluenceChance { get; set; }
        }

        /// <summary>
        /// Calculates all drop properties based on creature level.
        /// </summary>
        /// <param name="creatureLevel">The level of the creature.</param>
        /// <returns>A DropResult containing all calculated properties.</returns>
        public static DropResult CalculateDrop(int creatureLevel, Random random = null)
        {
            var result = new DropResult
            {
                ShouldDrop = ShouldDropItem(creatureLevel, random)
            };

            if (result.ShouldDrop)
            {
                result.Rarity = CalculateRarity(creatureLevel, random);
                result.PowerTier = CalculatePowerTier(creatureLevel, random);
                result.InfluenceChance = CalculateInfluenceChance(creatureLevel);
            }

            return result;
        }

        /// <summary>
        /// Generates a complete item with all attributes based on creature level.
        /// </summary>
        /// <param name="baseItem">The base item to enhance.</param>
        /// <param name="creatureLevel">The level of the creature that dropped this item.</param>
        /// <param name="implicitPool">Optional pool of allowed implicit types.</param>
        /// <param name="explicitPool">Optional pool of allowed explicit types.</param>
        /// <param name="rarity">A rarity decided beforehand (a reward that promises one), else rolled.</param>
        public static void ApplyDropProperties(
            IItemData item,
            int creatureLevel,
            List<ItemImplicitTypes> implicitPool = null,
            List<ItemExplicitTypes> explicitPool = null,
            ItemRarityTypes? rarity = null,
            Random random = null)
        {
            // Calculate rarity and tier based on creature level
            item.Rarity = rarity ?? CalculateRarity(creatureLevel, random);
            item.PowerTier = CalculatePowerTier(creatureLevel, random);

            // Calculate influence chance and generate attributes
            float influenceChance = CalculateInfluenceChance(creatureLevel);
            ItemAttributeRoller.GenerateAttributes(item, influenceChance, implicitPool, explicitPool);
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Performs weighted random selection from a dictionary of weights.
        /// </summary>
        private static T WeightedRandomSelect<T>(Dictionary<T, float> weights, Random random)
        {
            float totalWeight = 0f;
            foreach (var weight in weights.Values)
            {
                totalWeight += weight;
            }

            float roll = (float)(random ?? Shared).NextDouble() * totalWeight;
            float cumulative = 0f;

            foreach (var kvp in weights)
            {
                cumulative += kvp.Value;
                if (roll <= cumulative)
                {
                    return kvp.Key;
                }
            }

            // Fallback to first item
            foreach (var key in weights.Keys)
            {
                return key;
            }

            throw new InvalidOperationException("No items in weights dictionary");
        }

        #endregion

        #region Debug / Statistics

        /// <summary>
        /// Gets statistics about drop rates at a given level.
        /// </summary>
        /// <param name="creatureLevel">The level to get stats for.</param>
        /// <returns>String describing drop statistics.</returns>
        public static string GetDropStatistics(int creatureLevel)
        {
            float dropChance = CalculateDropChance(creatureLevel) * 100f;
            float influenceChance = CalculateInfluenceChance(creatureLevel) * 100f;
            int maxTier = Math.Min(10, 1 + (creatureLevel / 3));

            return $"Level {creatureLevel}: Drop {dropChance:F1}%, Influence {influenceChance:F1}%, Max Tier {maxTier}";
        }

        #endregion
    }
}
