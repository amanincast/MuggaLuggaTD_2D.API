using Abilities.Models;
using Enums;

namespace Abilities.Strategies.UpgradeModifierStrategies
{
    public class AffinityBaseFlatIncreaseStrategy : AffinityModifierStrategyBase, IAbilityUpgradeModifierStrategy
    {
        public void ApplyModifier(IGameAbility gameAbility, AbilityModifier abilityModifier)
        {
            if (abilityModifier.Value == null)
            {
                return;
            }
            if (gameAbility.AffinityStats == null || gameAbility.AffinityStats.Count == 0)
            {
                return;
            }
            try
            {
                var affinityStat = PrepareAffinityForModification(gameAbility, abilityModifier.AffinityType.GetValueOrDefault());

                var value = System.Convert.ToInt64(abilityModifier.Value);
                affinityStat.Damage.AdjustedBaseValue += value;
                affinityStat.Damage.AdjustedValue = affinityStat.Damage.AdjustedBaseValue;

            }
            catch (System.Exception ex)
            {
                MuggaLuggaTD.Shared.SharedDiagnostics.LogError($"Error applying AffinityBaseFlatIncreaseStrategy: {ex.Message}");
                return;
            }
        }
    }
}