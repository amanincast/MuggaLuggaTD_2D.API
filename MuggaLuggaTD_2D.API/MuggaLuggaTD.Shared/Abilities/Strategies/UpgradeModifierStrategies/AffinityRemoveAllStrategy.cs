using Abilities.Models;
using Enums;

namespace Abilities.Strategies.UpgradeModifierStrategies
{
    public class AffinityRemoveAllStrategy : IAbilityUpgradeModifierStrategy
    {
        public void ApplyModifier(IGameAbility gameAbility, AbilityModifier abilityModifier)
        {
            if (gameAbility.AffinityStats == null || gameAbility.AffinityStats.Count == 0)
            {
                return;
            }

            gameAbility.AffinityStats.Clear();
        }
    }
}