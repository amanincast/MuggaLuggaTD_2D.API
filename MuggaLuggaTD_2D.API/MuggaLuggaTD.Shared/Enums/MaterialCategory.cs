namespace Enums
{
    /// <summary>
    /// Represents the category of a material, determining what aspect of an item it can upgrade.
    /// </summary>
    public enum MaterialCategory : short
    {
        /// <summary>
        /// Essence materials upgrade the PowerTier of equipment (1-10).
        /// </summary>
        Essence = 0,

        /// <summary>
        /// Affinity crystals add or upgrade affinity influences on equipment.
        /// </summary>
        AffinityCrystal = 1,

        /// <summary>
        /// Rarity shards upgrade the rarity level of equipment (Common through GodLike).
        /// </summary>
        RarityShard = 2
    }
}
