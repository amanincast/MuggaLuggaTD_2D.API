using System.Collections.Generic;
using System.Linq;
using Abilities.Handlers;
using MuggaLuggaTD.Shared.Animations;
using Enums;

namespace Abilities.Models
{
    public interface IGameAbility
    {
        string PrefabAssetLocation { get; set; }
        string SpriteLibraryAssetLocation { get; set; }
        string AbilityName { get; set; }
        string AbilityLinkName { get; set; }
        string Description { get; set; }
        AbilityModifiableProperty<float?> Range { get; set; }
        float TravelSpeed { get; set; }
        int ManaCost { get; set; }
        AbilityModifiableProperty<double?> ActivationCooldown { get; set; }
        float Duration { get; set; }
        string AnimationPrefix { get; set; }
        int Level { get; set; }
        List<AbilityAffinityStat> AffinityStats { get; set; }
        List<string> ValidTargetTags { get; set; }
        int? MaxActivations { get; set; }
        AbilityClassifications Classification { get; set; }
        AbilityActivationTracker ActivationTracker { get; set; }
        List<AbilityUpgrade> AppliedUpgrades { get; set; }
        List<AbilityUpgrade> DerivedUpgrades { get; set; }
        List<AbilityUpgrade> AvailableUpgrades { get; set; }
        AbilityAnimationState AnimationState { get; set; }
        AbilityMovementTypes MovementType { get; set; }
        float? TelegraphDuration { get; set; }
        AbilityModifiableProperty<double?> CollisionScale { get; set; }
        AbilityModifiableProperty<long?> PierceCount { get; set; }
        AbilityModifiableProperty<long?> ProjectileCount { get; set; }
        AbilityModifiableProperty<long?> ChainCount { get; set; }
        List<AffinityTypes> GetAffinityTypes();
        void ApplyAbilityUpgrade(AbilityUpgrade abilityUpgrade);
    }

    public class GameAbility : IGameAbility
    {
        public string PrefabAssetLocation { get; set; }
        public string SpriteLibraryAssetLocation { get; set; }
        public string AbilityName { get; set; }
        public string AbilityLinkName { get; set; }
        public string Description { get; set; }
        // Only BaseValue is defaulted. The adjusted layers MUST stay null until something actually
        // adjusts them.
        //
        // GetCurrentValue() returns the first non-default layer — AdjustedValue, then
        // AdjustedBaseValue, then BaseValue — so pre-filling the adjusted layers makes them win over
        // whatever the content says. Deserialization only writes BaseValue (the JSON is
        // "Range": { "BaseValue": 20.0 }) and Newtonsoft populates this existing instance rather than
        // replacing it, so the defaults survive. The result was that EVERY ability in the game had an
        // effective range of exactly 5 regardless of its data: Bow Attack 20 -> 5, Magic Missile
        // 10 -> 5, and melee 1.5 -> 5, which let melee enemies reach three times further than
        // intended. The same trap applied to every property below.
        public AbilityModifiableProperty<float?> Range { get; set; } = new AbilityModifiableProperty<float?>
        {
            BaseValue = 5f
        };
        public float TravelSpeed { get; set; }
        public int ManaCost { get; set; }
        public AbilityModifiableProperty<double?> ActivationCooldown { get; set; }
        public float Duration { get; set; }
        public string AnimationPrefix { get; set; }
        public int Level { get; set; }
        public List<AbilityAffinityStat> AffinityStats { get; set; }
        public List<string> ValidTargetTags { get; set; }
        public int? MaxActivations { get; set; }
        public AbilityClassifications Classification { get; set; } = AbilityClassifications.NotSpecified;
        public AbilityActivationTracker ActivationTracker { get; set; } = new AbilityActivationTracker();
        public List<AbilityUpgrade> AppliedUpgrades { get; set; } = new List<AbilityUpgrade>();

        // What the character IS, as opposed to what the player picked. Awakening writes here
        // (AwakeningRules), and nothing else does.
        //
        // It is deliberately NOT saved and NOT validated against an ability's upgrade pool: it is
        // re-derived from signature, affinity, rarity and level on every load, so it cannot be forged
        // into a save and AbilityUpgradeValidator cannot strip it out of one. Keeping it separate is
        // also what stops a level-up pick from wiping it - ApplyAbilityUpgrade replays from the base,
        // so awakening has to be part of what gets replayed.
        public List<AbilityUpgrade> DerivedUpgrades { get; set; } = new List<AbilityUpgrade>();
        public List<AbilityUpgrade> AvailableUpgrades { get; set; } = new List<AbilityUpgrade>();
        public AbilityAnimationState AnimationState { get; set; } = new AbilityAnimationState();
        public AbilityMovementTypes MovementType { get; set; }
        public float? TelegraphDuration { get; set; }
        public AbilityModifiableProperty<double?> CollisionScale { get; set; } = new AbilityModifiableProperty<double?>
        {
            BaseValue = 1.0
        };
        public AbilityModifiableProperty<long?> PierceCount { get; set; } = new AbilityModifiableProperty<long?>
        {
            BaseValue = 0
        };
        public AbilityModifiableProperty<long?> ProjectileCount { get; set; } = new AbilityModifiableProperty<long?>
        {
            BaseValue = 1
        };
        public AbilityModifiableProperty<long?> ChainCount { get; set; } = new AbilityModifiableProperty<long?>
        {
            BaseValue = 0
        };

        // ---- Support: what a cast does for the caster's own side (the Cleric, design doc 01) ----
        //
        // A support ability is still an attack - it fires at an enemy and deals its affinity like any
        // other, so its affinity, status effect, resonance and reactions all still mean something.
        // These ride along with the cast. They are modifiable properties so awakening and level-up
        // picks reach them by name ("Healing", "HealTargetCount", "SupportRadius") exactly as they
        // reach "Damage" and "CollisionScale".

        /// <summary>Health restored per cast to each target <see cref="HealTargets"/> picks. Counted
        /// like damage in power: a heal is negative damage, so it is priced on the same scale.</summary>
        public AbilityModifiableProperty<long?> Healing { get; set; } = new AbilityModifiableProperty<long?>
        {
            BaseValue = 0
        };

        /// <summary>How many of the most wounded a <see cref="AbilitySupportTargeting.MostWounded"/> heal reaches.</summary>
        public AbilityModifiableProperty<long?> HealTargetCount { get; set; } = new AbilityModifiableProperty<long?>
        {
            BaseValue = 1
        };

        /// <summary>The reach of a support effect centred on the caster: the heal of
        /// <see cref="AbilitySupportTargeting.AlliesNearCaster"/>, and the ward.</summary>
        public AbilityModifiableProperty<float?> SupportRadius { get; set; } = new AbilityModifiableProperty<float?>
        {
            BaseValue = 0f
        };

        public AbilitySupportTargeting HealTargets { get; set; } = AbilitySupportTargeting.None;

        /// <summary>Share of incoming damage a ward takes off the allies it covers (0.3 = 30% less).
        /// Zero means the ability wards nobody.</summary>
        public float WardReduction { get; set; }

        public float WardSeconds { get; set; }

        /// <summary>True for an ability that does anything for the caster's side.</summary>
        public bool IsSupport =>
            (HealTargets != AbilitySupportTargeting.None && (Healing?.GetCurrentValue() ?? 0) > 0)
            || WardReduction > 0f;

        public List<AffinityTypes> GetAffinityTypes()
        {
            return AffinityStats.Select(stat => stat.AffinityType).ToList();
        }

        public void ApplyAbilityUpgrade(AbilityUpgrade abilityUpgrade)
        {
            AppliedUpgrades.Add(abilityUpgrade);

            // Derived first, then picked. ApplyUpgrades replays everything from the base, so leaving
            // DerivedUpgrades out here would silently undo awakening the moment a player took a
            // level-up upgrade.
            UpgradeModifierHandler.ApplyUpgrades(
                this, (DerivedUpgrades ?? new List<AbilityUpgrade>()).Concat(AppliedUpgrades).ToList());
        }
    }
}
