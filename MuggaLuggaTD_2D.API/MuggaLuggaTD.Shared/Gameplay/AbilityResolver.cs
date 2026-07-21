using System.Collections.Generic;
using System.Linq;
using Abilities.Models;
using StateManagement.Models;


namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>Wrapper matching the AbilityData content document's shape.</summary>
    public class AbilityContentDocument
    {
        public System.Collections.Generic.List<GameAbility> Abilities { get; set; }
            = new System.Collections.Generic.List<GameAbility>();
    }

    /// <summary>
    /// Rebuilds a live <see cref="GameAbility"/> from its saved form.
    ///
    /// A saved ability stores only its link name, level, and the upgrades applied to it — the actual
    /// damage numbers are derived by cloning the content template and replaying those upgrades through
    /// <see cref="Abilities.Handlers.UpgradeModifierHandler"/>. Both the client (on load) and the
    /// server (when computing PvP power) need that derivation, so it lives here rather than in either.
    /// </summary>
    public static class AbilityResolver
    {
        /// <summary>
        /// Returns the ability with its upgrades applied, or null when no template matches the saved
        /// link name (content the player's save references but the current content set no longer has).
        /// </summary>
        public static GameAbility Resolve(AbilitySaveData saveData, IReadOnlyCollection<GameAbility> templates)
        {
            if (saveData == null || templates == null)
                return null;

            var template = templates.FirstOrDefault(a => a?.AbilityLinkName == saveData.AbilityLinkName);
            if (template == null)
                return null;

            var ability = CloneTemplate(template);
            ability.Level = saveData.Level;

            if (saveData.AppliedUpgrades != null)
            {
                foreach (var upgradeSave in saveData.AppliedUpgrades)
                {
                    var upgrade = ToUpgrade(upgradeSave);
                    if (upgrade != null)
                        ability.ApplyAbilityUpgrade(upgrade);
                }
            }

            return ability;
        }

        /// <summary>
        /// Deep-copies the parts of a template an upgrade can modify. Modifiable properties must be
        /// fresh instances — sharing them would let one character's upgrades mutate the template and
        /// leak into every other character using the same ability.
        /// </summary>
        public static GameAbility CloneTemplate(GameAbility template)
        {
            var ability = new GameAbility
            {
                PrefabAssetLocation = template.PrefabAssetLocation,
                SpriteLibraryAssetLocation = template.SpriteLibraryAssetLocation,
                AbilityName = template.AbilityName,
                AbilityLinkName = template.AbilityLinkName,
                Description = template.Description,
                TravelSpeed = template.TravelSpeed,
                ManaCost = template.ManaCost,
                Duration = template.Duration,
                AnimationPrefix = template.AnimationPrefix,
                Level = template.Level,
                MaxActivations = template.MaxActivations,
                Classification = template.Classification,
                MovementType = template.MovementType,
                TelegraphDuration = template.TelegraphDuration,
                ValidTargetTags = template.ValidTargetTags?.ToList()
            };

            ability.Range = Clone(template.Range) ?? ability.Range;
            ability.ActivationCooldown = Clone(template.ActivationCooldown);
            ability.CollisionScale = Clone(template.CollisionScale) ?? ability.CollisionScale;
            ability.PierceCount = Clone(template.PierceCount) ?? ability.PierceCount;
            ability.ProjectileCount = Clone(template.ProjectileCount) ?? ability.ProjectileCount;
            ability.ChainCount = Clone(template.ChainCount) ?? ability.ChainCount;

            if (template.AffinityStats != null)
            {
                ability.AffinityStats = template.AffinityStats.Select(s => new AbilityAffinityStat
                {
                    AffinityType = s.AffinityType,
                    Damage = Clone(s.Damage)
                }).ToList();
            }

            if (template.AvailableUpgrades != null)
            {
                ability.AvailableUpgrades = template.AvailableUpgrades.ToList();
            }

            return ability;
        }

        private static AbilityModifiableProperty<T> Clone<T>(AbilityModifiableProperty<T> source)
        {
            if (source == null)
                return null;

            return new AbilityModifiableProperty<T>
            {
                BaseValue = source.BaseValue,
                // Templates often carry only BaseValue; fall back so an unmodified template resolves
                // to its base rather than to default(T).
                AdjustedBaseValue = Or(source.AdjustedBaseValue, source.BaseValue),
                AdjustedValue = Or(source.AdjustedValue, source.BaseValue)
            };
        }

        private static T Or<T>(T value, T fallback)
        {
            return EqualityComparer<T>.Default.Equals(value, default(T)) ? fallback : value;
        }

        private static AbilityUpgrade ToUpgrade(AbilityUpgradeSaveData saveData)
        {
            if (saveData == null)
                return null;

            return new AbilityUpgrade
            {
                Name = saveData.Name,
                Description = saveData.Description,
                Modifiers = saveData.Modifiers?.Select(m => new AbilityModifier
                {
                    UpgradeModifierType = m.UpgradeModifierType,
                    Value = m.Value,
                    AffinityType = m.AffinityType,
                    Property = m.Property
                }).ToList() ?? new List<AbilityModifier>()
            };
        }
    }
}
