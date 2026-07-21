using System;
using Abilities.Models;

namespace Abilities.Strategies.UpgradeModifierStrategies
{
    public class MultiplierIncreaseStrategy : PropertyModifierStrategyBase
    {
        protected override void EnsureInitialized(AbilityModifiableProperty<float?> prop)
        {
            if (!prop.AdjustedBaseValue.HasValue)
                prop.AdjustedBaseValue = prop.BaseValue;
            if (!prop.AdjustedValue.HasValue)
                prop.AdjustedValue = prop.GetCurrentValue();
        }

        protected override void EnsureInitialized(AbilityModifiableProperty<double?> prop)
        {
            if (!prop.AdjustedBaseValue.HasValue)
                prop.AdjustedBaseValue = prop.BaseValue;
            if (!prop.AdjustedValue.HasValue)
                prop.AdjustedValue = prop.GetCurrentValue();
        }

        protected override void EnsureInitialized(AbilityModifiableProperty<long?> prop)
        {
            if (!prop.AdjustedBaseValue.HasValue)
                prop.AdjustedBaseValue = prop.BaseValue;
            if (!prop.AdjustedValue.HasValue)
                prop.AdjustedValue = prop.GetCurrentValue();
        }

        protected override void ApplyToFloat(AbilityModifiableProperty<float?> prop, double value)
        {
            prop.AdjustedValue += (float)(prop.AdjustedBaseValue.GetValueOrDefault() * value);
        }

        protected override void ApplyToDouble(AbilityModifiableProperty<double?> prop, double value)
        {
            prop.AdjustedValue += prop.AdjustedBaseValue * value;
        }

        protected override void ApplyToLong(AbilityModifiableProperty<long?> prop, double value)
        {
            prop.AdjustedValue += (long)Math.Round(prop.AdjustedBaseValue.GetValueOrDefault() * value);
        }
    }
}
