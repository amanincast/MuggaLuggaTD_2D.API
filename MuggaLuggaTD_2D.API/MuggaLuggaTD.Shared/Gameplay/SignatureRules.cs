using System;
using System.Collections.Generic;
using System.Linq;
using Abilities.Models;
using Enums;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// Turns a character's signature and affinity into the ability it actually casts.
    ///
    /// <para>Shared because both sides need the same answer for different reasons: the client builds
    /// the kit it fights with, and the server prices a party's power from the same roll. If the two
    /// resolved a signature differently, a Fire Blast would be worth one number in the fight and
    /// another in PvP.</para>
    ///
    /// <para><b>Retuning preserves total damage.</b> A signature's affinity decides its damage
    /// <i>type</i> and therefore its status effect, not how hard it hits - so no affinity is the one
    /// to roll for, and the chase is for the combination rather than for Fire. Design doc 05 §1.</para>
    /// </summary>
    public static class SignatureRules
    {
        /// <summary>Every affinity, in enum order. What a signature rolls from when it names none.</summary>
        public static readonly IReadOnlyList<AffinityTypes> AllAffinities = new[]
        {
            AffinityTypes.Physical, AffinityTypes.Fire, AffinityTypes.Water, AffinityTypes.Earth,
            AffinityTypes.Air, AffinityTypes.Light, AffinityTypes.Dark, AffinityTypes.Arcane
        };

        /// <summary>The signature with this id, or null. Ids are compared exactly - they are data keys.</summary>
        public static SignatureDefinition Find(IEnumerable<SignatureDefinition> signatures, string signatureId)
        {
            if (signatures == null || string.IsNullOrEmpty(signatureId))
                return null;

            return signatures.FirstOrDefault(s =>
                s != null && string.Equals(s.SignatureId, signatureId, StringComparison.Ordinal));
        }

        /// <summary>The signatures a class may roll, in content order.</summary>
        public static IReadOnlyList<SignatureDefinition> ForClass(
            IEnumerable<SignatureDefinition> signatures, string className)
        {
            if (signatures == null || string.IsNullOrEmpty(className))
                return Array.Empty<SignatureDefinition>();

            return signatures
                .Where(s => s != null && string.Equals(s.Class, className, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// The affinities this signature may roll. A signature that names none may roll all eight,
        /// which keeps the common case out of content.
        /// </summary>
        public static IReadOnlyList<AffinityTypes> AffinitiesFor(SignatureDefinition signature)
        {
            if (signature?.AllowedAffinities == null || signature.AllowedAffinities.Count == 0)
                return AllAffinities;

            return signature.AllowedAffinities.Distinct().ToList();
        }

        public static bool IsAffinityAllowed(SignatureDefinition signature, AffinityTypes affinity)
        {
            return signature != null && AffinitiesFor(signature).Contains(affinity);
        }

        /// <summary>
        /// Which ability this signature is at <paramref name="affinity"/>: the per-affinity override
        /// when content names one, otherwise the base ability. Null when the signature is null.
        /// </summary>
        public static string AbilityLinkFor(SignatureDefinition signature, AffinityTypes affinity)
        {
            if (signature == null)
                return null;

            var over = signature.AffinityAbilities?
                .FirstOrDefault(a => a != null && a.AffinityType == affinity && !string.IsNullOrEmpty(a.AbilityLinkName));

            return over?.AbilityLinkName ?? signature.BaseAbilityLinkName;
        }

        /// <summary>
        /// Whether <paramref name="abilityLinkName"/> is this signature's own ability for this
        /// affinity - as opposed to one of the class basics sitting beside it in the kit.
        ///
        /// <para>Only the signature is retuned and awakened, so every caller that rebuilds a kit or
        /// prices one has to make this distinction the same way. Making it a rule here rather than a
        /// comparison at each call site is what keeps the client's kit and the server's power from
        /// disagreeing about which ability grows.</para>
        /// </summary>
        public static bool IsSignatureAbility(
            SignatureDefinition signature, AffinityTypes affinity, string abilityLinkName)
        {
            if (signature == null || string.IsNullOrEmpty(abilityLinkName))
                return false;

            return string.Equals(
                AbilityLinkFor(signature, affinity), abilityLinkName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Retunes <paramref name="ability"/> to <paramref name="affinity"/> in place: one affinity
        /// stat, carrying the damage the template's stats carried between them.
        ///
        /// <para>Summing rather than keeping the first matters for an ability that already deals two
        /// types - retuning it must not quietly halve it. An ability with no affinity stats at all
        /// (a shield, a dodge) is left alone: it has no damage to retune, and inventing a zero-damage
        /// stat would make it look like a damage ability to everything downstream.</para>
        ///
        /// <para>All three layers are written. <c>GetCurrentValue()</c> prefers AdjustedValue, so
        /// setting only BaseValue would leave the template's old affinity damage in front of the new
        /// one - the same trap that made every ability's range 5.</para>
        /// </summary>
        public static void Retune(IGameAbility ability, AffinityTypes affinity)
        {
            if (ability?.AffinityStats == null || ability.AffinityStats.Count == 0)
                return;

            long damage = ability.AffinityStats
                .Where(s => s != null)
                .Sum(s => s.GetDamageValue());

            ability.AffinityStats = new List<AbilityAffinityStat>
            {
                new AbilityAffinityStat
                {
                    AffinityType = affinity,
                    Damage = new AbilityModifiableProperty<long?>
                    {
                        BaseValue = damage,
                        AdjustedBaseValue = damage,
                        AdjustedValue = damage
                    }
                }
            };
        }

        /// <summary>
        /// The signature's ability, resolved and retuned, cloned from <paramref name="templates"/>.
        /// Null when the signature is unknown or names an ability content does not have - a caller
        /// should fall back to the character's own list rather than leaving it weaponless.
        /// </summary>
        public static GameAbility ResolveAbility(
            SignatureDefinition signature,
            AffinityTypes affinity,
            IReadOnlyCollection<GameAbility> templates)
        {
            var linkName = AbilityLinkFor(signature, affinity);
            if (string.IsNullOrEmpty(linkName) || templates == null)
                return null;

            var template = templates.FirstOrDefault(a =>
                a != null && string.Equals(a.AbilityLinkName, linkName, StringComparison.OrdinalIgnoreCase));

            if (template == null)
                return null;

            var ability = AbilityResolver.CloneTemplate(template);
            Retune(ability, affinity);
            return ability;
        }
    }
}
