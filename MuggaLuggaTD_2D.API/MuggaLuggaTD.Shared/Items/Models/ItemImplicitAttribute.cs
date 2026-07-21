using Enums;

namespace Items.Models
{
    /// <summary>
    /// Represents an implicit attribute on an item.
    /// Each item has exactly one implicit attribute with a static, tier-based value.
    /// </summary>
    public class ItemImplicitAttribute
    {
        /// <summary>
        /// The type of implicit attribute.
        /// </summary>
        public ItemImplicitTypes Type { get; set; }

        /// <summary>
        /// The value of the attribute. Static and based on item tier.
        /// For percentage types (CooldownReduction, MovementSpeed, IncreasedRange): 1-10%
        /// For count types (ProjectilePierceCount, ProjectileCount, ChainingCount): 1-2
        /// </summary>
        public float Value { get; set; }
    }
}
