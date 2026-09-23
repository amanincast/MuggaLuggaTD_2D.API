using System.Collections.Generic;
using Abilities.Models;
using Enums;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// Everything needed to decide how far a character's signature has woken up, carried together so
    /// <see cref="AbilityResolver.Resolve"/> does not grow four more parameters.
    ///
    /// <para>Deliberately a value carrier with no behaviour of its own beyond delegating to
    /// <see cref="AwakeningRules"/> - the rules stay in one place, and both sides build this the same
    /// way from a character's own fields.</para>
    /// </summary>
    public class AwakeningContext
    {
        public AwakeningContext(SignatureDefinition signature, AffinityTypes affinity,
            CharacterRarity rarity, long level)
        {
            Signature = signature;
            Affinity = affinity;
            Rarity = rarity;
            Level = level;
        }

        public SignatureDefinition Signature { get; }
        public AffinityTypes Affinity { get; }
        public CharacterRarity Rarity { get; }
        public long Level { get; }

        /// <summary>The stage this character is at.</summary>
        public AwakeningStage Stage => AwakeningRules.StageFor(Rarity, Level);

        /// <summary>Applies this character's awakening to one of its abilities.</summary>
        public void ApplyTo(IGameAbility ability)
            => AwakeningRules.Apply(ability, Signature, Affinity, Rarity, Level);

        /// <summary>
        /// Builds the context for a character, or null when it has no signature to awaken - an enemy,
        /// a boss, or an ally from before signatures existed.
        /// </summary>
        public static AwakeningContext For(
            IEnumerable<SignatureDefinition> signatures, string signatureId, AffinityTypes? affinity,
            CharacterRarity rarity, long level)
        {
            if (string.IsNullOrEmpty(signatureId) || !affinity.HasValue)
                return null;

            var signature = SignatureRules.Find(signatures, signatureId);
            return signature == null ? null : new AwakeningContext(signature, affinity.Value, rarity, level);
        }
    }
}
