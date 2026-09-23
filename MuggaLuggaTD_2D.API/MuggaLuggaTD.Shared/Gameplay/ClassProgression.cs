using System;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// How a player character's health grows with its level.
    ///
    /// <para>It did not, until now. A level bought ability upgrades and nothing else, while enemies
    /// gained 15% health a level - so the whole of a character's durability came from gear, and a
    /// level-20 party was as fragile as a level-1 one. Design doc 03 §2b.</para>
    ///
    /// <para>The <i>rate</i> is per class and lives in content, because it is a balance dial; the
    /// shape is here, because the server recomputes PvP power from the saved roster and health is one
    /// of its terms. A class that grows faster is durable rather than powerful, which is the whole
    /// point of having classes.</para>
    /// </summary>
    public static class ClassProgression
    {
        /// <summary>What a class with no growth stated in content gets. Deliberately modest.</summary>
        public const float DefaultHealthGrowthPerLevel = 0.05f;

        /// <summary>
        /// A character's health at <paramref name="level"/>. Level 1 is the content value, and growth
        /// is additive on the base rather than compounding - the same shape enemies use, so the two
        /// curves can be read against each other.
        /// </summary>
        public static long ScaledHealth(long baseHealth, long level, float growthPerLevel)
        {
            if (level < 1) level = 1;
            if (growthPerLevel < 0f) growthPerLevel = 0f;

            long scaled = (long)Math.Round(baseHealth * (1f + growthPerLevel * (level - 1)));
            return scaled < 1 ? 1 : scaled;
        }
    }
}
