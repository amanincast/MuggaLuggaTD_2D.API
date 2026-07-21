using Enums;

namespace Items.Models
{
    /// <summary>
    /// Represents an explicit attribute on an item.
    /// Items can have multiple explicit attributes based on tier and rarity.
    /// </summary>
    public class ItemExplicitAttribute
    {
        /// <summary>
        /// The type of explicit attribute.
        /// </summary>
        public ItemExplicitTypes Type { get; set; }

        /// <summary>
        /// The affinity type for affinity-based attributes (AffinityDamageFlat, etc.).
        /// Null for non-affinity attributes (Health, EvasionFlat, etc.).
        /// </summary>
        public AffinityTypes? AffinityType { get; set; }

        /// <summary>
        /// The value of the attribute.
        /// Scaled based on rarity and tier.
        /// </summary>
        public float Value { get; set; }

        /// <summary>
        /// True if this attribute was added as an influence bonus.
        /// Influence bonuses are separate from the main explicit attribute count.
        /// </summary>
        public bool IsInfluenceBonus { get; set; }
    }
}
