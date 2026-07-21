namespace Enums
{
    /// <summary>
    /// Types of implicit attributes that can appear on items.
    /// Each item has exactly one implicit attribute, which is static and tier-based.
    /// </summary>
    public enum ItemImplicitTypes : short
    {
        CooldownReduction = 0,
        MovementSpeed = 1,
        IncreasedRange = 2,
        ProjectilePierceCount = 3,
        ProjectileCount = 4,
        ChainingCount = 5
    }
}
