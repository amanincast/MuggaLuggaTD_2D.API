namespace Enums
{
    /// <summary>
    /// Represents the tier/strength of a material.
    /// Higher tier materials provide stronger upgrade effects.
    /// </summary>
    public enum MaterialTier : short
    {
        /// <summary>
        /// Tier 1 materials provide +1 upgrade level.
        /// Examples: Lesser Essence, Minor Affinity Crystal, Common Shard
        /// </summary>
        Tier1 = 1,

        /// <summary>
        /// Tier 2 materials provide +2 upgrade levels.
        /// Examples: Greater Essence, Major Affinity Crystal, Rare Shard
        /// </summary>
        Tier2 = 2,

        /// <summary>
        /// Tier 3 materials provide +3 upgrade levels.
        /// Examples: Supreme Essence, Perfect Affinity Crystal, Legendary Shard
        /// </summary>
        Tier3 = 3
    }
}
