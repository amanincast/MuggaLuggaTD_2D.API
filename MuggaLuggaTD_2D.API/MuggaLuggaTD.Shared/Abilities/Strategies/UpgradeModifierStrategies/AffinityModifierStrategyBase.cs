using Abilities.Models;
using Enums;

namespace Abilities.Strategies.UpgradeModifierStrategies
{
    public class AffinityModifierStrategyBase
    {
        protected AbilityAffinityStat PrepareAffinityForModification(IGameAbility gameAbility, AffinityTypes affinityType)
        {
            var affinityStat = gameAbility.AffinityStats.Find(x => x.AffinityType == affinityType);
            if (affinityStat == null)
            {
                affinityStat = new AbilityAffinityStat
                {
                    AffinityType = affinityType,
                    Damage = new AbilityModifiableProperty<long?>
                    {
                        BaseValue = 0,
                        AdjustedBaseValue = 0,
                        AdjustedValue = 0
                    }
                };
                gameAbility.AffinityStats.Add(affinityStat);
            }
            if (affinityStat.Damage == null)
            {
                affinityStat.Damage = new AbilityModifiableProperty<long?>
                {
                    BaseValue = 0,
                    AdjustedBaseValue = 0,
                    AdjustedValue = 0
                };
            }

            if (!affinityStat.Damage.BaseValue.HasValue)
            {
                affinityStat.Damage.BaseValue = 0;
            }

            if (!affinityStat.Damage.AdjustedBaseValue.HasValue)
            {
                affinityStat.Damage.AdjustedBaseValue = affinityStat.Damage.BaseValue;
            }

            if (!affinityStat.Damage.AdjustedValue.HasValue)
            {
                affinityStat.Damage.AdjustedValue = affinityStat.Damage.AdjustedBaseValue;
            }

            return affinityStat;
        }
    }
}