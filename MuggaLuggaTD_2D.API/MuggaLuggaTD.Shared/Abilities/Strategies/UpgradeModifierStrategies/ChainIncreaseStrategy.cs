using Abilities.Models;

namespace Abilities.Strategies.UpgradeModifierStrategies
{
    public class ChainIncreaseStrategy : IAbilityUpgradeModifierStrategy
    {
        public void ApplyModifier(IGameAbility gameAbility, AbilityModifier abilityModifier)
        {
            if (abilityModifier.Value == null)
                return;

            var chainCount = gameAbility.ChainCount;
            if (!chainCount.AdjustedValue.HasValue)
                chainCount.AdjustedValue = chainCount.GetCurrentValue();

            chainCount.AdjustedValue += (long)abilityModifier.Value.Value;
        }
    }
}
