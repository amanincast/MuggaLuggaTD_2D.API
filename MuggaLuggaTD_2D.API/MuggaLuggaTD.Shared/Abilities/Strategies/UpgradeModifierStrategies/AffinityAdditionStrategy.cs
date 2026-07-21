using Abilities.Models;
using Enums;

namespace Abilities.Strategies.UpgradeModifierStrategies
{
    public class AffinityAdditionStrategy : IAbilityUpgradeModifierStrategy
    {
        public void ApplyModifier(IGameAbility gameAbility, AbilityModifier abilityModifier)
        {
            if (!abilityModifier.AffinityType.HasValue)
            {
                return;
            }

            var affinityType = abilityModifier.AffinityType.Value;

            if (gameAbility.AffinityStats == null)
            {
                gameAbility.AffinityStats = new System.Collections.Generic.List<AbilityAffinityStat>();
            }

            if (!gameAbility.GetAffinityTypes().Contains(affinityType))
            {
                gameAbility.AffinityStats.Add(new AbilityAffinityStat
                {
                    AffinityType = affinityType,
                    Damage = new AbilityModifiableProperty<long?>()
                    {
                        BaseValue = 0
                    }
                });
            }
        }
    }
}