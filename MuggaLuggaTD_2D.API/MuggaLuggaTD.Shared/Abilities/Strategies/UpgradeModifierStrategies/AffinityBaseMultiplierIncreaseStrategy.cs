using System;
using System.Linq;
using Abilities.Models;

namespace Abilities.Strategies.UpgradeModifierStrategies
{
    public class AffinityBaseMultiplierIncreaseStrategy : AffinityModifierStrategyBase, IAbilityUpgradeModifierStrategy
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
                affinityStat.Damage.AdjustedBaseValue += (long?)Math.Round(affinityStat.Damage.BaseValue.GetValueOrDefault() * abilityModifier.Value.GetValueOrDefault());
                affinityStat.Damage.AdjustedValue = affinityStat.Damage.AdjustedBaseValue;
            }
            catch (System.Exception ex)
            {
                MuggaLuggaTD.Shared.SharedDiagnostics.LogError($"Error applying AffinityBaseMultiplierIncreaseStrategy: {ex.Message}");
                return;
            }
        }
    }
}