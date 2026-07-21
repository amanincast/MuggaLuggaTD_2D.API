namespace Abilities.Strategies.UpgradeModifierStrategies
{
    using Abilities.Models;
    using Enums;
    using System;

    public class AffinityRemovalStrategy : IAbilityUpgradeModifierStrategy
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

            if (gameAbility.GetAffinityTypes().Contains(affinityType))
            {
                gameAbility.AffinityStats.RemoveAll(x => x.AffinityType == affinityType);
            }
        }
    }
}