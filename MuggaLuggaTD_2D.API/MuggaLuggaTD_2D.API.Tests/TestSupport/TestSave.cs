using Abilities.Models;
using Enums;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Tests.TestSupport;

/// <summary>
/// Builds player saves — the roster the server recomputes PvP power from, and the document the
/// upgrade validator edits.
///
/// <para>Written as Newtonsoft JSON where a test needs the stored form, because that is what the
/// client writes and what <c>WorldRaidService</c> reads back.</para>
/// </summary>
public static class TestSave
{
    /// <summary>
    /// A character worth roughly <c>level * 100</c> power — level is the dominant term in
    /// <see cref="MuggaLuggaTD.Shared.Gameplay.PartyPowerCalculator"/>, which is what lets a test
    /// set up a lopsided fight without hand-computing a power value.
    /// </summary>
    public static CharacterSaveData Character(string id, long level = 1)
    {
        return new CharacterSaveData
        {
            Id = id,
            CharacterName = id,
            LinkName = "TestHero",
            Level = level,
            MaxHealth = new ModifiablePropertySaveData<long?> { BaseValue = 100, AdjustedBaseValue = 100 },
            MovementSpeed = new ModifiablePropertySaveData<float?> { BaseValue = 5f, AdjustedBaseValue = 5f }
        };
    }

    public static UserSaveData Roster(params CharacterSaveData[] characters)
    {
        return new UserSaveData
        {
            Username = "tester",
            LastSaveTime = DateTime.UtcNow,
            Characters = characters.ToList()
        };
    }

    /// <summary>The save as the client stores it: Newtonsoft, because the client wrote it that way.</summary>
    public static string ToJson(UserSaveData save) => Newtonsoft.Json.JsonConvert.SerializeObject(save);

    // -----------------------------------------------------------------
    // Ability upgrades
    // -----------------------------------------------------------------

    /// <summary>A content-side upgrade: one of the options the game legitimately offers.</summary>
    public static AbilityUpgrade Upgrade(string name, AbilityUpgradeModifierTypes type, double value,
        string property = "Damage")
    {
        return new AbilityUpgrade
        {
            Name = name,
            Description = name,
            Modifiers = new List<AbilityModifier>
            {
                new() { UpgradeModifierType = type, Value = value, Property = property }
            }
        };
    }

    /// <summary>The same upgrade as a player save records it, once applied.</summary>
    public static AbilityUpgradeSaveData Applied(string name, AbilityUpgradeModifierTypes type, double value,
        string property = "Damage")
    {
        return new AbilityUpgradeSaveData
        {
            Name = name,
            Description = name,
            Modifiers = new List<AbilityModifierSaveData>
            {
                new() { UpgradeModifierType = type, Value = value, Property = property }
            }
        };
    }

    /// <summary>A save holding one character with one ability carrying the given applied upgrades.</summary>
    public static UserSaveData WithAppliedUpgrades(string abilityLinkName, params AbilityUpgradeSaveData[] applied)
    {
        var character = Character("hero-1");
        character.Abilities = new List<AbilitySaveData>
        {
            new()
            {
                AbilityLinkName = abilityLinkName,
                AbilityName = abilityLinkName,
                Level = 1,
                AppliedUpgrades = applied.ToList()
            }
        };

        return Roster(character);
    }

    /// <summary>
    /// The save as a <see cref="System.Text.Json.Nodes.JsonNode"/>, which is the form the validator
    /// performs its surgery on.
    /// </summary>
    public static System.Text.Json.Nodes.JsonNode AsNode(UserSaveData save)
        => System.Text.Json.Nodes.JsonNode.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(save))!;
}
