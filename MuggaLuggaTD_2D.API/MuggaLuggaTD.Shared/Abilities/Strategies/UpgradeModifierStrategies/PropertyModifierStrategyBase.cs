using Abilities.Models;
using System;
using System.Collections.Generic;

namespace Abilities.Strategies.UpgradeModifierStrategies
{
    public abstract class PropertyModifierStrategyBase : IAbilityUpgradeModifierStrategy
    {
        /// <summary>The property an upgrade means by "Damage": every affinity the ability deals.</summary>
        private const string DamageProperty = "Damage";

        // Content writes the name a designer would; this maps it to the property that holds it.
        private static readonly Dictionary<string, string> PropertyNameMap = new Dictionary<string, string>
        {
            { "Cooldown", "ActivationCooldown" }
        };

        public void ApplyModifier(IGameAbility gameAbility, AbilityModifier abilityModifier)
        {
            if (abilityModifier.Value == null)
                return;

            // Damage is not a property of the ability - it is one per affinity the ability deals, so
            // it cannot be reached by reflecting over GameAbility.
            //
            // It used to be mapped to "Range" instead, which meant every "Increase damage by 10%"
            // upgrade in the game silently increased the ability's RANGE and left its damage exactly
            // where it started. That is most of the upgrade content, on every ability.
            if (string.Equals(abilityModifier.Property, DamageProperty, StringComparison.Ordinal))
            {
                ApplyToEveryAffinity(gameAbility, abilityModifier.Value.Value);
                return;
            }

            var propertyName = abilityModifier.Property;
            if (propertyName != null && PropertyNameMap.TryGetValue(propertyName, out var mappedName))
            {
                propertyName = mappedName;
            }

            if (string.IsNullOrEmpty(propertyName))
                return;

            var propertyInfo = gameAbility.GetType().GetProperty(propertyName);
            if (propertyInfo == null)
                return;

            var currentValue = propertyInfo.GetValue(gameAbility);

            if (currentValue is AbilityModifiableProperty<float?> floatProp)
            {
                EnsureInitialized(floatProp);
                ApplyToFloat(floatProp, abilityModifier.Value.Value);
            }
            else if (currentValue is AbilityModifiableProperty<double?> doubleProp)
            {
                EnsureInitialized(doubleProp);
                ApplyToDouble(doubleProp, abilityModifier.Value.Value);
            }
            else if (currentValue is AbilityModifiableProperty<long?> longProp)
            {
                EnsureInitialized(longProp);
                ApplyToLong(longProp, abilityModifier.Value.Value);
            }
        }

        /// <summary>
        /// A damage modifier moves every affinity the ability deals, each by its own base - so a
        /// multiplier means the same thing to a single-affinity ability and to one that has been
        /// given a second affinity by an earlier upgrade.
        /// </summary>
        private void ApplyToEveryAffinity(IGameAbility gameAbility, double value)
        {
            if (gameAbility.AffinityStats == null)
                return;

            foreach (var stat in gameAbility.AffinityStats)
            {
                if (stat?.Damage == null)
                    continue;

                EnsureInitialized(stat.Damage);
                ApplyToLong(stat.Damage, value);
            }
        }

        protected abstract void EnsureInitialized(AbilityModifiableProperty<float?> prop);
        protected abstract void EnsureInitialized(AbilityModifiableProperty<double?> prop);
        protected abstract void EnsureInitialized(AbilityModifiableProperty<long?> prop);
        protected abstract void ApplyToFloat(AbilityModifiableProperty<float?> prop, double value);
        protected abstract void ApplyToDouble(AbilityModifiableProperty<double?> prop, double value);
        protected abstract void ApplyToLong(AbilityModifiableProperty<long?> prop, double value);
    }
}
