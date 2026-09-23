using System;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// What a boss is, and when a run gets one.
    ///
    /// <para>A run needs a climax rather than a counter reaching ten. From tier 3 up, the last wave
    /// carries a boss and does not end until it is dead - so the run finishes on a fight the player
    /// can lose, not on a tally. Design doc 03 §3 and doc 04.</para>
    ///
    /// <para><b>Shared</b> for the same reason <see cref="EliteRules"/> is: the server prices a run
    /// from the fight the location demands, and a boss is a large part of that fight.</para>
    ///
    /// <para>The phases are what make it a fight rather than a wall of health. The boss opens with
    /// one ability and gains another at each threshold, so the same enemy asks a different question
    /// as it goes down.</para>
    /// </summary>
    public static class BossRules
    {
        /// <summary>A boss has this much of an ordinary enemy's health. It is meant to take a while.</summary>
        public const float HealthMultiplier = 12f;

        /// <summary>And hits this much harder.</summary>
        public const float DamageMultiplier = 1.5f;

        /// <summary>And is worth this much more than its health alone would say.</summary>
        public const float ExperienceMultiplier = 2f;

        /// <summary>
        /// Health fractions at which the boss enters its next phase and gains its next ability.
        /// Ordered high to low, so the index of the last one crossed is the phase.
        /// </summary>
        public static readonly float[] PhaseThresholds = { 0.66f, 0.33f };

        /// <summary>Phases a boss has: the one it opens in, plus one per threshold.</summary>
        public static int PhaseCount => PhaseThresholds.Length + 1;

        /// <summary>
        /// Which phase a boss at <paramref name="healthFraction"/> is in. 1 while it is above the
        /// first threshold, rising as it falls.
        /// </summary>
        public static int PhaseFor(float healthFraction)
        {
            int phase = 1;
            foreach (var threshold in PhaseThresholds)
            {
                if (healthFraction <= threshold) phase++;
            }

            return phase;
        }

        /// <summary>Whether a run at this tier ends on a boss.</summary>
        public static bool HasBoss(RunTuning tuning, int locationTier)
            => tuning != null && tuning.BossFromTier > 0 && locationTier >= tuning.BossFromTier;

        public static long BossHealth(long health)
        {
            long scaled = (long)Math.Round(health * (double)HealthMultiplier);
            return scaled < 1 ? 1 : scaled;
        }

        public static long BossDamage(long damage)
        {
            long scaled = (long)Math.Round(damage * (double)DamageMultiplier);
            return scaled < 1 ? 1 : scaled;
        }

        public static long BossExperience(long experience)
            => (long)Math.Round(experience * (double)ExperienceMultiplier);
    }
}
