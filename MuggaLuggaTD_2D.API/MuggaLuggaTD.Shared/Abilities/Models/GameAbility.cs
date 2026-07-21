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
        public AbilityModifiableProperty<float?> Range { get; set; } = new AbilityModifiableProperty<float?>
        {
            BaseValue = 5f,
            AdjustedBaseValue = 5f,
            AdjustedValue = 5f
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
        public List<AbilityUpgrade> AvailableUpgrades { get; set; } = new List<AbilityUpgrade>();
        public AbilityAnimationState AnimationState { get; set; } = new AbilityAnimationState();
        public AbilityMovementTypes MovementType { get; set; }
        public float? TelegraphDuration { get; set; }
        public AbilityModifiableProperty<double?> CollisionScale { get; set; } = new AbilityModifiableProperty<double?>
        {
            BaseValue = 1.0,
            AdjustedBaseValue = 1.0,
            AdjustedValue = 1.0
        };
        public AbilityModifiableProperty<long?> PierceCount { get; set; } = new AbilityModifiableProperty<long?>
        {
            BaseValue = 0,
            AdjustedBaseValue = 0,
            AdjustedValue = 0
        };
        public AbilityModifiableProperty<long?> ProjectileCount { get; set; } = new AbilityModifiableProperty<long?>
        {
            BaseValue = 1,
            AdjustedBaseValue = 1,
            AdjustedValue = 1
        };
        public AbilityModifiableProperty<long?> ChainCount { get; set; } = new AbilityModifiableProperty<long?>
        {
            BaseValue = 0,
            AdjustedBaseValue = 0,
            AdjustedValue = 0
        };

        public List<AffinityTypes> GetAffinityTypes()
        {
            return AffinityStats.Select(stat => stat.AffinityType).ToList();
        }

        public void ApplyAbilityUpgrade(AbilityUpgrade abilityUpgrade)
        {
            AppliedUpgrades.Add(abilityUpgrade);
            UpgradeModifierHandler.ApplyUpgrades(this, AppliedUpgrades);
        }
    }
}
