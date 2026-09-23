using System.Collections.Generic;
using System.Linq;
using Abilities.Models;
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

        /// <summary>
        /// What this signature grows into as the character levels. Sparse: a stage with no entry
        /// simply grants nothing, and stage I never has one because stage I <i>is</i> the signature.
        /// See <see cref="AwakeningRules"/>.
        /// </summary>
        public List<AwakeningStageDefinition> Awakening { get; set; }
            = new List<AwakeningStageDefinition>();
    }

    /// <summary>
    /// One rung of a signature's awakening ladder: the modifiers it adds, and the name it is shown
    /// under. Design doc 05 section 2.
    /// </summary>
    public class AwakeningStageDefinition
    {
        public AwakeningStage Stage { get; set; }

        /// <summary>
        /// What the player sees. Apex is named per signature ("Winter's Mark"); the earlier stages
        /// usually leave this empty and fall back to "Awakening II".
        /// </summary>
        public string Name { get; set; }

        public string Description { get; set; }

        /// <summary>The modifiers this stage adds, applied to the character's signature ability.</summary>
        public List<AbilityModifier> Modifiers { get; set; } = new List<AbilityModifier>();

        /// <summary>
        /// Per-affinity replacements for <see cref="Modifiers"/>, for a stage that should do something
        /// different depending on what the signature is made of. Sparse.
        /// </summary>
        public List<AwakeningAffinityModifiers> AffinityModifiers { get; set; }
            = new List<AwakeningAffinityModifiers>();

        /// <summary>This stage's modifiers for an affinity: its override if it has one, else the default set.</summary>
        public List<AbilityModifier> ModifiersFor(AffinityTypes affinity)
        {
            var over = AffinityModifiers?.FirstOrDefault(a => a != null && a.AffinityType == affinity);
            return over?.Modifiers != null && over.Modifiers.Count > 0 ? over.Modifiers : Modifiers;
        }

        /// <summary>Its given name, or "Awakening &lt;stage&gt;" when it has none.</summary>
        public string DisplayName(AwakeningStage stage)
            => string.IsNullOrWhiteSpace(Name) ? "Awakening " + stage : Name;
    }

    /// <summary>One affinity's override of an awakening stage's modifiers.</summary>
    public class AwakeningAffinityModifiers
    {
        public AffinityTypes AffinityType { get; set; }
        public List<AbilityModifier> Modifiers { get; set; } = new List<AbilityModifier>();
    }

    /// <summary>One affinity's replacement ability for a signature.</summary>
    public class SignatureAffinityAbility
    {
        public AffinityTypes AffinityType { get; set; }
        public string AbilityLinkName { get; set; }
    }
}
