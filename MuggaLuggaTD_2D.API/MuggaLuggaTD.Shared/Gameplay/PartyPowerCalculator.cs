using System.Collections.Generic;
using System.Linq;
using Abilities.Models;
using Enums;
using Items.Models;
using StateManagement.Models;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// Computes the power rating of characters and parties.
    ///
    /// This is the single implementation, shared by the Unity client and the API. The server recomputes
    /// PvP power from the player's persisted roster rather than trusting a number sent by the client;
    /// because both sides run this exact code there is no second copy to drift out of sync.
    ///
    /// Everything here is a pure function of save data plus the ability templates from game content —
    /// no Unity types, no scene access.
    /// </summary>
    public static class PartyPowerCalculator
    {
        // Power contribution weights
        public const float POWER_PER_LEVEL = 100f;
        public const float POWER_PER_HP = 0.5f;
        public const float POWER_PER_DAMAGE = 2f;

        // Equipment power weights
        public const float POWER_PER_ITEM_TIER = 25f;
        public const float POWER_PER_EXPLICIT_ATTRIBUTE = 15f;
        public const float POWER_PER_IMPLICIT_ATTRIBUTE = 10f;
        public const float POWER_PER_INFLUENCE = 20f;
        public const float POWER_PER_RARITY_POINT = 50f;

        /// <summary>
        /// Sums the power of the characters whose ids are in <paramref name="characterIds"/>, resolved
        /// against <paramref name="save"/>. Ids with no matching character are ignored — a party that
        /// references a character the player no longer owns simply contributes nothing for it.
        /// </summary>
        public static float CalculatePartyPower(
            UserSaveData save,
            IEnumerable<string> characterIds,
            IReadOnlyCollection<GameAbility> abilityTemplates)
        {
            if (save?.Characters == null || characterIds == null)
                return 0f;

            var ids = new HashSet<string>(characterIds.Where(id => !string.IsNullOrEmpty(id)));
            if (ids.Count == 0)
                return 0f;

            return save.Characters
                .Where(c => c != null && ids.Contains(c.Id))
                .Sum(c => CalculateCharacterPower(c, save.InventoryItems, abilityTemplates));
        }

        /// <summary>
        /// Power of a single saved character: level, health, resolved ability damage, and equipped gear.
        /// </summary>
        public static float CalculateCharacterPower(
            CharacterSaveData character,
            IReadOnlyCollection<ItemSaveData> inventory,
            IReadOnlyCollection<GameAbility> abilityTemplates)
        {
            if (character == null)
                return 0f;

            float power = character.Level * POWER_PER_LEVEL;

            // Health: the saved AdjustedBaseValue is the level-scaled value; fall back to the base.
            var health = character.MaxHealth?.AdjustedBaseValue ?? character.MaxHealth?.BaseValue ?? 0L;
            power += health * POWER_PER_HP;

            power += CalculateAbilityPower(character, abilityTemplates);
            power += CalculateEquipmentPower(character, inventory);

            return power;
        }

        /// <summary>
        /// Rebuilds each saved ability from its content template, replays the saved upgrades through
        /// the same modifier pipeline the client uses, and sums the resulting affinity damage. Saved
        /// abilities store only their link name and upgrades, so the damage has to be re-derived —
        /// which is exactly why this pipeline is shared rather than reimplemented server-side.
        /// </summary>
        private static float CalculateAbilityPower(
            CharacterSaveData character,
            IReadOnlyCollection<GameAbility> abilityTemplates)
        {
            if (character.Abilities == null || abilityTemplates == null)
                return 0f;

            float power = 0f;

            foreach (var savedAbility in character.Abilities)
            {
                if (savedAbility == null) continue;

                var resolved = AbilityResolver.Resolve(savedAbility, abilityTemplates);
                if (resolved?.AffinityStats == null) continue;

                foreach (var stat in resolved.AffinityStats)
                {
                    var damage = stat?.Damage?.GetCurrentValue();
                    if (damage.HasValue)
                        power += damage.Value * POWER_PER_DAMAGE;
                }
            }

            return power;
        }

        /// <summary>
        /// Equipment power, resolved by matching the character's equipped item ids against the saved
        /// inventory. Slots referencing an item that is no longer in the inventory contribute nothing.
        /// </summary>
        private static float CalculateEquipmentPower(
            CharacterSaveData character,
            IReadOnlyCollection<ItemSaveData> inventory)
        {
            if (character.Equipment == null || inventory == null || inventory.Count == 0)
                return 0f;

            var equippedIds = new[]
            {
                character.Equipment.HeadSlotItemId,
                character.Equipment.ChestSlotItemId,
                character.Equipment.LegsSlotItemId,
                character.Equipment.FeetSlotItemId,
                character.Equipment.HandsSlotItemId,
                character.Equipment.NeckSlotItemId,
                character.Equipment.Ring1SlotItemId,
                character.Equipment.Ring2SlotItemId,
                character.Equipment.WeaponSlotItemId,
                character.Equipment.OffHandSlotItemId
            }.Where(id => !string.IsNullOrEmpty(id)).ToList();

            if (equippedIds.Count == 0)
                return 0f;

            var byId = new Dictionary<string, ItemSaveData>();
            foreach (var item in inventory)
                if (item?.Id != null) byId[item.Id] = item;

            float power = 0f;
            foreach (var id in equippedIds)
                if (byId.TryGetValue(id, out var item))
                    power += CalculateItemPower(item);

            return power;
        }

        /// <summary>
        /// Power of a single item. Takes <see cref="IItemData"/> so it serves both the runtime Item
        /// (which carries a Unity sprite and therefore stays client-side) and the saved item data.
        /// </summary>
        public static float CalculateItemPower(IItemData item)
        {
            if (item == null) return 0f;

            float power = (int)item.PowerTier * POWER_PER_ITEM_TIER;
            power += GetRarityMultiplier(item.Rarity) * POWER_PER_RARITY_POINT;

            if (item.ExplicitAttributes != null)
                power += item.ExplicitAttributes.Count * POWER_PER_EXPLICIT_ATTRIBUTE;

            if (item.ImplicitAttribute != null)
                power += POWER_PER_IMPLICIT_ATTRIBUTE;

            if (item.Influences != null)
                power += item.Influences.Count * POWER_PER_INFLUENCE;

            return power;
        }

        public static float GetRarityMultiplier(ItemRarityTypes rarity)
        {
            switch (rarity)
            {
                case ItemRarityTypes.Common: return 1f;
                case ItemRarityTypes.Uncommon: return 1.5f;
                case ItemRarityTypes.Magic: return 2.5f;
                case ItemRarityTypes.Rare: return 4f;
                case ItemRarityTypes.Legendary: return 6f;
                case ItemRarityTypes.Celestial: return 9f;
                case ItemRarityTypes.GodLike: return 13f;
                default: return 1f;
            }
        }
    }
}
