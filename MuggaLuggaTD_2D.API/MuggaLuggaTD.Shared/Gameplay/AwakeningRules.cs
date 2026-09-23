using System.Collections.Generic;
using System.Linq;
using Abilities.Handlers;
using Abilities.Models;
using Enums;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// What a character's signature has grown into. Design doc 05 section 2.
    ///
    /// <para><b>Rarity sets the ceiling, level sets the climb.</b> A Common reaches stage II, a Rare
    /// III, an Epic IV, and only a Legendary reaches Apex - but a Common with the signature and
    /// affinity a player wanted is still worth levelling, which is the whole point of rolling rarity
    /// separately from the roll it decorates.</para>
    ///
    /// <para><b>The stage is derived, never stored.</b> It is a pure function of rarity and level, so
    /// a save cannot claim stage IV at level 1, and the server recomputes the same answer the client
    /// shows without trusting anything. That is why this lives in the shared assembly next to
    /// <see cref="SignatureRules"/> rather than on either side.</para>
    ///
    /// <para><b>Awakening is not a picked upgrade.</b> Its modifiers go into
    /// <see cref="IGameAbility.DerivedUpgrades"/>, which is what the character <i>is</i>, separate
    /// from <see cref="IGameAbility.AppliedUpgrades"/>, which is what the player <i>chose</i>. Only
    /// the latter is saved, so awakening cannot be forged into a save and cannot be stripped out of
    /// one by <see cref="AbilityUpgradeValidator"/>.</para>
    /// </summary>
    public static class AwakeningRules
    {
        /// <summary>Levels each stage unlocks at. IV and Apex share level 30; rarity separates them.</summary>
        public const int StageIILevel = 10;
        public const int StageIIILevel = 20;
        public const int StageIVLevel = 30;
        public const int ApexLevel = 30;

        private static readonly AwakeningStage[] Ladder =
        {
            AwakeningStage.I, AwakeningStage.II, AwakeningStage.III, AwakeningStage.IV, AwakeningStage.Apex
        };

        /// <summary>The level a stage unlocks at.</summary>
        public static int LevelFor(AwakeningStage stage)
        {
            switch (stage)
            {
                case AwakeningStage.I: return 1;
                case AwakeningStage.II: return StageIILevel;
                case AwakeningStage.III: return StageIIILevel;
                case AwakeningStage.IV: return StageIVLevel;
                case AwakeningStage.Apex: return ApexLevel;
                default: return 1;
            }
        }

        /// <summary>The furthest stage a rarity may ever reach, however high the level.</summary>
        public static AwakeningStage CeilingFor(CharacterRarity rarity)
        {
            switch (rarity)
            {
                case CharacterRarity.Rare: return AwakeningStage.III;
                case CharacterRarity.Epic: return AwakeningStage.IV;
                case CharacterRarity.Legendary: return AwakeningStage.Apex;
                default: return AwakeningStage.II;
            }
        }

        /// <summary>
        /// The stage a character of this rarity is at, at this level: the highest rung it has both
        /// levelled into and is allowed to stand on.
        /// </summary>
        public static AwakeningStage StageFor(CharacterRarity rarity, long level)
        {
            var ceiling = CeilingFor(rarity);
            var reached = AwakeningStage.I;

            foreach (var stage in Ladder)
            {
                if (stage > ceiling) break;
                if (level >= LevelFor(stage)) reached = stage;
            }

            return reached;
        }

        /// <summary>
        /// The next stage this character can still reach, and the level it needs - or null when it is
        /// already at its ceiling. This is what the Guild Hall shows a player deciding who to level.
        /// </summary>
        public static AwakeningStage? NextStageFor(CharacterRarity rarity, long level)
        {
            var ceiling = CeilingFor(rarity);
            var current = StageFor(rarity, level);

            foreach (var stage in Ladder)
            {
                if (stage > ceiling) break;
                if (stage > current) return stage;
            }

            return null;
        }

        /// <summary>
        /// The awakening upgrades for a character, <b>cumulative</b> through every stage it has
        /// reached: stage II's potency is still there once stage IV's spike lands on top of it.
        /// Stage I contributes nothing, because stage I <i>is</i> the signature.
        /// </summary>
        public static List<AbilityUpgrade> UpgradesFor(
            SignatureDefinition signature, AffinityTypes affinity, CharacterRarity rarity, long level)
        {
            var upgrades = new List<AbilityUpgrade>();
            if (signature?.Awakening == null || signature.Awakening.Count == 0)
                return upgrades;

            var current = StageFor(rarity, level);

            foreach (var stage in Ladder)
            {
                if (stage > current) break;

                var definition = signature.Awakening.FirstOrDefault(a => a != null && a.Stage == stage);
                if (definition == null) continue;

                var modifiers = definition.ModifiersFor(affinity);
                if (modifiers == null || modifiers.Count == 0) continue;

                upgrades.Add(new AbilityUpgrade
                {
                    Name = definition.DisplayName(stage),
                    Description = definition.Description,
                    Modifiers = modifiers.ToList()
                });
            }

            return upgrades;
        }

        /// <summary>
        /// Applies awakening to an ability in place.
        ///
        /// <para>The modifiers are recorded on <see cref="IGameAbility.DerivedUpgrades"/> and then the
        /// whole pipeline is re-run over derived <i>and</i> applied together. Re-running both is what
        /// keeps a later level-up pick from silently dropping awakening: <c>ApplyAbilityUpgrade</c>
        /// replays from the base, so awakening has to be part of what it replays.</para>
        /// </summary>
        public static void Apply(
            IGameAbility ability, SignatureDefinition signature, AffinityTypes affinity,
            CharacterRarity rarity, long level)
        {
            if (ability == null) return;

            var derived = UpgradesFor(signature, affinity, rarity, level);
            ability.DerivedUpgrades = derived;

            if (derived.Count == 0) return;

            UpgradeModifierHandler.ApplyUpgrades(
                ability, derived.Concat(ability.AppliedUpgrades ?? new List<AbilityUpgrade>()).ToList());
        }
    }
}
