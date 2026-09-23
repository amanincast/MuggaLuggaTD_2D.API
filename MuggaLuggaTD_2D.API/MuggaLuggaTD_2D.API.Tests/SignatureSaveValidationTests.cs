using System.Text.Json.Nodes;
using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The gate on a character's identity roll.
///
/// <para>The roll decides the kit and PvP power is computed from it, so a roll with nothing behind it
/// is a character the player awarded themselves. Every roster is written back to what the server's
/// hire records say — design doc 05 §5.2.</para>
/// </summary>
public class SignatureSaveValidationTests
{
    private const string Starter = "Ally_Archer_2";

    private static PlayerSaveValidator Validator()
    {
        return new PlayerSaveValidator(new FakeGameContent
        {
            RecruitSheets = new List<RecruitSheet>
            {
                new()
                {
                    Sheet = Starter,
                    Class = "Archer",
                    SignatureId = "archer_snipe",
                    SignatureAffinity = AffinityTypes.Water
                }
            },
            Signatures = new List<SignatureDefinition>
            {
                new() { SignatureId = "archer_snipe", Class = "Archer", BaseAbilityLinkName = "Snipe_Shot_1" },
                new() { SignatureId = "mage_blast", Class = "Mage", BaseAbilityLinkName = "Magic_Burst_1" }
            }
        });
    }

    private static HiredCharacter Record(
        string characterId, string signatureId, AffinityTypes affinity, CharacterRarity rarity,
        string sheet = "Ally_Mage_1")
        => new()
        {
            CharacterId = characterId,
            Name = "Kestrel",
            Sheet = sheet,
            CharacterClass = "Mage",
            SignatureId = signatureId,
            Affinity = affinity,
            Rarity = rarity
        };

    private static JsonNode Save(
        string id, string linkName, string? signatureId, AffinityTypes? affinity,
        CharacterRarity rarity = CharacterRarity.Common)
    {
        var character = new JsonObject
        {
            ["Id"] = id,
            ["LinkName"] = linkName,
            ["Level"] = 1,
            ["Rarity"] = (int)rarity
        };

        if (signatureId != null) character["SignatureId"] = signatureId;
        if (affinity != null) character["SignatureAffinity"] = (int)affinity.Value;

        return new JsonObject { ["Characters"] = new JsonArray(character) };
    }

    private static JsonObject Hero(JsonNode save) => (JsonObject)save["Characters"]!.AsArray()[0]!;

    // -----------------------------------------------------------------
    // Hired characters
    // -----------------------------------------------------------------

    [Fact]
    public async Task ARollThatMatchesItsHireRecord_IsLeftAlone()
    {
        var save = Save("c1", "Ally_Mage_1", "mage_blast", AffinityTypes.Fire, CharacterRarity.Epic);
        var records = new[] { Record("c1", "mage_blast", AffinityTypes.Fire, CharacterRarity.Epic) };

        var result = Validator().ReconcileRoster(save, records);

        Assert.False(result.Changed);
        await Task.CompletedTask;
    }

    [Fact]
    public void ARollThatDisagreesWithItsHireRecord_IsRewrittenToTheRecord()
    {
        // The player hired a Common Fire Blast and typed Legendary Dark into their save.
        var save = Save("c1", "Ally_Mage_1", "mage_blast", AffinityTypes.Dark, CharacterRarity.Legendary);
        var records = new[] { Record("c1", "mage_blast", AffinityTypes.Fire, CharacterRarity.Common) };

        var result = Validator().ReconcileRoster(save, records);

        Assert.Equal(1, result.Corrected);
        Assert.Equal("mage_blast", Hero(save)["SignatureId"]!.GetValue<string>());
        Assert.Equal((int)AffinityTypes.Fire, Hero(save)["SignatureAffinity"]!.GetValue<int>());
        Assert.Equal((int)CharacterRarity.Common, Hero(save)["Rarity"]!.GetValue<int>());
    }

    [Fact]
    public void ARecordAlsoDecidesTheSheet_SoAHireCannotBeRepainted()
    {
        var save = Save("c1", "Ally_Warrior_1", "mage_blast", AffinityTypes.Fire);
        var records = new[] { Record("c1", "mage_blast", AffinityTypes.Fire, CharacterRarity.Common, sheet: "Ally_Mage_3") };

        Validator().ReconcileRoster(save, records);

        Assert.Equal("Ally_Mage_3", Hero(save)["LinkName"]!.GetValue<string>());
    }

    [Fact]
    public void AnotherPlayersRecord_DoesNotEntitleThisSave()
    {
        // Records are fetched per player per realm, so this is the shape of "id not among them".
        var save = Save("c1", "Ally_Mage_1", "mage_blast", AffinityTypes.Fire, CharacterRarity.Legendary);

        var result = Validator().ReconcileRoster(save, new[] { Record("someone-else", "mage_blast", AffinityTypes.Fire, CharacterRarity.Legendary) });

        Assert.Equal(1, result.Stripped);
        Assert.Null(Hero(save)["SignatureId"]);
    }

    // -----------------------------------------------------------------
    // The starting roster
    // -----------------------------------------------------------------

    [Fact]
    public void AStarterCarryingItsTemplatesRoll_NeedsNoRecord()
    {
        var save = Save("c1", Starter, "archer_snipe", AffinityTypes.Water);

        Assert.False(Validator().ReconcileRoster(save, Array.Empty<HiredCharacter>()).Changed);
    }

    [Fact]
    public void AStarterWhoseRollHasBeenEdited_IsNotAStarter()
    {
        var save = Save("c1", Starter, "archer_snipe", AffinityTypes.Fire);

        var result = Validator().ReconcileRoster(save, Array.Empty<HiredCharacter>());

        Assert.Equal(1, result.Stripped);
        Assert.Null(Hero(save)["SignatureId"]);
    }

    [Fact]
    public void AStarterClaimingARarity_IsNotAStarter()
    {
        // Nothing grants rarity but a hire, and the starting roster was never hired.
        var save = Save("c1", Starter, "archer_snipe", AffinityTypes.Water, CharacterRarity.Legendary);

        var result = Validator().ReconcileRoster(save, Array.Empty<HiredCharacter>());

        Assert.Equal(1, result.Stripped);
        Assert.Equal((int)CharacterRarity.Common, Hero(save)["Rarity"]!.GetValue<int>());
    }

    // -----------------------------------------------------------------
    // Everything else
    // -----------------------------------------------------------------

    [Fact]
    public void ARollWithNothingBehindIt_IsStrippedRatherThanTheCharacterDeleted()
    {
        var save = Save("c1", "Ally_Mage_1", "mage_blast", AffinityTypes.Fire, CharacterRarity.Legendary);

        var result = Validator().ReconcileRoster(save, Array.Empty<HiredCharacter>());

        Assert.Equal(1, result.Stripped);
        Assert.Null(Hero(save)["SignatureId"]);
        Assert.Null(Hero(save)["SignatureAffinity"]);
        Assert.Equal((int)CharacterRarity.Common, Hero(save)["Rarity"]!.GetValue<int>());
        // The character itself survives, with whatever else it had.
        Assert.Single(save["Characters"]!.AsArray());
        Assert.Equal(1, Hero(save)["Level"]!.GetValue<int>());
    }

    [Fact]
    public void ACharacterWithNoRollAtAll_IsLeftAlone()
    {
        var save = Save("c1", "Ally_Mage_1", null, null);

        Assert.False(Validator().ReconcileRoster(save, Array.Empty<HiredCharacter>()).Changed);
    }

    [Fact]
    public void ASaveWithNoRoster_IsNotAnError()
    {
        Assert.False(Validator().ReconcileRoster(new JsonObject(), Array.Empty<HiredCharacter>()).Changed);
        Assert.False(Validator().ReconcileRoster(null, null).Changed);
    }
}
