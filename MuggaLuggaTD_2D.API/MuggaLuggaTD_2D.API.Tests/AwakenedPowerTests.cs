using System.Collections.Generic;
using System.Linq;
using Abilities.Models;
using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using StateManagement.Models;
using Xunit;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The server prices a roster it did not watch play. Design doc 05 §2 and §5.
///
/// <para>Awakening raises a signature's damage and is never stored, so the server has to re-derive it
/// exactly as the client does. If it does not, a level-30 Legendary is priced as a level-1 one and
/// every number built on power — garrison hold, a raid's marching strength, a siege's encounter — is
/// quietly wrong in the defender's favour. These pin that it is derived, and that it is derived only
/// for the signature.</para>
/// </summary>
public class AwakenedPowerTests
{
    private const string Volley = "Volley_1";
    private const string Basic = "Bow_Attack_1";

    private static IReadOnlyCollection<GameAbility> Templates() => new List<GameAbility>
    {
        Ability(Volley, 40),
        Ability(Basic, 20)
    };

    private static GameAbility Ability(string link, long damage) => new()
    {
        AbilityName = link,
        AbilityLinkName = link,
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

    private static IReadOnlyCollection<SignatureDefinition> Signatures() => new List<SignatureDefinition>
    {
        new()
        {
            SignatureId = "archer_volley",
            Class = "Archer",
            BaseAbilityLinkName = Volley,
            Awakening = new List<AwakeningStageDefinition>
            {
                new()
                {
                    Stage = AwakeningStage.II,
                    Modifiers = new List<AbilityModifier>
                    {
                        new()
                        {
                            UpgradeModifierType = AbilityUpgradeModifierTypes.BaseMultiplierIncrease,
                            Value = 0.3,
                            Property = "Damage"
                        }
                    }
                }
            }
        }
    };

    private static CharacterSaveData Character(long level, CharacterRarity rarity) => new()
    {
        Id = "hero-1",
        Level = level,
        Rarity = rarity,
        SignatureId = "archer_volley",
        SignatureAffinity = AffinityTypes.Water,
        MaxHealth = new ModifiablePropertySaveData<long?> { BaseValue = 100, AdjustedBaseValue = 100 },
        Abilities = new List<AbilitySaveData>
        {
            new() { AbilityLinkName = Volley },
            new() { AbilityLinkName = Basic }
        }
    };

    [Fact]
    public void AnAwakenedCharacterIsWorthMoreThanAnUnawakenedOne()
    {
        var below = PartyPowerCalculator.CalculateCharacterPower(
            Character(9, CharacterRarity.Common), null, Templates(), Signatures());

        var awakened = PartyPowerCalculator.CalculateCharacterPower(
            Character(10, CharacterRarity.Common), null, Templates(), Signatures());

        // Level alone is worth 100; the rest of the gap is the signature waking up.
        Assert.True(awakened - below > PartyPowerCalculator.POWER_PER_LEVEL,
            "stage II should raise power beyond the level it unlocked at");
    }

    [Fact]
    public void RarityChangesPowerOnlyThroughTheCeilingItLifts()
    {
        // Same level, same roll: at level 9 neither has woken up, so rarity is worth nothing yet.
        var common = PartyPowerCalculator.CalculateCharacterPower(
            Character(9, CharacterRarity.Common), null, Templates(), Signatures());

        var legendary = PartyPowerCalculator.CalculateCharacterPower(
            Character(9, CharacterRarity.Legendary), null, Templates(), Signatures());

        Assert.Equal(common, legendary);
    }

    [Fact]
    public void OnlyTheSignatureAwakens_NotTheClassBasic()
    {
        var withSignature = PartyPowerCalculator.CalculateCharacterPower(
            Character(10, CharacterRarity.Common), null, Templates(), Signatures());

        // 100 level + 50 health + damage. Awakened Volley is 52, the basic stays 20.
        const float expected = (10 * PartyPowerCalculator.POWER_PER_LEVEL)
                               + (100 * PartyPowerCalculator.POWER_PER_HP)
                               + ((52 + 20) * PartyPowerCalculator.POWER_PER_DAMAGE);

        Assert.Equal(expected, withSignature);
    }

    [Fact]
    public void WithoutSignatureContentNothingAwakens()
    {
        // The overload that predates awakening still has to produce the old answer rather than throw:
        // enemies, bosses and pre-Tavern allies all go through it.
        var priced = PartyPowerCalculator.CalculateCharacterPower(
            Character(30, CharacterRarity.Legendary), null, Templates());

        const float expected = (30 * PartyPowerCalculator.POWER_PER_LEVEL)
                               + (100 * PartyPowerCalculator.POWER_PER_HP)
                               + ((40 + 20) * PartyPowerCalculator.POWER_PER_DAMAGE);

        Assert.Equal(expected, priced);
    }

    [Fact]
    public void ACharacterWithNoSignatureIsPricedPlainly()
    {
        var character = Character(30, CharacterRarity.Legendary);
        character.SignatureId = null;
        character.SignatureAffinity = null;

        var priced = PartyPowerCalculator.CalculateCharacterPower(
            character, null, Templates(), Signatures());

        const float expected = (30 * PartyPowerCalculator.POWER_PER_LEVEL)
                               + (100 * PartyPowerCalculator.POWER_PER_HP)
                               + ((40 + 20) * PartyPowerCalculator.POWER_PER_DAMAGE);

        Assert.Equal(expected, priced);
    }

    [Fact]
    public void PartyPowerCarriesSignaturesThrough()
    {
        var save = new UserSaveData
        {
            Characters = new List<CharacterSaveData> { Character(10, CharacterRarity.Common) }
        };

        var withContent = PartyPowerCalculator.CalculatePartyPower(
            save, new[] { "hero-1" }, Templates(), Signatures());

        var withoutContent = PartyPowerCalculator.CalculatePartyPower(
            save, new[] { "hero-1" }, Templates());

        Assert.True(withContent > withoutContent,
            "a caller that passes signatures must price the awakening a caller without them cannot see");
    }
}
