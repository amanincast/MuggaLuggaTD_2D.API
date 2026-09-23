using System;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// What an elite is, and how many of them a wave holds.
    ///
    /// <para>A horde game that only ever adds <i>more</i> of the same enemy ramps by noise. An elite
    /// is the other lever: one enemy in the wave that has to be answered rather than absorbed. Design
    /// doc 03 §3 and doc 04.</para>
    ///
    /// <para><b>Shared</b> because the server prices a run from the fight the location demands, and
    /// elites are part of that fight. If the client spawned them and the pricing did not know, a
    /// tier-4 run would be paid for an easier run than the one fought - which is the same mismatch
    /// <see cref="EnemyStatScaling"/> exists to prevent.</para>
    ///
    /// <para>A siege assault brings its own elites: the server says how many champions defend the
    /// region, and they are dealt into the later waves. That path pays nothing, so it does not touch
    /// pricing.</para>
    /// </summary>
    public static class EliteRules
    {
        /// <summary>An elite has this much of a normal enemy's health.</summary>
        public const float HealthMultiplier = 2.0f;

        /// <summary>And hits this much harder.</summary>
        public const float DamageMultiplier = 1.25f;

        /// <summary>And is worth this much more when it dies.</summary>
        public const float ExperienceMultiplier = 1.5f;

        /// <summary>
        /// How many of <paramref name="waveNumber"/>'s enemies are elite: none until the run has
        /// found its feet, then one, then one more every few waves.
        ///
        /// <para>Deliberately <b>not</b> scaled by tier as well. Tier already buys more waves, and a
        /// per-tier elite bonus on top would put half of a tier-4 wave in elites - an addition to
        /// make once there is playtest evidence for it, not before.</para>
        /// </summary>
        public static int ElitesInWave(RunTuning tuning, int waveNumber)
        {
            if (tuning == null || waveNumber < 1)
                return 0;

            int from = Math.Max(1, tuning.ElitesFromWave);
            if (waveNumber < from)
                return 0;

            int interval = Math.Max(1, tuning.EliteIntervalWaves);
            return 1 + ((waveNumber - from) / interval);
        }

        /// <summary>
        /// How many of a siege's champions stand in <paramref name="waveNumber"/>.
        ///
        /// <para>Spread evenly, with the remainder falling at the <b>end</b> of the assault - the
        /// defence should get heavier as the attacker pushes in, not thinner.</para>
        /// </summary>
        public static int SiegeElitesInWave(int eliteCount, int totalWaves, int waveNumber)
        {
            if (eliteCount <= 0 || totalWaves < 1 || waveNumber < 1 || waveNumber > totalWaves)
                return 0;

            int even = eliteCount / totalWaves;
            int remainder = eliteCount % totalWaves;

            // Counting back from the last wave, so the remainder lands on the final ones.
            int fromTheEnd = totalWaves - waveNumber;
            return even + (fromTheEnd < remainder ? 1 : 0);
        }

        public static long EliteHealth(long health)
        {
            long scaled = (long)Math.Round(health * (double)HealthMultiplier);
            return scaled < 1 ? 1 : scaled;
        }

        public static long EliteDamage(long damage)
        {
            long scaled = (long)Math.Round(damage * (double)DamageMultiplier);
            return scaled < 1 ? 1 : scaled;
        }

        public static long EliteExperience(long experience)
            => (long)Math.Round(experience * (double)ExperienceMultiplier);
    }
}
