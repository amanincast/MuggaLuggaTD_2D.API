using System;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// The waves of an open-field fight: how many there are, how many enemies each one holds, how
    /// often they come, and what each enemy pays (Mike, 2026-10-04: the wave arena felt "too
    /// fast/easy", ended with enemies still standing, and should grow to "mass chaos" at high tiers).
    ///
    /// <para><b>Each wave is a planned roster</b>, not "ten kills". It is dealt in over
    /// <see cref="RunTuning.WaveArenaDealSeconds"/> and is over when every enemy in it is dead. The next
    /// wave comes <see cref="RunTuning.WaveArenaWaveSeconds"/> after the last one began, or a breather
    /// after the field is cleared, whichever is first. Waves grow while the clock does not, so a slow
    /// clearer is swamped (Mike's call). The run is won when the last wave is in and the field is empty.</para>
    ///
    /// <para><b>Only the open field.</b> A dungeon, a ruin and a keep's road place their enemies
    /// (<c>DungeonEncounter</c>) and are still sized and paid by <see cref="RunTuning.GetWavesRequiredForTier"/>
    /// × <see cref="RunTuning.EnemiesRequiredPerWave"/>, as are ambush skirmishes.</para>
    ///
    /// <para><b>Shared</b> because the server pays the run from the same roster the fight deals. The
    /// runs are three to ten times longer, so each enemy pays a share (<see cref="PayShare"/>) that
    /// brings the whole run to <see cref="RunTuning.WaveArenaPayGrowth"/> times what the old run paid
    /// (Mike: "grow modestly").</para>
    /// </summary>
    public static class WavePlan
    {
        /// <summary>
        /// Whether a site is fought in the open, in waves. Mirrors the client's <c>ArenaLayout.For</c>:
        /// a dungeon is a cave, a ruin is rooms, an outpost or castle is the road to its keep.
        /// </summary>
        public static bool IsWaveArena(LocationType site)
        {
            switch (site)
            {
                case LocationType.Dungeon:
                case LocationType.Ruin:
                case LocationType.Outpost:
                case LocationType.Castle:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>How many waves an open-field run of this tier has.</summary>
        public static int Waves(RunTuning tuning, int tier)
        {
            if (tuning == null) return 1;
            int waves;
            switch (Clamp(tier))
            {
                case 1: waves = tuning.WaveArenaWavesTier1; break;
                case 2: waves = tuning.WaveArenaWavesTier2; break;
                case 3: waves = tuning.WaveArenaWavesTier3; break;
                default: waves = tuning.WaveArenaWavesTier4; break;
            }
            return Math.Max(1, waves);
        }

        /// <summary>How much bigger every wave is at this tier.</summary>
        public static double TierFactor(RunTuning tuning, int tier)
        {
            if (tuning == null) return 1;
            switch (Clamp(tier))
            {
                case 1: return tuning.WaveArenaTierFactorTier1;
                case 2: return tuning.WaveArenaTierFactorTier2;
                case 3: return tuning.WaveArenaTierFactorTier3;
                default: return tuning.WaveArenaTierFactorTier4;
            }
        }

        /// <summary>
        /// The enemies wave <paramref name="wave"/> (1-based) holds, elites included: the first wave's
        /// count plus a fixed number per wave after it, times the tier's factor.
        /// </summary>
        public static int EnemiesInWave(RunTuning tuning, int tier, int wave)
        {
            if (tuning == null || wave < 1) return 0;
            double plain = tuning.WaveArenaFirstWaveEnemies + tuning.WaveArenaEnemiesAddedPerWave * (wave - 1);
            return Math.Max(1, (int)Math.Round(plain * TierFactor(tuning, tier), MidpointRounding.AwayFromZero));
        }

        /// <summary>Every enemy the run deals, over all its waves (the boss not included).</summary>
        public static int TotalEnemies(RunTuning tuning, int tier)
        {
            int total = 0;
            int waves = Waves(tuning, tier);
            for (int wave = 1; wave <= waves; wave++)
                total += EnemiesInWave(tuning, tier, wave);
            return total;
        }

        /// <summary>The elites among wave <paramref name="wave"/>'s enemies, on the usual schedule.</summary>
        public static int ElitesInWave(RunTuning tuning, int tier, int wave) =>
            Math.Min(EnemiesInWave(tuning, tier, wave), Math.Max(0, EliteRules.ElitesInWave(tuning, wave)));

        /// <summary>
        /// What one enemy pays, as a share of what it pays in a placed fight. Set so the run as a whole
        /// pays <see cref="RunTuning.WaveArenaPayGrowth"/> times what the old ten-a-wave run did:
        /// growth × (old waves × ten) / this run's enemies. Never more than a whole enemy.
        /// </summary>
        public static double PayShare(RunTuning tuning, int tier)
        {
            if (tuning == null) return 1;
            int total = TotalEnemies(tuning, tier);
            if (total <= 0) return 1;
            double before = Math.Max(1, tuning.GetWavesRequiredForTier(tier)) * Math.Max(1, tuning.EnemiesRequiredPerWave);
            return Math.Min(1.0, Math.Max(0.0, tuning.WaveArenaPayGrowth * before / total));
        }

        /// <summary>
        /// Whether the next wave is due: <paramref name="sinceWaveBegan"/> has reached the wave clock,
        /// or the field has been clear (everything dealt and dead) for the breather.
        /// </summary>
        public static bool NextWaveDue(RunTuning tuning, double sinceWaveBegan, double clearFor)
        {
            double clock = tuning?.WaveArenaWaveSeconds ?? 30;
            double breather = tuning?.WaveArenaBreatherSeconds ?? 2;
            return sinceWaveBegan >= clock || clearFor >= breather;
        }

        private static int Clamp(int tier) => tier < 1 ? 1 : tier > 4 ? 4 : tier;
    }
}
