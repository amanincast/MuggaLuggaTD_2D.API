namespace Enums
{
    /// <summary>
    /// What a strike covers when it lands. <see cref="Point"/> is everything before the Warrior spec:
    /// the ability's own collider decides what it touches.
    /// </summary>
    public enum AbilityShapes : short
    {
        Point = 0,

        /// <summary>A sweep in front of the caster: every enemy within reach and inside the arc.</summary>
        Arc = 1,

        /// <summary>A ring around the caster that hits everything inside it, tick by tick.</summary>
        Aura = 2
    }
}
