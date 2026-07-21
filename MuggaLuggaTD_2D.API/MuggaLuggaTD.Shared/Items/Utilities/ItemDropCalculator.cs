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
        private static readonly Random _random = new Random();

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
        public static bool ShouldDropItem(int creatureLevel)
        {
            float dropChance = CalculateDropChance(creatureLevel);
            return _random.NextDouble() < dropChance;
        }

        #endregion

        #region Rarity Calculation

        /// <summary>
        /// Calculates the rarity of a dropped item based on creature level.
        /// Uses weighted random selection that favors common items, with higher levels increasing rare drop chances.
        /// </summary>
        /// <param name="creatureLevel">The level of the creature.</param>
        /// <returns>The rolled rarity.</returns>
        public static ItemRarityTypes CalculateRarity(int creatureLevel)
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

            return WeightedRandomSelect(weights);
        }

        #endregion

        #region Power Tier Calculation

        /// <summary>
        /// Calculates the power tier of a dropped item based on creature level.
        /// Higher levels unlock higher tiers, weighted towards lower tiers.
        /// </summary>
        /// <param name="creatureLevel">The level of the creature.</param>
        /// <returns>The rolled power tier.</returns>
        public static ItemPowerTier CalculatePowerTier(int creatureLevel)
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

            return WeightedRandomSelect(weights);
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
        public static DropResult CalculateDrop(int creatureLevel)
        {
            var result = new DropResult
            {
                ShouldDrop = ShouldDropItem(creatureLevel)
            };

            if (result.ShouldDrop)
            {
                result.Rarity = CalculateRarity(creatureLevel);
                result.PowerTier = CalculatePowerTier(creatureLevel);
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
        public static void ApplyDropProperties(
            IItemData item,
            int creatureLevel,
            List<ItemImplicitTypes> implicitPool = null,
            List<ItemExplicitTypes> explicitPool = null)
        {
            // Calculate rarity and tier based on creature level
            item.Rarity = CalculateRarity(creatureLevel);
            item.PowerTier = CalculatePowerTier(creatureLevel);

            // Calculate influence chance and generate attributes
            float influenceChance = CalculateInfluenceChance(creatureLevel);
            ItemAttributeRoller.GenerateAttributes(item, influenceChance, implicitPool, explicitPool);
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Performs weighted random selection from a dictionary of weights.
        /// </summary>
        private static T WeightedRandomSelect<T>(Dictionary<T, float> weights)
        {
            float totalWeight = 0f;
            foreach (var weight in weights.Values)
            {
                totalWeight += weight;
            }

            float roll = (float)_random.NextDouble() * totalWeight;
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
