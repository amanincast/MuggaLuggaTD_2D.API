using Abilities.Models;

namespace Abilities.Strategies.UpgradeModifierStrategies
{
    public class FlatDecreaseStrategy : PropertyModifierStrategyBase
    {
        protected override void EnsureInitialized(AbilityModifiableProperty<float?> prop)
        {
            if (!prop.AdjustedValue.HasValue)
                prop.AdjustedValue = prop.GetCurrentValue();
        }

        protected override void EnsureInitialized(AbilityModifiableProperty<double?> prop)
        {
            if (!prop.AdjustedValue.HasValue)
                prop.AdjustedValue = prop.GetCurrentValue();
        }

        protected override void EnsureInitialized(AbilityModifiableProperty<long?> prop)
        {
            if (!prop.AdjustedValue.HasValue)
                prop.AdjustedValue = prop.GetCurrentValue();
        }

        protected override void ApplyToFloat(AbilityModifiableProperty<float?> prop, double value)
        {
            prop.AdjustedValue -= (float)value;
        }

        protected override void ApplyToDouble(AbilityModifiableProperty<double?> prop, double value)
        {
            prop.AdjustedValue -= value;
        }

        protected override void ApplyToLong(AbilityModifiableProperty<long?> prop, double value)
        {
            prop.AdjustedValue -= (long)value;
        }
    }
}
