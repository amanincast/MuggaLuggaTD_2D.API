using Abilities.Models;

namespace Abilities.Strategies.UpgradeModifierStrategies
{
    public class BaseFlatDecreaseStrategy : PropertyModifierStrategyBase
    {
        protected override void EnsureInitialized(AbilityModifiableProperty<float?> prop)
        {
            if (!prop.AdjustedBaseValue.HasValue)
                prop.AdjustedBaseValue = prop.BaseValue;
        }

        protected override void EnsureInitialized(AbilityModifiableProperty<double?> prop)
        {
            if (!prop.AdjustedBaseValue.HasValue)
                prop.AdjustedBaseValue = prop.BaseValue;
        }

        protected override void EnsureInitialized(AbilityModifiableProperty<long?> prop)
        {
            if (!prop.AdjustedBaseValue.HasValue)
                prop.AdjustedBaseValue = prop.BaseValue;
        }

        protected override void ApplyToFloat(AbilityModifiableProperty<float?> prop, double value)
        {
            prop.AdjustedBaseValue -= (float)value;
            prop.AdjustedValue = prop.AdjustedBaseValue;
        }

        protected override void ApplyToDouble(AbilityModifiableProperty<double?> prop, double value)
        {
            prop.AdjustedBaseValue -= value;
            prop.AdjustedValue = prop.AdjustedBaseValue;
        }

        protected override void ApplyToLong(AbilityModifiableProperty<long?> prop, double value)
        {
            prop.AdjustedBaseValue -= (long)value;
            prop.AdjustedValue = prop.AdjustedBaseValue;
        }
    }
}
