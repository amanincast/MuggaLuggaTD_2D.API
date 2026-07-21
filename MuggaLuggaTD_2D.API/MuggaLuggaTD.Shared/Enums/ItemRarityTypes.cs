namespace Enums
{
    /// <summary>
    /// Represents the rarity level of an item.
    /// Higher rarity items have more explicit attributes and better stat rolls.
    /// </summary>
    public enum ItemRarityTypes : short
    {
        Common = 0,      // White
        Uncommon = 1,    // Green
        Magic = 2,       // Blue
        Rare = 3,        // Purple
        Legendary = 4,   // Gold
        Celestial = 5,   // Light-blue
        GodLike = 6      // Rainbow/Silver
    }
}
