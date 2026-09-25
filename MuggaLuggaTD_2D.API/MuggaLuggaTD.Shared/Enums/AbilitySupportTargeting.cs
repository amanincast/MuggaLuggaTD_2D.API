namespace Enums
{
    /// <summary>
    /// Who an ability's <c>Healing</c> lands on. A support ability still fires at an enemy like any
    /// other - the healing rides along with the cast - so it needs no targeting of its own beyond this.
    /// </summary>
    public enum AbilitySupportTargeting : short
    {
        /// <summary>Heals nobody. Every ability that is not a support one.</summary>
        None = 0,

        /// <summary>The most wounded of the caster's side within the ability's range - as many of them
        /// as <c>HealTargetCount</c> says, most wounded first.</summary>
        MostWounded = 1,

        /// <summary>Everyone on the caster's side within <c>SupportRadius</c> of the caster.</summary>
        AlliesNearCaster = 2
    }
}
