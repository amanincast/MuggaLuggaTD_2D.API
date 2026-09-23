using Abilities.Models;
using Enums;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// What a character actually casts, given its signature and affinity.
///
/// <para>The rule these tests defend is that <b>affinity is a sidegrade</b>: it changes the damage
/// type - and so the status effect - without changing how hard the ability hits. If retuning moved
/// the number, there would be one affinity to roll for and the whole chase would collapse into it.</para>
/// </summary>
public class SignatureRulesTests
{
    private static GameAbility Ability(string linkName, params (AffinityTypes Affinity, long Damage)[] stats)
    {
        return new GameAbility
        {
            AbilityLinkName = linkName,
            AbilityName = linkName,
            AffinityStats = stats.Select(s => new AbilityAffinityStat
            {
                AffinityType = s.Affinity,
                Damage = new AbilityModifiableProperty<long?> { BaseValue = s.Damage }
            }).ToList()
        };
    }

    private static SignatureDefinition Snipe() => new()
    {
        SignatureId = "archer_snipe",
        DisplayName = "Snipe Shot",
        Class = "Archer",
        BaseAbilityLinkName = "Snipe_Shot_1"
    };

    private static SignatureDefinition Blast() => new()
    {
        SignatureId = "mage_blast",
        DisplayName = "Blast",
        Class = "Mage",
        BaseAbilityLinkName = "Magic_Burst_1",
        AllowedAffinities = new List<AffinityTypes>
        {
            AffinityTypes.Fire, AffinityTypes.Water, AffinityTypes.Earth, AffinityTypes.Air,
            AffinityTypes.Light, AffinityTypes.Dark, AffinityTypes.Arcane
        },
        AffinityAbilities = new List<SignatureAffinityAbility>
        {
            new() { AffinityType = AffinityTypes.Fire, AbilityLinkName = "Inferno_Blast_1" },
            new() { AffinityType = AffinityTypes.Water, AbilityLinkName = "Frost_Nova_1" }
        }
    };

    // -----------------------------------------------------------------
    // Which ability
    // -----------------------------------------------------------------

    [Fact]
    public void ASignatureWithNoOverride_IsItsBaseAbility_WhateverTheAffinity()
    {
        Assert.Equal("Snipe_Shot_1", SignatureRules.AbilityLinkFor(Snipe(), AffinityTypes.Fire));
        Assert.Equal("Snipe_Shot_1", SignatureRules.AbilityLinkFor(Snipe(), AffinityTypes.Water));
    }

    [Fact]
    public void AnAffinityThatIsItsOwnSpell_UsesTheNamedAbility()
    {
        Assert.Equal("Inferno_Blast_1", SignatureRules.AbilityLinkFor(Blast(), AffinityTypes.Fire));
        Assert.Equal("Frost_Nova_1", SignatureRules.AbilityLinkFor(Blast(), AffinityTypes.Water));
    }

    [Fact]
    public void AnAffinityWithNoOverride_FallsBackToTheBase()
    {
        // Arcane is the base explosion, so Blast deliberately names no override for it.
        Assert.Equal("Magic_Burst_1", SignatureRules.AbilityLinkFor(Blast(), AffinityTypes.Arcane));
    }

    // -----------------------------------------------------------------
    // Which affinities
    // -----------------------------------------------------------------

    [Fact]
    public void ASignatureNamingNoAffinities_MayRollAllEight()
    {
        Assert.Equal(8, SignatureRules.AffinitiesFor(Snipe()).Count);
        Assert.True(SignatureRules.IsAffinityAllowed(Snipe(), AffinityTypes.Physical));
    }

    [Fact]
    public void AMageSignature_CannotRollPhysical()
    {
        Assert.False(SignatureRules.IsAffinityAllowed(Blast(), AffinityTypes.Physical));
        Assert.True(SignatureRules.IsAffinityAllowed(Blast(), AffinityTypes.Light));
    }

    // -----------------------------------------------------------------
    // Retuning
    // -----------------------------------------------------------------

    [Fact]
    public void Retuning_ChangesTheDamageTypeAndKeepsTheDamage()
    {
        var ability = Ability("Snipe_Shot_1", (AffinityTypes.Physical, 95));

        SignatureRules.Retune(ability, AffinityTypes.Water);

        var stat = Assert.Single(ability.AffinityStats);
        Assert.Equal(AffinityTypes.Water, stat.AffinityType);
        Assert.Equal(95, stat.GetDamageValue());
    }

    [Fact]
    public void RetuningAnAbilityThatDealtTwoTypes_KeepsTheirTotal()
    {
        // Halving a two-type ability by keeping only the first stat would make the roll a downgrade.
        var ability = Ability("Hybrid", (AffinityTypes.Physical, 40), (AffinityTypes.Fire, 30));

        SignatureRules.Retune(ability, AffinityTypes.Dark);

        var stat = Assert.Single(ability.AffinityStats);
        Assert.Equal(70, stat.GetDamageValue());
    }

    [Fact]
    public void Retuning_WritesEveryLayer_SoTheNewAffinityIsWhatResolves()
    {
        // GetCurrentValue prefers AdjustedValue. Writing only BaseValue would leave the template's
        // old number in front of the new one - the bug that made every ability's range 5. A content
        // template carries only BaseValue, so the adjusted layers start empty.
        var ability = Ability("Snipe_Shot_1", (AffinityTypes.Physical, 95));
        Assert.Null(ability.AffinityStats[0].Damage.AdjustedValue);

        SignatureRules.Retune(ability, AffinityTypes.Fire);

        var damage = ability.AffinityStats[0].Damage;
        Assert.Equal(95, damage.BaseValue);
        Assert.Equal(95, damage.AdjustedBaseValue);
        Assert.Equal(95, damage.AdjustedValue);
        Assert.Equal(95, ability.AffinityStats[0].GetDamageValue());
    }

    [Fact]
    public void AnAbilityWithNoDamage_IsLeftAlone()
    {
        // A shield has nothing to retune, and inventing a zero-damage stat would make it look like
        // a damage ability to the telegraph, the power calculation and the status hook alike.
        var ability = new GameAbility { AbilityLinkName = "Arcane_Shield_1", AffinityStats = new List<AbilityAffinityStat>() };

        SignatureRules.Retune(ability, AffinityTypes.Fire);

        Assert.Empty(ability.AffinityStats);
    }

    // -----------------------------------------------------------------
    // Resolving against content
    // -----------------------------------------------------------------

    [Fact]
    public void ResolvingASignature_ClonesTheTemplateRatherThanRetuningIt()
    {
        // ApplicationData holds one instance per definition. Retuning it in place would recolour the
        // ability for every character in the game that casts it.
        var template = Ability("Magic_Burst_1", (AffinityTypes.Arcane, 50));
        var templates = new[] { template };

        var resolved = SignatureRules.ResolveAbility(Blast(), AffinityTypes.Light, templates);

        Assert.NotNull(resolved);
        Assert.Equal(AffinityTypes.Light, resolved!.AffinityStats[0].AffinityType);
        Assert.Equal(AffinityTypes.Arcane, template.AffinityStats[0].AffinityType);
    }

    [Fact]
    public void ASignatureNamingAnAbilityContentDoesNotHave_ResolvesToNothing()
    {
        var resolved = SignatureRules.ResolveAbility(Snipe(), AffinityTypes.Fire, Array.Empty<GameAbility>());

        Assert.Null(resolved);
    }

    [Fact]
    public void FindingAnUnknownSignature_ReturnsNothingRatherThanThrowing()
    {
        Assert.Null(SignatureRules.Find(new[] { Snipe() }, "nope"));
        Assert.Null(SignatureRules.Find(null, "archer_snipe"));
    }

    [Fact]
    public void SignaturesAreListedPerClass()
    {
        var all = new[] { Snipe(), Blast() };

        Assert.Equal("archer_snipe", Assert.Single(SignatureRules.ForClass(all, "Archer")).SignatureId);
        Assert.Empty(SignatureRules.ForClass(all, "Cleric"));
    }
}
