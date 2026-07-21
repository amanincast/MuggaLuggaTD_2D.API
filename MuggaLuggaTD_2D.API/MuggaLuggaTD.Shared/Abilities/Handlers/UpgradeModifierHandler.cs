using Abilities.Models;
using Abilities.Strategies.UpgradeModifierStrategies;
using System.Collections.Generic;
using System.Linq;
using Enums;

namespace Abilities.Handlers
{
    public class UpgradeModifierHandler
    {
        private static Dictionary<Enums.AbilityUpgradeModifierTypes, IAbilityUpgradeModifierStrategy> _UpgradeStrategies;
        private static Dictionary<Enums.AbilityUpgradeModifierTypes, int> _UpgradePriorityDictionary;

        static UpgradeModifierHandler()
        {
            LoadSupportedUpgradeStrategies();
            LoadUpgradePriorities();
        }

        public static void ApplyUpgrades(IGameAbility gameAbility, List<AbilityUpgrade> upgrades)
        {
            if (upgrades == null || upgrades.Count == 0)
            {
                return;
            }

            //Start by resetting the upgradeable properties to their base values
            //Use reflection to find all properties of type IAbilityModifiableProperty<>
            var upgradeableProperties = gameAbility.GetType().GetProperties()
                .Where(prop => prop.PropertyType.IsGenericType &&
                               prop.PropertyType.Name.StartsWith("AbilityModifiableProperty"))
                .ToList();

            //Reset each upgradeable property
            foreach (var prop in upgradeableProperties)
            {
                //Check if the property value is a float, double, or long type
                var propValue = prop.GetValue(gameAbility);
                if (propValue is IAbilityModifiableProperty<float?> floatProp)
                {
                    floatProp.Reset();
                }
                else if (propValue is IAbilityModifiableProperty<double?> doubleProp)
                {
                    doubleProp.Reset();
                }
                else if (propValue is IAbilityModifiableProperty<long?> intProp)
                {
                    intProp.Reset();
                }
            }

            //Apply each upgrade's modifiers
            foreach (var upgrade in upgrades)
            {
                if (!string.IsNullOrEmpty(upgrade.SpriteLibraryAssetLocation))
                {
                    gameAbility.SpriteLibraryAssetLocation = upgrade.SpriteLibraryAssetLocation;
                }

                ApplyModifiers(gameAbility, upgrade.Modifiers);
            }
        }

        public static void ApplyModifiers(IGameAbility gameAbility, List<AbilityModifier> abilityModifiers)
        {
            //Sort the modifiers based on priority
            abilityModifiers.Sort((x, y) => _UpgradePriorityDictionary[x.UpgradeModifierType].CompareTo(_UpgradePriorityDictionary[y.UpgradeModifierType]));
            foreach (var modifier in abilityModifiers)
            {
                ApplyModifier(gameAbility, modifier);
            }
        }

        public static void ApplyModifier(IGameAbility gameAbility, AbilityModifier abilityModifier)
        {
            _UpgradeStrategies[abilityModifier.UpgradeModifierType].ApplyModifier(gameAbility, abilityModifier);
        }

        private static void LoadSupportedUpgradeStrategies()
        {
            _UpgradeStrategies = new Dictionary<Enums.AbilityUpgradeModifierTypes, IAbilityUpgradeModifierStrategy>
            {
                { Enums.AbilityUpgradeModifierTypes.BaseMultiplierIncrease, new BaseMultiplierIncreaseStrategy() },
                { Enums.AbilityUpgradeModifierTypes.BaseFlatIncrease, new BaseFlatIncreaseStrategy() },
                { Enums.AbilityUpgradeModifierTypes.MultiplierIncrease, new MultiplierIncreaseStrategy() },
                { Enums.AbilityUpgradeModifierTypes.FlatIncrease, new FlatIncreaseStrategy() },
                { Enums.AbilityUpgradeModifierTypes.BaseMultiplierDecrease, new BaseMultiplierDecreaseStrategy() },
                { Enums.AbilityUpgradeModifierTypes.BaseFlatDecrease, new BaseFlatDecreaseStrategy() },
                { Enums.AbilityUpgradeModifierTypes.MultiplierDecrease, new MultiplierDecreaseStrategy() },
                { Enums.AbilityUpgradeModifierTypes.FlatDecrease, new FlatDecreaseStrategy() },
                { Enums.AbilityUpgradeModifierTypes.AffinityRemoveAll, new AffinityRemoveAllStrategy() },
                { Enums.AbilityUpgradeModifierTypes.AffinityRemoval, new AffinityRemovalStrategy() },
                { Enums.AbilityUpgradeModifierTypes.AffinityAddition, new AffinityAdditionStrategy() },
                { Enums.AbilityUpgradeModifierTypes.AffinityBaseMultiplierIncrease, new AffinityBaseMultiplierIncreaseStrategy() },
                { Enums.AbilityUpgradeModifierTypes.AffinityBaseFlatIncrease, new AffinityBaseFlatIncreaseStrategy() },
                { Enums.AbilityUpgradeModifierTypes.ChainIncrease, new ChainIncreaseStrategy() }
            };
        }

        private static void LoadUpgradePriorities()
        {
            //Prioritize "Base" modifications before regular modifications
            _UpgradePriorityDictionary = new Dictionary<AbilityUpgradeModifierTypes, int>()
            {
                { AbilityUpgradeModifierTypes.AffinityRemoveAll, 1 },
                { AbilityUpgradeModifierTypes.AffinityRemoval, 2 },
                { AbilityUpgradeModifierTypes.AffinityAddition, 3 },
                { AbilityUpgradeModifierTypes.BaseFlatIncrease, 4 },
                { AbilityUpgradeModifierTypes.BaseFlatDecrease, 5 },
                { AbilityUpgradeModifierTypes.BaseMultiplierIncrease, 6 },
                { AbilityUpgradeModifierTypes.BaseMultiplierDecrease, 7 },
                { AbilityUpgradeModifierTypes.AffinityBaseFlatIncrease, 8 },
                { AbilityUpgradeModifierTypes.AffinityBaseMultiplierIncrease, 9 },
                { AbilityUpgradeModifierTypes.FlatIncrease, 10 },
                { AbilityUpgradeModifierTypes.FlatDecrease, 11 },
                { AbilityUpgradeModifierTypes.MultiplierIncrease, 12 },
                { AbilityUpgradeModifierTypes.MultiplierDecrease, 13 },
                { AbilityUpgradeModifierTypes.ChainIncrease, 14 }
            };
        }
    }
    
}