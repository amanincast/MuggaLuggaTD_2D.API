using System;
using System.Collections.Generic;
using Enums;
using Items.Models;

namespace StateManagement.Models
{
    /// <summary>
    /// Root save data container for a user's game state.
    /// This is the top-level object that gets serialized to JSON.
    ///
    /// Shared with the API: the server reads this roster out of PlayerGameData to compute a party's
    /// power itself, rather than trusting a number the client sends.
    /// </summary>
    [Serializable]
    public class UserSaveData
    {
        public string Username { get; set; }
        public DateTime LastSaveTime { get; set; }
        public int SaveVersion { get; set; } = 1;

        public List<CharacterSaveData> Characters { get; set; } = new List<CharacterSaveData>();
        public List<string> ActiveCharacterIds { get; set; } = new List<string>();
        public List<ItemSaveData> InventoryItems { get; set; } = new List<ItemSaveData>();
    }

    /// <summary>
    /// Serializable character data for persistence.
    /// </summary>
    [Serializable]
    public class CharacterSaveData
    {
        // Identity
        public string Id { get; set; }
        public string TypeName { get; set; }
        public string CharacterName { get; set; }
        public string LinkName { get; set; }
        public string PrefabAssetLocation { get; set; }
        public string SpriteLibraryAssetLocation { get; set; }

        // Progression
        public long Level { get; set; }
        public long CurrentExperience { get; set; }
        public long RequiredExperienceToLevel { get; set; }
        public long DamageTaken { get; set; }

        // Base stats (store base and adjusted base values)
        public ModifiablePropertySaveData<float?> MovementSpeed { get; set; }
        public ModifiablePropertySaveData<long?> MaxHealth { get; set; }

        // Abilities with their applied upgrades
        public List<AbilitySaveData> Abilities { get; set; } = new List<AbilitySaveData>();

        // Unlockable abilities info
        public List<UnlockableAbilitySaveData> UnlockableAbilities { get; set; } = new List<UnlockableAbilitySaveData>();

        // Equipment (store item IDs per slot)
        public EquipmentSaveData Equipment { get; set; }

        // Level scaling configuration
        public CharacterLevelScalingSaveData LevelScaling { get; set; }
    }

    /// <summary>
    /// Serializable modifiable property data.
    /// </summary>
    [Serializable]
    public class ModifiablePropertySaveData<T>
    {
        public T BaseValue { get; set; }
        public T AdjustedBaseValue { get; set; }
    }

    /// <summary>
    /// Serializable ability data for persistence.
    /// </summary>
    [Serializable]
    public class AbilitySaveData
    {
        public string AbilityLinkName { get; set; }
        public string AbilityName { get; set; }
        public int Level { get; set; }

        // Applied upgrades - these get reapplied on load
        public List<AbilityUpgradeSaveData> AppliedUpgrades { get; set; } = new List<AbilityUpgradeSaveData>();
    }

    /// <summary>
    /// Serializable ability upgrade data.
    /// </summary>
    [Serializable]
    public class AbilityUpgradeSaveData
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public List<AbilityModifierSaveData> Modifiers { get; set; } = new List<AbilityModifierSaveData>();
    }

    /// <summary>
    /// Serializable ability modifier data.
    /// </summary>
    [Serializable]
    public class AbilityModifierSaveData
    {
        public AbilityUpgradeModifierTypes UpgradeModifierType { get; set; }
        public double? Value { get; set; }
        public AffinityTypes? AffinityType { get; set; }
        public string Property { get; set; }
    }

    /// <summary>
    /// Serializable unlockable ability info.
    /// </summary>
    [Serializable]
    public class UnlockableAbilitySaveData
    {
        public string AbilityLinkName { get; set; }
        public List<string> ValidTargetTags { get; set; } = new List<string>();
    }

    /// <summary>
    /// Serializable equipment data.
    /// </summary>
    [Serializable]
    public class EquipmentSaveData
    {
        public string HeadSlotItemId { get; set; }
        public string ChestSlotItemId { get; set; }
        public string LegsSlotItemId { get; set; }
        public string FeetSlotItemId { get; set; }
        public string HandsSlotItemId { get; set; }
        public string NeckSlotItemId { get; set; }
        public string Ring1SlotItemId { get; set; }
        public string Ring2SlotItemId { get; set; }
        public string WeaponSlotItemId { get; set; }
        public string OffHandSlotItemId { get; set; }
    }

    /// <summary>
    /// Serializable character level scaling configuration.
    /// </summary>
    [Serializable]
    public class CharacterLevelScalingSaveData
    {
        public float ExperienceMultiplierPerLevel { get; set; }
        public float HealthMultiplierPerLevel { get; set; }
        public float DamageMultiplierPerLevel { get; set; }
    }

    /// <summary>
    /// Serializable item data for persistence.
    /// </summary>
    [Serializable]
    public class ItemSaveData : IItemData
    {
        public string Id { get; set; }
        public string ItemName { get; set; }
        public int ItemCount { get; set; }
        public ItemTypes ItemType { get; set; }
        public string EquippedByCharacterId { get; set; }
        public string IconAssetPath { get; set; }
        public List<ItemAffinityStat> AffinityStats { get; set; } = new List<ItemAffinityStat>();
        public ItemRarityTypes Rarity { get; set; }
        public ItemPowerTier PowerTier { get; set; }
        public List<ItemInfluence> Influences { get; set; } = new List<ItemInfluence>();
        public ItemImplicitAttribute ImplicitAttribute { get; set; }
        public List<ItemExplicitAttribute> ExplicitAttributes { get; set; } = new List<ItemExplicitAttribute>();
    }
}
