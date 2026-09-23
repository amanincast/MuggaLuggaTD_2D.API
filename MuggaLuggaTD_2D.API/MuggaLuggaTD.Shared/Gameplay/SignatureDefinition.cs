using System.Collections.Generic;
using Enums;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>Wrapper matching the SignatureData content document's shape.</summary>
    public class SignatureContentDocument
    {
        public List<SignatureDefinition> Signatures { get; set; } = new List<SignatureDefinition>();
    }

    /// <summary>
    /// A signature: the ability that makes this Warrior different from that one.
    ///
    /// <para>A character is a <b>class</b> (its basic and how fast it gains health), a
    /// <b>signature</b> (this) and an <b>affinity</b> (what the signature is made of). The class is
    /// what everyone of that kind can do; the signature and affinity are the roll, and the thing the
    /// Tavern is worth hunting through. Design doc 05 §1.</para>
    ///
    /// <para>An affinity is <i>not</i> a separate ability per combination. It is this signature's
    /// ability retuned to that affinity, so 6 signatures x 8 affinities costs 6 abilities of work
    /// rather than 48. <see cref="AffinityAbilities"/> is the exception for signatures whose
    /// affinities really are different spells - the mage's Blast is a different explosion for each -
    /// and it is a sparse override, not a requirement.</para>
    /// </summary>
    public class SignatureDefinition
    {
        /// <summary>Stable id. This is what a save and a hire record store, so it must not change.</summary>
        public string SignatureId { get; set; }

        /// <summary>What the player sees: "Snipe Shot", "Ground Slam".</summary>
        public string DisplayName { get; set; }

        public string Description { get; set; }

        /// <summary>Which class may roll this. Matches <c>CharacterClassData.ClassName</c>.</summary>
        public string Class { get; set; }

        /// <summary>The ability this signature is, before any per-affinity override.</summary>
        public string BaseAbilityLinkName { get; set; }

        /// <summary>
        /// Which affinities this signature may roll. Empty means all eight; the mage signatures list
        /// seven because a bolt of Physical is not a thing.
        /// </summary>
        public List<AffinityTypes> AllowedAffinities { get; set; } = new List<AffinityTypes>();

        /// <summary>
        /// Per-affinity ability replacements, where an affinity is genuinely a different spell rather
        /// than the same one in another colour. Sparse: an affinity with no entry uses
        /// <see cref="BaseAbilityLinkName"/> retuned.
        /// </summary>
        public List<SignatureAffinityAbility> AffinityAbilities { get; set; }
            = new List<SignatureAffinityAbility>();
    }

    /// <summary>One affinity's replacement ability for a signature.</summary>
    public class SignatureAffinityAbility
    {
        public AffinityTypes AffinityType { get; set; }
        public string AbilityLinkName { get; set; }
    }
}
