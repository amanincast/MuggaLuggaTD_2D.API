using System;
using System.Collections.Generic;
using System.Linq;
using Enums;
using Items.Models;

namespace Items.Utilities
{
    /// <summary>
    /// Generates attributes for items based on tier, rarity, and influence.
    /// Handles rolling of implicit attributes, explicit attributes, and influence bonuses.
    /// </summary>
    public static class ItemAttributeRoller
    {
        private static readonly Random _random = new Random();

        #region Attribute Count Calculations

        /// <summary>
        /// Calculates the number of explicit attributes from tier contribution.
        /// Tier 1-2: +1, Tier 3-4: +2, Tier 5-6: +3, etc.
        /// </summary>
        public static int GetTierAttributeCount(ItemPowerTier tier)
        {
            int tierValue = (int)tier;
            return 1 + ((tierValue - 1) / 2);
        }

        /// <summary>
        /// Calculates the number of explicit attributes from rarity contribution.
        /// Common/Uncommon (0-1): +1, Magic/Rare (2-3): +2, Legendary/Celestial (4-5): +3, GodLike (6): +4
        /// </summary>
        public static int GetRarityAttributeCount(ItemRarityTypes rarity)
        {
            int rarityIndex = (int)rarity;
            int baseCount = 1 + (rarityIndex / 2);
            int godlikeBonus = rarityIndex == 6 ? 1 : 0;
            return baseCount + godlikeBonus;
        }

        /// <summary>
        /// Gets the total number of explicit attributes (not including influence bonuses).
        /// </summary>
        public static int GetTotalExplicitCount(ItemPowerTier tier, ItemRarityTypes rarity)
        {
            return GetTierAttributeCount(tier) + GetRarityAttributeCount(rarity);
        }

        /// <summary>
        /// Gets the total number of influence bonus attributes.
        /// Each influence star adds one affinity-specific bonus attribute.
        /// </summary>
        public static int GetInfluenceAttributeCount(List<ItemInfluence> influences)
        {
            if (influences == null || influences.Count == 0)
                return 0;
            return influences.Sum(i => i.Stars);
        }

        #endregion

        #region Implicit Attribute Rolling

        /// <summary>
        /// Rolls an implicit attribute for an item.
        /// </summary>
        /// <param name="tier">The item's power tier.</param>
        /// <param name="implicitPool">Optional pool of allowed implicit types. If empty, all types are available.</param>
        /// <returns>A rolled implicit attribute.</returns>
        public static ItemImplicitAttribute RollImplicitAttribute(ItemPowerTier tier, List<ItemImplicitTypes> implicitPool = null)
        {
            // Select type from pool or all types
            ItemImplicitTypes[] availableTypes = implicitPool != null && implicitPool.Count > 0
                ? implicitPool.ToArray()
                : (ItemImplicitTypes[])Enum.GetValues(typeof(ItemImplicitTypes));

            var selectedType = availableTypes[_random.Next(availableTypes.Length)];
            float value = CalculateImplicitValue(selectedType, tier);

            return new ItemImplicitAttribute
            {
                Type = selectedType,
                Value = value
            };
        }

        /// <summary>
        /// Calculates the value for an implicit attribute based on type and tier.
        /// </summary>
        private static float CalculateImplicitValue(ItemImplicitTypes type, ItemPowerTier tier)
        {
            int tierValue = (int)tier;

            switch (type)
            {
                case ItemImplicitTypes.CooldownReduction:
                case ItemImplicitTypes.MovementSpeed:
                case ItemImplicitTypes.IncreasedRange:
                    // Percentage types: 1% at Tier 1, 10% at Tier 10
                    return 1f + (tierValue - 1) * (9f / 9f);

                case ItemImplicitTypes.ProjectilePierceCount:
                case ItemImplicitTypes.ProjectileCount:
                case ItemImplicitTypes.ChainingCount:
                    // Count types: 1 at Tier 1-5, 2 at Tier 6+
                    return tierValue >= 6 ? 2f : 1f;

                default:
                    return 1f;
            }
        }

        #endregion

        #region Explicit Attribute Rolling

        /// <summary>
        /// Rolls explicit attributes for an item.
        /// </summary>
        /// <param name="tier">The item's power tier.</param>
        /// <param name="rarity">The item's rarity.</param>
        /// <param name="influences">Optional influences for bonus attributes.</param>
        /// <param name="explicitPool">Optional pool of allowed explicit types.</param>
        /// <returns>List of rolled explicit attributes.</returns>
        public static List<ItemExplicitAttribute> RollExplicitAttributes(
            ItemPowerTier tier,
            ItemRarityTypes rarity,
            List<ItemInfluence> influences = null,
            List<ItemExplicitTypes> explicitPool = null)
        {
            var result = new List<ItemExplicitAttribute>();

            // Get available types
            ItemExplicitTypes[] availableTypes = explicitPool != null && explicitPool.Count > 0
                ? explicitPool.ToArray()
                : (ItemExplicitTypes[])Enum.GetValues(typeof(ItemExplicitTypes));

            // Roll main explicit attributes
            int mainAttributeCount = GetTotalExplicitCount(tier, rarity);
            var usedTypes = new HashSet<ItemExplicitTypes>();

            for (int i = 0; i < mainAttributeCount; i++)
            {
                var attribute = RollSingleExplicitAttribute(tier, rarity, availableTypes, usedTypes, null, false);
                if (attribute != null)
                {
                    result.Add(attribute);
                    usedTypes.Add(attribute.Type);
                }
            }

            // Roll influence bonus attributes
            if (influences != null)
            {
                foreach (var influence in influences)
                {
                    for (int star = 0; star < influence.Stars; star++)
                    {
                        var attribute = RollInfluenceBonusAttribute(tier, rarity, influence.AffinityType);
                        if (attribute != null)
                        {
                            result.Add(attribute);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Rolls a single explicit attribute.
        /// </summary>
        private static ItemExplicitAttribute RollSingleExplicitAttribute(
            ItemPowerTier tier,
            ItemRarityTypes rarity,
            ItemExplicitTypes[] availableTypes,
            HashSet<ItemExplicitTypes> usedTypes,
            AffinityTypes? forcedAffinity,
            bool isInfluenceBonus)
        {
            // Filter out already used non-affinity types (affinity types can repeat with different affinities)
            var unusedTypes = availableTypes.Where(t => !usedTypes.Contains(t) || IsAffinityBased(t)).ToArray();
            if (unusedTypes.Length == 0)
                return null;

            var selectedType = unusedTypes[_random.Next(unusedTypes.Length)];

            // Determine affinity if needed
            AffinityTypes? affinity = null;
            if (IsAffinityBased(selectedType))
            {
                affinity = forcedAffinity ?? GetRandomAffinity();
            }

            float value = CalculateExplicitValue(selectedType, tier, rarity);

            return new ItemExplicitAttribute
            {
                Type = selectedType,
                AffinityType = affinity,
                Value = value,
                IsInfluenceBonus = isInfluenceBonus
            };
        }

        /// <summary>
        /// Rolls an influence bonus attribute (always affinity-based).
        /// </summary>
        private static ItemExplicitAttribute RollInfluenceBonusAttribute(
            ItemPowerTier tier,
            ItemRarityTypes rarity,
            AffinityTypes affinityType)
        {
            // Influence bonuses are always affinity-based damage or defense
            var affinityTypes = new[] { ItemExplicitTypes.AffinityDamageFlat, ItemExplicitTypes.AffinityDefenseFlat };
            var selectedType = affinityTypes[_random.Next(affinityTypes.Length)];

            float baseValue = CalculateExplicitValue(selectedType, tier, rarity);
            float tierMultiplier = 1.0f + ((int)tier - 1) * 0.15f;

            // Influence bonus scaling: 10% of base value per star (handled by caller looping per star)
            float influenceValue = baseValue * 0.1f * tierMultiplier;

            return new ItemExplicitAttribute
            {
                Type = selectedType,
                AffinityType = affinityType,
                Value = influenceValue,
                IsInfluenceBonus = true
            };
        }

        /// <summary>
        /// Calculates the value for an explicit attribute based on type, tier, and rarity.
        /// </summary>
        private static float CalculateExplicitValue(ItemExplicitTypes type, ItemPowerTier tier, ItemRarityTypes rarity)
        {
            float tierMultiplier = 1.0f + ((int)tier - 1) * 0.15f;
            float baseMax = GetBaseMaxValue(type);
            float rarityMax = GetRarityScaledMax(baseMax, rarity);

            // Roll between 50% and 100% of the rarity-scaled max
            float minRoll = rarityMax * 0.5f;
            float maxRoll = rarityMax;
            float value = minRoll + (float)_random.NextDouble() * (maxRoll - minRoll);

            return value * tierMultiplier;
        }

        /// <summary>
        /// Gets the base maximum value for an explicit attribute type at Common rarity.
        /// </summary>
        private static float GetBaseMaxValue(ItemExplicitTypes type)
        {
            switch (type)
            {
                case ItemExplicitTypes.Health:
                    return 40f;
                case ItemExplicitTypes.IncreasedDefensePercent:
                    return 5f;
                case ItemExplicitTypes.EvasionFlat:
                    return 20f;
                case ItemExplicitTypes.EvasionPercent:
                    return 3f;
                case ItemExplicitTypes.AffinityDamageFlat:
                    return 5f;
                case ItemExplicitTypes.AffinityDefenseFlat:
                    return 10f;
                case ItemExplicitTypes.AffinityDamagePercent:
                    return 3f;
                case ItemExplicitTypes.AffinityDefensePercent:
                    return 5f;
                default:
                    return 10f;
            }
        }

        /// <summary>
        /// Scales the base max value by rarity (doubles each tier).
        /// </summary>
        private static float GetRarityScaledMax(float baseMax, ItemRarityTypes rarity)
        {
            // Each rarity level doubles the max value
            return baseMax * (float)Math.Pow(2, (int)rarity);
        }

        /// <summary>
        /// Checks if an explicit attribute type requires an affinity.
        /// </summary>
        private static bool IsAffinityBased(ItemExplicitTypes type)
        {
            return (int)type >= 10;
        }

        /// <summary>
        /// Gets a random affinity type.
        /// </summary>
        private static AffinityTypes GetRandomAffinity()
        {
            var affinities = (AffinityTypes[])Enum.GetValues(typeof(AffinityTypes));
            return affinities[_random.Next(affinities.Length)];
        }

        #endregion

        #region Influence Rolling

        /// <summary>
        /// Rolls influences for an item based on influence chance.
        /// </summary>
        /// <param name="influenceChance">Probability of having an influence (0-1).</param>
        /// <param name="maxTotalStars">Maximum total stars across all influences.</param>
        /// <returns>List of influences, or empty list if no influence.</returns>
        public static List<ItemInfluence> RollInfluences(float influenceChance, int maxTotalStars = 5)
        {
            var influences = new List<ItemInfluence>();

            if (_random.NextDouble() >= influenceChance)
                return influences;

            // Roll total stars (weighted towards lower values)
            int totalStars = RollWeightedStars(maxTotalStars);
            var usedAffinities = new HashSet<AffinityTypes>();

            int remainingStars = totalStars;
            while (remainingStars > 0)
            {
                // Get available affinities
                var availableAffinities = ((AffinityTypes[])Enum.GetValues(typeof(AffinityTypes)))
                    .Where(a => !usedAffinities.Contains(a))
                    .ToArray();

                if (availableAffinities.Length == 0)
                    break;

                var selectedAffinity = availableAffinities[_random.Next(availableAffinities.Length)];
                int stars = Math.Min(remainingStars, RollWeightedStars(Math.Min(5, remainingStars)));

                influences.Add(new ItemInfluence
                {
                    AffinityType = selectedAffinity,
                    Stars = stars
                });

                usedAffinities.Add(selectedAffinity);
                remainingStars -= stars;
            }

            return influences;
        }

        /// <summary>
        /// Rolls a weighted star count favoring lower values.
        /// </summary>
        private static int RollWeightedStars(int maxStars)
        {
            // Weights: 1 star = 50%, 2 stars = 25%, 3 stars = 15%, 4 stars = 7%, 5 stars = 3%
            float roll = (float)_random.NextDouble();

            if (roll < 0.50f && maxStars >= 1) return 1;
            if (roll < 0.75f && maxStars >= 2) return 2;
            if (roll < 0.90f && maxStars >= 3) return 3;
            if (roll < 0.97f && maxStars >= 4) return 4;
            if (maxStars >= 5) return 5;

            return Math.Max(1, maxStars);
        }

        #endregion

        #region Full Item Generation

        /// <summary>
        /// Generates all attributes for an item.
        /// </summary>
        /// <param name="item">The item to generate attributes for.</param>
        /// <param name="influenceChance">Probability of having an influence.</param>
        /// <param name="implicitPool">Optional pool of allowed implicit types.</param>
        /// <param name="explicitPool">Optional pool of allowed explicit types.</param>
        public static void GenerateAttributes(
            IItemData item,
            float influenceChance = 0.1f,
            List<ItemImplicitTypes> implicitPool = null,
            List<ItemExplicitTypes> explicitPool = null)
        {
            // Roll influences first (affects explicit attribute count)
            item.Influences = RollInfluences(influenceChance);

            // Roll implicit attribute
            item.ImplicitAttribute = RollImplicitAttribute(item.PowerTier, implicitPool);

            // Roll explicit attributes
            item.ExplicitAttributes = RollExplicitAttributes(
                item.PowerTier,
                item.Rarity,
                item.Influences,
                explicitPool);
        }

        #endregion
    }
}
