using Abilities.Models;

namespace Abilities.Strategies.UpgradeModifierStrategies
{
    public interface IAbilityUpgradeModifierStrategy
    {
        public void ApplyModifier(IGameAbility gameAbility, AbilityModifier abilityModifier);
    }
}
