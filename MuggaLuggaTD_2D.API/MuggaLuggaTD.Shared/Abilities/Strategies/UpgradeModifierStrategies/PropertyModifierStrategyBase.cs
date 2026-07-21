using Abilities.Models;
using System.Collections.Generic;

namespace Abilities.Strategies.UpgradeModifierStrategies
{
    public abstract class PropertyModifierStrategyBase : IAbilityUpgradeModifierStrategy
    {
        // Map user-facing property names to actual GameAbility property names
        private static readonly Dictionary<string, string> PropertyNameMap = new Dictionary<string, string>
        {
            { "Damage", "Range" },
            { "Cooldown", "ActivationCooldown" }
        };

        public void ApplyModifier(IGameAbility gameAbility, AbilityModifier abilityModifier)
        {
            if (abilityModifier.Value == null)
                return;

            // Map the property name if an alias exists
            var propertyName = abilityModifier.Property;
            if (PropertyNameMap.TryGetValue(propertyName, out var mappedName))
            {
                propertyName = mappedName;
            }

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

        protected abstract void EnsureInitialized(AbilityModifiableProperty<float?> prop);
        protected abstract void EnsureInitialized(AbilityModifiableProperty<double?> prop);
        protected abstract void EnsureInitialized(AbilityModifiableProperty<long?> prop);
        protected abstract void ApplyToFloat(AbilityModifiableProperty<float?> prop, double value);
        protected abstract void ApplyToDouble(AbilityModifiableProperty<double?> prop, double value);
        protected abstract void ApplyToLong(AbilityModifiableProperty<long?> prop, double value);
    }
}
