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

        /// <summary>Health of a representative enemy at level 1, used to value a kill.</summary>
        public long BaseEnemyHealth { get; set; } = 50;

        /// <summary>
        /// Fraction of base health added per enemy level. Matches CharacterLevelScaling's default so
        /// a kill is valued against roughly the enemy the player actually fought.
        /// </summary>
        public float EnemyHealthMultiplierPerLevel { get; set; } = 0.15f;

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
