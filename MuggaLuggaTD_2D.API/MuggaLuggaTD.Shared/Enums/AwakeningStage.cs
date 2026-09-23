namespace Enums
{
    /// <summary>
    /// How far a character's signature has woken up. Design doc 05 section 2.
    ///
    /// <para>The stage is <b>derived</b> from rarity and level, never stored: rarity sets the ceiling
    /// and level sets the climb. That is what makes rarity worth rolling for without making a Common
    /// with the right signature worthless.</para>
    /// </summary>
    public enum AwakeningStage : short
    {
        /// <summary>Not a stage. Used for "no awakening applies".</summary>
        None = 0,

        /// <summary>The signature itself, retuned to its affinity. Every character has this from hire.</summary>
        I = 1,

        /// <summary>Potency: the signature hits harder, which sharpens its status effect with it.</summary>
        II = 2,

        /// <summary>Shape: what the signature does changes - more projectiles, a wider area, more pierce.</summary>
        III = 3,

        /// <summary>Spike: a second power step and a shorter cooldown.</summary>
        IV = 4,

        /// <summary>The Legendary-only capstone, named per signature.</summary>
        Apex = 5
    }
}
