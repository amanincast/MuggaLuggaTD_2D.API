namespace Enums
{
    /// <summary>
    /// Types of explicit attributes that can appear on items.
    /// Items can have multiple explicit attributes based on tier and rarity.
    /// Affinity-based attributes (10+) require an AffinityType to be specified.
    /// </summary>
    public enum ItemExplicitTypes : short
    {
        // Non-affinity attributes
        Health = 0,
        IncreasedDefensePercent = 1,
        EvasionFlat = 2,
        EvasionPercent = 3,

        // Affinity-based attributes (require AffinityType)
        AffinityDamageFlat = 10,
        AffinityDefenseFlat = 11,
        AffinityDamagePercent = 12,
        AffinityDefensePercent = 13
    }
}
