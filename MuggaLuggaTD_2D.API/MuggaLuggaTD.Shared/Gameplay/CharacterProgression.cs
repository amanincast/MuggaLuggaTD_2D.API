using System;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// What a character's level costs.
    ///
    /// <para>The old curve charged <c>100 × 1.5^(L−1)</c> while a run pays out roughly 1.2× more per
    /// level, so the two grew apart in both directions: the first dozen levels went by in a handful of
    /// runs, and then it walled — level 20 wanted 441k cumulative while a matched tier-4 run paid about
    /// 63k, and level 25 wanted 3.4M. Matching the curve's growth to the reward's makes
    /// runs-per-level roughly flat, and leaves the <i>base</i> as the single dial for pacing.</para>
    ///
    /// <para>Shared because the server grants the experience and the client spends it on levels; a
    /// character's level feeds PvP power, so both sides must agree what a level costs. Design doc 03.</para>
    /// </summary>
    public static class CharacterProgression
    {
        /// <summary>Experience for the first level. The dial that sets the pace.</summary>
        public const long BaseExperienceToLevel = 4000;

        /// <summary>Growth per level, matched to how run rewards grow.</summary>
        public const float ExperienceGrowthPerLevel = 1.2f;

        /// <summary>The highest level a character can reach.</summary>
        public const long MaxLevel = 30;

        /// <summary>
        /// Experience needed to go from <paramref name="level"/> to the next one. Zero at the cap,
        /// which is what stops <c>AppylyExperience</c> from levelling past it.
        /// </summary>
        public static long RequiredExperienceFor(long level)
        {
            if (level < 1) level = 1;
            if (level >= MaxLevel) return 0;

            return (long)Math.Round(BaseExperienceToLevel * Math.Pow(ExperienceGrowthPerLevel, level - 1));
        }

        /// <summary>Total experience to take a fresh character to <paramref name="level"/>.</summary>
        public static long CumulativeExperienceFor(long level)
        {
            long total = 0;
            for (long step = 1; step < level && step < MaxLevel; step++)
                total += RequiredExperienceFor(step);

            return total;
        }

        public static bool IsAtCap(long level) => level >= MaxLevel;
    }
}
