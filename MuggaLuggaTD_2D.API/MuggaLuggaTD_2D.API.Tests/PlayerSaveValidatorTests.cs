using System.Text.Json.Nodes;
using Enums;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The gate on what a player may persist.
///
/// <para>Ability upgrades drive ability damage, ability damage drives party power, and party power
/// decides PvP. So an upgrade the game never offered is not a cosmetic lie in a save file — it is a
/// PvP exploit that the server would then compute with. This is the only thing standing between a
/// hand-edited save and an unbeatable garrison.</para>
/// </summary>
public class PlayerSaveValidatorTests
{
    private const string Ability = "Fireball";

    /// <summary>The pool the game actually offers for <see cref="Ability"/>.</summary>
    private static PlayerSaveValidator ValidatorOfferingTheUsualPool()
    {
        return new PlayerSaveValidator(FakeGameContent.WithUpgradePool(
            Ability,
            TestSave.Upgrade("Scorching", AbilityUpgradeModifierTypes.FlatIncrease, 5),
            TestSave.Upgrade("Blazing", AbilityUpgradeModifierTypes.MultiplierIncrease, 1.5)));
    }

    // -----------------------------------------------------------------
    // What survives
    // -----------------------------------------------------------------

    [Fact]
    public void AnUpgradeTheGameOffers_IsKept()
    {
        var save = TestSave.AsNode(TestSave.WithAppliedUpgrades(
            Ability, TestSave.Applied("Scorching", AbilityUpgradeModifierTypes.FlatIncrease, 5)));

        var result = ValidatorOfferingTheUsualPool().StripIllegalUpgrades(save);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(0, result.Rejected);
        Assert.False(result.Changed);
        Assert.Single(AppliedUpgrades(save));
    }

    [Fact]
    public void AnUpgradeRenamedButOtherwiseIdentical_IsStillKept()
    {
        // Legality is judged on the effect, not the label: matching on names would break the moment
        // an upgrade was re-worded in content, wiping legitimate saves.
        var save = TestSave.AsNode(TestSave.WithAppliedUpgrades(
            Ability, TestSave.Applied("Renamed In Content", AbilityUpgradeModifierTypes.FlatIncrease, 5)));

        var result = ValidatorOfferingTheUsualPool().StripIllegalUpgrades(save);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(0, result.Rejected);
    }

    // -----------------------------------------------------------------
    // What does not
    // -----------------------------------------------------------------

    [Fact]
    public void AnUpgradeTheGameDoesNotOffer_IsStripped()
    {
        var save = TestSave.AsNode(TestSave.WithAppliedUpgrades(
            Ability, TestSave.Applied("Invented", AbilityUpgradeModifierTypes.FlatIncrease, 9999)));

        var result = ValidatorOfferingTheUsualPool().StripIllegalUpgrades(save);

        Assert.Equal(0, result.Accepted);
        Assert.Equal(1, result.Rejected);
        Assert.True(result.Changed);
        Assert.Empty(AppliedUpgrades(save));
        Assert.Contains("Fireball", result.RejectedDetails.Single());
        Assert.Contains("Invented", result.RejectedDetails.Single());
    }

    [Fact]
    public void ARealUpgradeWithAnInflatedValue_IsStripped()
    {
        // The exploit this exists for: take an upgrade the game does offer and edit its number up.
        // The modifier set no longer matches anything in the pool, so it fails.
        var save = TestSave.AsNode(TestSave.WithAppliedUpgrades(
            Ability, TestSave.Applied("Scorching", AbilityUpgradeModifierTypes.FlatIncrease, 500)));

        var result = ValidatorOfferingTheUsualPool().StripIllegalUpgrades(save);

        Assert.Equal(1, result.Rejected);
        Assert.Empty(AppliedUpgrades(save));
    }

    [Fact]
    public void ARealUpgradeRetargetedAtAnotherProperty_IsStripped()
    {
        // Same value, different property: a damage bonus moved onto range is not an upgrade the
        // game offers, even though every number in it came from one that is.
        var save = TestSave.AsNode(TestSave.WithAppliedUpgrades(
            Ability, TestSave.Applied("Scorching", AbilityUpgradeModifierTypes.FlatIncrease, 5, property: "Range")));

        var result = ValidatorOfferingTheUsualPool().StripIllegalUpgrades(save);

        Assert.Equal(1, result.Rejected);
    }

    [Fact]
    public void AnUpgradeAppliedToAnAbilityWithNoPool_IsStripped()
    {
        // An ability the content does not know about has no legal upgrades, so nothing it carries
        // can be vouched for.
        var save = TestSave.AsNode(TestSave.WithAppliedUpgrades(
            "AbilityThatDoesNotExist", TestSave.Applied("Scorching", AbilityUpgradeModifierTypes.FlatIncrease, 5)));

        var result = ValidatorOfferingTheUsualPool().StripIllegalUpgrades(save);

        Assert.Equal(1, result.Rejected);
        Assert.Equal(0, result.Accepted);
    }

    [Fact]
    public void AnUpgradeThatGrantsNothing_IsStripped()
    {
        var empty = TestSave.Applied("Hollow", AbilityUpgradeModifierTypes.FlatIncrease, 5);
        empty.Modifiers.Clear();
        var save = TestSave.AsNode(TestSave.WithAppliedUpgrades(Ability, empty));

        var result = ValidatorOfferingTheUsualPool().StripIllegalUpgrades(save);

        Assert.Equal(1, result.Rejected);
        Assert.Empty(AppliedUpgrades(save));
    }

    // -----------------------------------------------------------------
    // Surgery, not rewriting
    // -----------------------------------------------------------------

    [Fact]
    public void TheLegalUpgradesSurviveEvenWhenSurroundedByIllegalOnes()
    {
        // The validator walks the list backwards precisely so removals do not shift the entries it
        // has yet to check. Illegal-legal-illegal is the arrangement that catches getting that wrong.
        var save = TestSave.AsNode(TestSave.WithAppliedUpgrades(
            Ability,
            TestSave.Applied("Invented A", AbilityUpgradeModifierTypes.FlatIncrease, 1000),
            TestSave.Applied("Scorching", AbilityUpgradeModifierTypes.FlatIncrease, 5),
            TestSave.Applied("Invented B", AbilityUpgradeModifierTypes.FlatIncrease, 2000),
            TestSave.Applied("Blazing", AbilityUpgradeModifierTypes.MultiplierIncrease, 1.5)));

        var result = ValidatorOfferingTheUsualPool().StripIllegalUpgrades(save);

        Assert.Equal(2, result.Accepted);
        Assert.Equal(2, result.Rejected);

        var survivors = AppliedUpgrades(save).Select(u => u!["Name"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "Scorching", "Blazing" }, survivors);
    }

    [Fact]
    public void EverythingElseInTheSaveIsLeftAlone()
    {
        // The save is edited as JSON rather than round-tripped through a server-side model, because
        // the client writes fields the server has no type for. Losing them on every save would be a
        // far worse bug than the one this class prevents.
        var roster = TestSave.WithAppliedUpgrades(
            Ability, TestSave.Applied("Invented", AbilityUpgradeModifierTypes.FlatIncrease, 9999));
        roster.ActiveCharacterIds = new List<string> { "hero-1" };

        var save = TestSave.AsNode(roster);
        save["SomeFieldOnlyTheClientKnowsAbout"] = "keep me";
        save["Characters"]![0]!["AnotherClientOnlyField"] = 42;

        ValidatorOfferingTheUsualPool().StripIllegalUpgrades(save);

        Assert.Equal("keep me", save["SomeFieldOnlyTheClientKnowsAbout"]!.GetValue<string>());
        Assert.Equal(42, save["Characters"]![0]!["AnotherClientOnlyField"]!.GetValue<int>());
        Assert.Equal("tester", save["Username"]!.GetValue<string>());
        Assert.Equal("hero-1", save["ActiveCharacterIds"]![0]!.GetValue<string>());
    }

    [Fact]
    public void UpgradesAreCheckedOnEveryCharacterAndEveryAbility()
    {
        var first = TestSave.Character("hero-1");
        first.Abilities = new List<StateManagement.Models.AbilitySaveData>
        {
            AbilityWith(Ability, TestSave.Applied("Scorching", AbilityUpgradeModifierTypes.FlatIncrease, 5)),
            AbilityWith(Ability, TestSave.Applied("Invented", AbilityUpgradeModifierTypes.FlatIncrease, 777))
        };

        var second = TestSave.Character("hero-2");
        second.Abilities = new List<StateManagement.Models.AbilitySaveData>
        {
            AbilityWith(Ability, TestSave.Applied("Invented", AbilityUpgradeModifierTypes.FlatIncrease, 888))
        };

        var save = TestSave.AsNode(TestSave.Roster(first, second));

        var result = ValidatorOfferingTheUsualPool().StripIllegalUpgrades(save);

        Assert.Equal(1, result.Accepted);
        Assert.Equal(2, result.Rejected);
    }

    // -----------------------------------------------------------------
    // Saves that are not shaped as expected
    // -----------------------------------------------------------------

    [Fact]
    public void ASaveWithNoCharacters_IsLeftAloneRatherThanRejected()
    {
        var save = TestSave.AsNode(TestSave.Roster());

        var result = ValidatorOfferingTheUsualPool().StripIllegalUpgrades(save);

        Assert.Equal(0, result.Accepted);
        Assert.Equal(0, result.Rejected);
        Assert.False(result.Changed);
    }

    [Theory]
    [InlineData("[]")]                                  // the save body is an array
    [InlineData("5")]                                   // …or a bare number
    [InlineData("\"nonsense\"")]                        // …or a string
    [InlineData("{\"Characters\":\"nonsense\"}")]       // …or the roster is not a list
    [InlineData("{\"Characters\":[\"nonsense\"]}")]     // …or a character is not an object
    [InlineData("{\"Characters\":[{\"Abilities\":[\"nonsense\"]}]}")]
    [InlineData("{\"Characters\":[{\"Abilities\":[{\"AbilityLinkName\":7,\"AppliedUpgrades\":[]}]}]}")]
    public void ASaveThatIsNotShapedLikeASave_IsLeftAloneRatherThanThrowing(string json)
    {
        // The endpoint binds the save body as `object` and the merger hands a non-object straight
        // through, so any of these can reach here from a client. Indexing a JsonNode by name throws
        // unless it is an object — which turned a malformed body into a 500 on the save path.
        var result = ValidatorOfferingTheUsualPool().StripIllegalUpgrades(JsonNode.Parse(json));

        Assert.Equal(0, result.Accepted);
        Assert.Equal(0, result.Rejected);
        Assert.False(result.Changed);
    }

    [Fact]
    public void ASaveThatIsMissingEntirely_IsLeftAlone()
    {
        Assert.Equal(0, ValidatorOfferingTheUsualPool().StripIllegalUpgrades(null).Rejected);
    }

    [Fact]
    public void AnUpgradeEntryThatIsNonsense_IsStrippedRatherThanTrusted()
    {
        // A malformed entry cannot be shown to be legal, so it goes. Failing open here would be an
        // invitation to send deliberate garbage.
        var save = TestSave.AsNode(TestSave.WithAppliedUpgrades(Ability));
        ((JsonArray)save["Characters"]![0]!["Abilities"]![0]!["AppliedUpgrades"]!).Add("not an upgrade at all");

        var result = ValidatorOfferingTheUsualPool().StripIllegalUpgrades(save);

        Assert.Equal(1, result.Rejected);
        Assert.Empty(AppliedUpgrades(save));
    }

    // -----------------------------------------------------------------

    private static JsonArray AppliedUpgrades(JsonNode? save)
        => (JsonArray)save!["Characters"]![0]!["Abilities"]![0]!["AppliedUpgrades"]!;

    private static StateManagement.Models.AbilitySaveData AbilityWith(
        string linkName, params StateManagement.Models.AbilityUpgradeSaveData[] applied)
    {
        return new StateManagement.Models.AbilitySaveData
        {
            AbilityLinkName = linkName,
            AbilityName = linkName,
            Level = 1,
            AppliedUpgrades = applied.ToList()
        };
    }
}
