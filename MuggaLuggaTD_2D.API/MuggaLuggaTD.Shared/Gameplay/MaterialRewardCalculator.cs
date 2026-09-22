using System;
using System.Collections.Generic;
using System.Linq;
using Enums;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>A material the drop roll can pick, taken from the MaterialData content.</summary>
    public class MaterialTemplate
    {
        public string MaterialName { get; set; }
        public MaterialCategory Category { get; set; }
        public MaterialTier Tier { get; set; }
        public AffinityTypes? AffinityType { get; set; }
    }

    /// <summary>A quantity of one material, granted for a run.</summary>
    public class MaterialGrant
    {
        public string MaterialName { get; set; }
        public int Quantity { get; set; }
    }

    /// <summary>
    /// What a cleared location pays in materials.
    ///
    /// Materials used to drop only as scenery: the client spawned them per kill and threw them away
    /// with the rest of the run, and the server never granted any — so nothing a player could hold
    /// was ever made of them. They are the Tavern's currency (design doc 05), which means they have
    /// to be granted by the server, from the same kind of budget as XP and gear.
    ///
    /// The odds mirror the client's MaterialDropCalculator, so what the server pays still resembles
    /// what the player watched drop during the fight.
    /// </summary>
    public static class MaterialRewardCalculator
    {
        private const float BaseDropChance = 0.15f;
        private const float DropChancePerLevel = 0.01f;
        private const float MaxDropChance = 0.50f;

        /// <summary>The chance one enemy of this level leaves a material behind.</summary>
        public static float DropChanceFor(int enemyLevel)
        {
            float chance = BaseDropChance + enemyLevel * DropChancePerLevel;
            return chance > MaxDropChance ? MaxDropChance : chance;
        }

        /// <summary>
        /// Rolls the materials for clearing a location, enemy by enemy, over the same waves and enemy
        /// levels the run is otherwise priced from.
        /// </summary>
        public static List<MaterialGrant> Calculate(
            int locationLevel,
            int locationTier,
            RunTuning tuning,
            IReadOnlyList<MaterialTemplate> materials,
            Random random)
        {
            var granted = new Dictionary<string, int>();
            if (tuning == null || materials == null || materials.Count == 0 || random == null)
                return new List<MaterialGrant>();

            int waves = Math.Max(1, tuning.GetWavesRequiredForTier(locationTier));
            int enemiesPerWave = Math.Max(1, tuning.EnemiesRequiredPerWave);
            int levelInterval = Math.Max(1, tuning.EnemyLevelIncreaseInterval);
            int baseLevel = Math.Max(1, locationLevel);

            for (int wave = 0; wave < waves; wave++)
            {
                int enemyLevel = baseLevel + (wave / levelInterval);
                float dropChance = DropChanceFor(enemyLevel);

                for (int enemy = 0; enemy < enemiesPerWave; enemy++)
                {
                    if (random.NextDouble() >= dropChance)
                        continue;

                    var template = Roll(enemyLevel, materials, random);
                    if (template?.MaterialName == null)
                        continue;

                    granted.TryGetValue(template.MaterialName, out int count);
                    granted[template.MaterialName] = count + 1;
                }
            }

            return granted
                .Select(pair => new MaterialGrant { MaterialName = pair.Key, Quantity = pair.Value })
                .OrderBy(grant => grant.MaterialName, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Picks one material: its category, then its tier, then which one that names.</summary>
        private static MaterialTemplate Roll(
            int enemyLevel, IReadOnlyList<MaterialTemplate> materials, Random random)
        {
            var category = SelectCategory(enemyLevel, random);
            var tier = SelectTier(enemyLevel, random);

            var candidates = materials
                .Where(m => m != null && m.Category == category && m.Tier == tier)
                .ToList();

            // Nothing of that tier exists for the category (an affinity crystal tier that content
            // does not define, say) — fall back within the category rather than paying nothing.
            if (candidates.Count == 0)
                candidates = materials.Where(m => m != null && m.Category == category).ToList();

            if (candidates.Count == 0)
                return null;

            return candidates[random.Next(candidates.Count)];
        }

        /// <summary>Essences are the staple; crystals and shards climb with the enemy's level.</summary>
        public static MaterialCategory SelectCategory(int enemyLevel, Random random)
        {
            var weights = new Dictionary<MaterialCategory, float>
            {
                { MaterialCategory.Essence, 100f },
                { MaterialCategory.AffinityCrystal, 80f + enemyLevel * 2f },
                { MaterialCategory.RarityShard, 20f + enemyLevel * 3f }
            };

            return WeightedSelect(weights, random);
        }

        /// <summary>Tier 2 opens at enemy level 5, tier 3 at 10 — as the client's drops do.</summary>
        public static MaterialTier SelectTier(int enemyLevel, Random random)
        {
            var weights = new Dictionary<MaterialTier, float> { { MaterialTier.Tier1, 100f } };

            if (enemyLevel >= 5)
                weights[MaterialTier.Tier2] = 30f + enemyLevel * 2f;

            if (enemyLevel >= 10)
                weights[MaterialTier.Tier3] = 10f + enemyLevel * 1f;

            return WeightedSelect(weights, random);
        }

        private static T WeightedSelect<T>(Dictionary<T, float> weights, Random random)
        {
            float total = weights.Values.Sum();
            double roll = random.NextDouble() * total;

            float running = 0f;
            foreach (var pair in weights)
            {
                running += pair.Value;
                if (roll < running)
                    return pair.Key;
            }

            return weights.Keys.Last();
        }
    }
}
