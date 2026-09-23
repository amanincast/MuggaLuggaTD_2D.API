using System.Collections.Generic;
using System.Linq;
using Abilities.Models;
using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using Xunit;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// How far a signature has woken up. Design doc 05 §2.
///
/// <para>The rule being pinned is that the stage is a <b>pure function of rarity and level</b>:
/// rarity is the ceiling, level is the climb, and neither side stores the answer. That is what lets
/// the server price a character without trusting anything the client said about it.</para>
/// </summary>
public class AwakeningRulesTests
{
    private const string SignatureId = "archer_volley";

    private static SignatureDefinition Signature() => new()
    {
        SignatureId = SignatureId,
        Class = "Archer",
        BaseAbilityLinkName = "Volley_1",
        Awakening = new List<AwakeningStageDefinition>
        {
            Stage(AwakeningStage.II, Damage(0.3)),
            Stage(AwakeningStage.III, Projectiles(2)),
            Stage(AwakeningStage.IV, Damage(0.4)),
            Apex("Arrowfall", Damage(0.6))
        }
    };

    private static AwakeningStageDefinition Stage(AwakeningStage stage, params AbilityModifier[] modifiers)
        => new() { Stage = stage, Modifiers = modifiers.ToList() };

    private static AwakeningStageDefinition Apex(string name, params AbilityModifier[] modifiers)
        => new() { Stage = AwakeningStage.Apex, Name = name, Modifiers = modifiers.ToList() };

    private static AbilityModifier Damage(double value) => new()
    {
        UpgradeModifierType = AbilityUpgradeModifierTypes.BaseMultiplierIncrease,
        Value = value,
        Property = "Damage"
    };

    private static AbilityModifier Projectiles(double value) => new()
    {
        UpgradeModifierType = AbilityUpgradeModifierTypes.BaseFlatIncrease,
        Value = value,
        Property = "ProjectileCount"
    };

    private static GameAbility Volley(long damage = 40) => new()
    {
        AbilityName = "Volley",
        AbilityLinkName = "Volley_1",
        ProjectileCount = new AbilityModifiableProperty<long?> { BaseValue = 5 },
        AffinityStats = new List<AbilityAffinityStat>
        {
            new()
            {
                AffinityType = AffinityTypes.Physical,
                Damage = new AbilityModifiableProperty<long?> { BaseValue = damage }
            }
        }
    };

    // ---------------------------------------------------------------- the ladder

    [Theory]
    [InlineData(CharacterRarity.Common, AwakeningStage.II)]
    [InlineData(CharacterRarity.Rare, AwakeningStage.III)]
    [InlineData(CharacterRarity.Epic, AwakeningStage.IV)]
    [InlineData(CharacterRarity.Legendary, AwakeningStage.Apex)]
    public void RaritySetsTheCeiling(CharacterRarity rarity, AwakeningStage expected)
    {
        Assert.Equal(expected, AwakeningRules.CeilingFor(rarity));
    }

    [Theory]
    [InlineData(1, AwakeningStage.I)]
    [InlineData(9, AwakeningStage.I)]
    [InlineData(10, AwakeningStage.II)]
    [InlineData(19, AwakeningStage.II)]
    [InlineData(20, AwakeningStage.III)]
    [InlineData(29, AwakeningStage.III)]
    [InlineData(30, AwakeningStage.IV)]
    public void LevelSetsTheClimb(long level, AwakeningStage expected)
    {
        // Epic can reach IV, so this walks the whole ladder without the ceiling interfering.
        Assert.Equal(expected, AwakeningRules.StageFor(CharacterRarity.Epic, level));
    }

    [Fact]
    public void ACommonAtMaxLevelStopsAtTwo()
    {
        Assert.Equal(AwakeningStage.II, AwakeningRules.StageFor(CharacterRarity.Common, 30));
    }

    [Fact]
    public void OnlyALegendaryReachesApex()
    {
        Assert.Equal(AwakeningStage.IV, AwakeningRules.StageFor(CharacterRarity.Epic, 30));
        Assert.Equal(AwakeningStage.Apex, AwakeningRules.StageFor(CharacterRarity.Legendary, 30));
    }

    [Fact]
    public void ALegendaryBelowMaxLevelIsNotYetApex()
    {
        // Rarity is the ceiling, not a shortcut: a Legendary still has to climb.
        Assert.Equal(AwakeningStage.III, AwakeningRules.StageFor(CharacterRarity.Legendary, 29));
    }

    [Fact]
    public void NextStageIsWhatTheCharacterCanStillReach()
    {
        Assert.Equal(AwakeningStage.II, AwakeningRules.NextStageFor(CharacterRarity.Common, 1));
        Assert.Equal(AwakeningStage.IV, AwakeningRules.NextStageFor(CharacterRarity.Epic, 20));
    }

    [Fact]
    public void AtItsCeilingThereIsNoNextStage()
    {
        Assert.Null(AwakeningRules.NextStageFor(CharacterRarity.Common, 10));
        Assert.Null(AwakeningRules.NextStageFor(CharacterRarity.Legendary, 30));
    }

    // ---------------------------------------------------------------- what it grants

    [Fact]
    public void StagesAreCumulative()
    {
        var upgrades = AwakeningRules.UpgradesFor(
            Signature(), AffinityTypes.Water, CharacterRarity.Legendary, 30);

        // II, III, IV and Apex - stage I contributes nothing, because stage I is the signature.
        Assert.Equal(4, upgrades.Count);
    }

    [Fact]
    public void AStageAboveTheCeilingGrantsNothing()
    {
        var common = AwakeningRules.UpgradesFor(
            Signature(), AffinityTypes.Water, CharacterRarity.Common, 30);

        Assert.Single(common);
    }

    [Fact]
    public void ApexCarriesItsGivenName()
    {
        var upgrades = AwakeningRules.UpgradesFor(
            Signature(), AffinityTypes.Water, CharacterRarity.Legendary, 30);

        Assert.Contains(upgrades, u => u.Name == "Arrowfall");
    }

    [Fact]
    public void AnUnnamedStageFallsBackToItsNumber()
    {
        var upgrades = AwakeningRules.UpgradesFor(
            Signature(), AffinityTypes.Water, CharacterRarity.Common, 10);

        Assert.Equal("Awakening II", upgrades.Single().Name);
    }

    [Fact]
    public void ASignatureWithNoLadderAwakensIntoNothing()
    {
        var bare = new SignatureDefinition { SignatureId = "x", BaseAbilityLinkName = "Volley_1" };

        Assert.Empty(AwakeningRules.UpgradesFor(bare, AffinityTypes.Water, CharacterRarity.Legendary, 30));
    }

    // ---------------------------------------------------------------- applied to an ability

    [Fact]
    public void ApplyingStageTwoRaisesTheSignaturesDamage()
    {
        var ability = Volley(damage: 40);

        AwakeningRules.Apply(ability, Signature(), AffinityTypes.Water, CharacterRarity.Common, 10);

        Assert.Equal(52, ability.AffinityStats.Single().Damage.GetCurrentValue());
    }

    [Fact]
    public void BelowTheFirstStageNothingChanges()
    {
        var ability = Volley(damage: 40);

        AwakeningRules.Apply(ability, Signature(), AffinityTypes.Water, CharacterRarity.Legendary, 9);

        Assert.Equal(40, ability.AffinityStats.Single().Damage.GetCurrentValue());
        Assert.Empty(ability.DerivedUpgrades);
    }

    [Fact]
    public void StageThreeChangesTheSignaturesShape()
    {
        var ability = Volley();

        AwakeningRules.Apply(ability, Signature(), AffinityTypes.Water, CharacterRarity.Rare, 20);

        Assert.Equal(7, ability.ProjectileCount.GetCurrentValue());
    }

    [Fact]
    public void AwakeningLandsOnDerivedUpgrades_NotOnTheSavedOnes()
    {
        // The whole reason the two lists are separate: awakening is what the character IS, so it must
        // never reach the save, where AbilityUpgradeValidator would strip it as an unknown pick.
        var ability = Volley();

        AwakeningRules.Apply(ability, Signature(), AffinityTypes.Water, CharacterRarity.Epic, 30);

        Assert.NotEmpty(ability.DerivedUpgrades);
        Assert.Empty(ability.AppliedUpgrades);
    }

    [Fact]
    public void APickedUpgradeDoesNotWipeAwakening()
    {
        // ApplyAbilityUpgrade replays every upgrade from the base. Before DerivedUpgrades existed,
        // taking a level-up pick would have silently undone the character's awakening.
        var ability = Volley(damage: 40);
        AwakeningRules.Apply(ability, Signature(), AffinityTypes.Water, CharacterRarity.Common, 10);

        ability.ApplyAbilityUpgrade(new AbilityUpgrade
        {
            Name = "Sharper Heads",
            Modifiers = new List<AbilityModifier> { Damage(0.5) }
        });

        // Still carrying the +30% from stage II underneath the +50% pick.
        Assert.True(ability.AffinityStats.Single().Damage.GetCurrentValue() > 60,
            "stage II's potency should survive a later pick");
    }

    [Fact]
    public void ReapplyingAwakeningDoesNotCompound()
    {
        // Rebuilding a kit twice must not double the bonus - the pipeline re-derives from the base.
        var ability = Volley(damage: 40);

        AwakeningRules.Apply(ability, Signature(), AffinityTypes.Water, CharacterRarity.Common, 10);
        var once = ability.AffinityStats.Single().Damage.GetCurrentValue();

        AwakeningRules.Apply(ability, Signature(), AffinityTypes.Water, CharacterRarity.Common, 10);

        Assert.Equal(once, ability.AffinityStats.Single().Damage.GetCurrentValue());
    }

    [Fact]
    public void AnAffinityOverrideReplacesThatStagesModifiers()
    {
        var signature = Signature();
        signature.Awakening.Single(a => a.Stage == AwakeningStage.II).AffinityModifiers = new()
        {
            new AwakeningAffinityModifiers
            {
                AffinityType = AffinityTypes.Fire,
                Modifiers = new List<AbilityModifier> { Damage(1.0) }
            }
        };

        var fire = Volley(damage: 40);
        AwakeningRules.Apply(fire, signature, AffinityTypes.Fire, CharacterRarity.Common, 10);

        var water = Volley(damage: 40);
        AwakeningRules.Apply(water, signature, AffinityTypes.Water, CharacterRarity.Common, 10);

        Assert.Equal(80, fire.AffinityStats.Single().Damage.GetCurrentValue());
        Assert.Equal(52, water.AffinityStats.Single().Damage.GetCurrentValue());
    }
}
