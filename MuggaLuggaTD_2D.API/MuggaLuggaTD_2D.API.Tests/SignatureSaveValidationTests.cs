using System.Text.Json.Nodes;
using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The gate on a character's identity roll.
///
/// <para>The roll decides the kit, so a save naming whatever signature it likes would be choosing
/// its own abilities. This does not yet check that the player <i>earned</i> the roll - that needs
/// the Tavern's hire record - but it does check that the roll is one the game contains.</para>
/// </summary>
public class SignatureSaveValidationTests
{
    private static PlayerSaveValidator Validator()
    {
        return new PlayerSaveValidator(new FakeGameContent
        {
            Signatures = new List<SignatureDefinition>
            {
                new()
                {
                    SignatureId = "archer_snipe",
                    Class = "Archer",
                    BaseAbilityLinkName = "Snipe_Shot_1"
                },
                new()
                {
                    SignatureId = "mage_blast",
                    Class = "Mage",
                    BaseAbilityLinkName = "Magic_Burst_1",
                    AllowedAffinities = new List<AffinityTypes>
                    {
                        AffinityTypes.Fire, AffinityTypes.Water, AffinityTypes.Arcane
                    }
                }
            }
        });
    }

    private static JsonNode SaveWith(string? signatureId, AffinityTypes? affinity,
        CharacterRarity rarity = CharacterRarity.Common)
    {
        var character = new JsonObject
        {
            ["Id"] = "hero",
            ["LinkName"] = "Ally_Archer_2",
            ["Level"] = 1,
            ["Rarity"] = (int)rarity
        };

        if (signatureId != null) character["SignatureId"] = signatureId;
        if (affinity != null) character["SignatureAffinity"] = (int)affinity.Value;

        return new JsonObject { ["Characters"] = new JsonArray(character) };
    }

    private static JsonObject Hero(JsonNode save) => (JsonObject)save["Characters"]!.AsArray()[0]!;

    [Fact]
    public void ARollTheGameContains_IsKept()
    {
        var save = SaveWith("archer_snipe", AffinityTypes.Water);

        var result = Validator().ValidateSignatures(save);

        Assert.False(result.Changed);
        Assert.Equal("archer_snipe", Hero(save)["SignatureId"]!.GetValue<string>());
    }

    [Fact]
    public void ASignatureContentDoesNotHave_IsCleared()
    {
        var save = SaveWith("warrior_apocalypse", AffinityTypes.Dark);

        var result = Validator().ValidateSignatures(save);

        Assert.Equal(1, result.Cleared);
        Assert.Null(Hero(save)["SignatureId"]);
        Assert.Null(Hero(save)["SignatureAffinity"]);
    }

    [Fact]
    public void AnAffinityTheSignatureMayNotHave_IsCleared()
    {
        // A mage's Blast has no Physical form, so claiming one is claiming an ability that cannot
        // be built.
        var save = SaveWith("mage_blast", AffinityTypes.Physical);

        var result = Validator().ValidateSignatures(save);

        Assert.Equal(1, result.Cleared);
        Assert.Null(Hero(save)["SignatureId"]);
    }

    [Fact]
    public void AnAffinityTheSignatureAllows_Survives()
    {
        var save = SaveWith("mage_blast", AffinityTypes.Fire);

        Assert.False(Validator().ValidateSignatures(save).Changed);
    }

    [Fact]
    public void RarityAboveCommon_IsPulledBack_BecauseNothingGrantsItYet()
    {
        var save = SaveWith("archer_snipe", AffinityTypes.Water, CharacterRarity.Legendary);

        var result = Validator().ValidateSignatures(save);

        Assert.Equal(1, result.RarityReset);
        Assert.Equal((int)CharacterRarity.Common, Hero(save)["Rarity"]!.GetValue<int>());
    }

    [Fact]
    public void ACharacterWithNoRollAtAll_IsLeftAlone()
    {
        var save = SaveWith(null, null);

        Assert.False(Validator().ValidateSignatures(save).Changed);
    }

    [Fact]
    public void ASaveWithNoRoster_IsNotAnError()
    {
        var result = Validator().ValidateSignatures(new JsonObject());

        Assert.False(result.Changed);
    }
}
