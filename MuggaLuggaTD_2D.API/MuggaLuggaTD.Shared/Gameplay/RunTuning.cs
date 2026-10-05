namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// The tuning that decides how long a PvE run is and how its enemies scale.
    ///
    /// Shipped as game content (SurvivalData.json) so the client's combat pacing and the server's
    /// reward budget come from one place. If these drifted apart, the server would pay out for a
    /// fight of a different length than the one the player actually fought.
    /// </summary>
    public class RunTuning
    {
        /// <summary>Waves that must be survived, indexed by location tier (1-4).</summary>
        public int WavesRequiredTier1 { get; set; } = 3;
        public int WavesRequiredTier2 { get; set; } = 4;
        public int WavesRequiredTier3 { get; set; } = 5;
        public int WavesRequiredTier4 { get; set; } = 6;

        /// <summary>Enemies that must be defeated to complete a wave.</summary>
        public int EnemiesRequiredPerWave { get; set; } = 10;

        /// <summary>Enemy level goes up by one every this many waves.</summary>
        public int EnemyLevelIncreaseInterval { get; set; } = 3;

        /// <summary>The wave elites start appearing in. Before it, a run is just enemies.</summary>
        public int ElitesFromWave { get; set; } = 3;

        /// <summary>One more elite per wave every this many waves after the first elite wave.</summary>
        public int EliteIntervalWaves { get; set; } = 2;

        /// <summary>
        /// The lowest tier whose last wave carries a boss. Zero for none - a tier-1 dungeon is a
        /// three-wave errand and does not want a set piece.
        /// </summary>
        public int BossFromTier { get; set; } = 3;

        /// <summary>Health of a representative enemy at level 1, used to value a kill.</summary>
        public long BaseEnemyHealth { get; set; } = 50;

        /// <summary>
        /// Fraction of base health added per enemy level. Matches CharacterLevelScaling's default so
        /// a kill is valued against roughly the enemy the player actually fought.
        /// </summary>
        public float EnemyHealthMultiplierPerLevel { get; set; } = 0.15f;

        // ----- The open-field wave arena (WavePlan). The fields above still size and pay placed
        //       fights - dungeons, ruins, a keep's road - and ambush skirmishes.

        /// <summary>Waves an open-field run has, by tier.</summary>
        public int WaveArenaWavesTier1 { get; set; } = 5;
        public int WaveArenaWavesTier2 { get; set; } = 6;
        public int WaveArenaWavesTier3 { get; set; } = 8;
        public int WaveArenaWavesTier4 { get; set; } = 10;

        /// <summary>Enemies in the first wave, and how many more each wave after it brings (before the tier factor).</summary>
        public int WaveArenaFirstWaveEnemies { get; set; } = 12;
        public int WaveArenaEnemiesAddedPerWave { get; set; } = 4;

        /// <summary>Every wave's size is multiplied by its tier's factor.</summary>
        public double WaveArenaTierFactorTier1 { get; set; } = 1.0;
        public double WaveArenaTierFactorTier2 { get; set; } = 1.25;
        public double WaveArenaTierFactorTier3 { get; set; } = 1.5;
        public double WaveArenaTierFactorTier4 { get; set; } = 2.0;

        /// <summary>The next wave comes this long after the last one began, cleared or not.</summary>
        public double WaveArenaWaveSeconds { get; set; } = 30;

        /// <summary>A wave's enemies are dealt in over this long, in bursts.</summary>
        public double WaveArenaDealSeconds { get; set; } = 12;

        /// <summary>After the field is cleared, the next wave comes this long after.</summary>
        public double WaveArenaBreatherSeconds { get; set; } = 2;

        /// <summary>
        /// The most enemies standing at once. Dealing waits while the field is this full, and the wave
        /// clock runs on, so a slow clearer is swamped up to here and no further (a frame budget: ~240
        /// standing cost 25-30 ms a frame in the 2026-10-04 profile). Does not change what a run pays.
        /// </summary>
        public int WaveArenaMostStanding { get; set; } = 150;

        /// <summary>A whole open-field run pays this many times what the old ten-a-wave run paid.</summary>
        public double WaveArenaPayGrowth { get; set; } = 1.75;

        public int GetWavesRequiredForTier(int tier)
        {
            switch (tier)
            {
                case 1: return WavesRequiredTier1;
                case 2: return WavesRequiredTier2;
                case 3: return WavesRequiredTier3;
                case 4: return WavesRequiredTier4;
                default: return tier < 1 ? WavesRequiredTier1 : WavesRequiredTier4;
            }
        }
    }
}
