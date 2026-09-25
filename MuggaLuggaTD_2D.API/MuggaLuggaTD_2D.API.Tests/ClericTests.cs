using System.Collections.Generic;
using System.IO;
using System.Linq;
using Abilities.Models;
using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The Cleric, the fourth class. Its signatures are hybrids - each strikes a foe with its affinity
/// and does something for its own side - and a heal is priced as negative damage (Mike, 2026-09-24),
/// so the server values a Cleric's garrison, raid and siege strength as it would a Mage's.
/// </summary>
public class ClericTests
{
    private const string Mend = "Mend_1";

    private static GameAbility MendTemplate() => new()
    {
        AbilityName = "Mend",
        AbilityLinkName = Mend,
        HealTargets = AbilitySupportTargeting.MostWounded,
        Healing = new AbilityModifiableProperty<long?> { BaseValue = 35 },
        HealTargetCount = new AbilityModifiableProperty<long?> { BaseValue = 1 },
        SupportRadius = new AbilityModifiableProperty<float?> { BaseValue = 3f },
        WardReduction = 0.3f,
        WardSeconds = 5f,
        AffinityStats = new List<AbilityAffinityStat>
        {
            new() { AffinityType = AffinityTypes.Light, Damage = new AbilityModifiableProperty<long?> { BaseValue = 25 } }
        }
    };

    private static SignatureDefinition MendSignature() => new()
    {
        SignatureId = "cleric_mend",
        Class = "Cleric",
        BaseAbilityLinkName = Mend,
        Awakening = new List<AwakeningStageDefinition>
        {
            new()
            {
                Stage = AwakeningStage.II,
                Modifiers = new List<AbilityModifier>
                {
                    new() { UpgradeModifierType = AbilityUpgradeModifierTypes.BaseMultiplierIncrease, Value = 0.3, Property = "Damage" },
                    new() { UpgradeModifierType = AbilityUpgradeModifierTypes.BaseMultiplierIncrease, Value = 0.3, Property = "Healing" }
                }
            },
            new()
            {
                Stage = AwakeningStage.III,
                Modifiers = new List<AbilityModifier>
                {
                    new() { UpgradeModifierType = AbilityUpgradeModifierTypes.BaseFlatIncrease, Value = 1, Property = "HealTargetCount" }
                }
            }
        }
    };

    private static CharacterSaveData Cleric(long level, CharacterRarity rarity = CharacterRarity.Common) => new()
    {
        Id = "cleric-1",
        Level = level,
        Rarity = rarity,
        SignatureId = "cleric_mend",
        SignatureAffinity = AffinityTypes.Light,
        MaxHealth = new ModifiablePropertySaveData<long?> { BaseValue = 100, AdjustedBaseValue = 100 },
        Abilities = new List<AbilitySaveData> { new() { AbilityLinkName = Mend } }
    };

    [Fact]
    public void AHealIsPricedAsNegativeDamage()
    {
        var priced = PartyPowerCalculator.CalculateCharacterPower(
            Cleric(1), null, new[] { MendTemplate() });

        // 25 damage and 35 healing are worth exactly what 60 damage would be.
        const float expected = PartyPowerCalculator.POWER_PER_LEVEL
                               + (100 * PartyPowerCalculator.POWER_PER_HP)
                               + ((25 + 35) * PartyPowerCalculator.POWER_PER_DAMAGE);

        Assert.Equal(expected, priced);
    }

    [Fact]
    public void HealingWithNobodyToLandOnIsWorthNothing()
    {
        var template = MendTemplate();
        template.HealTargets = AbilitySupportTargeting.None;

        var priced = PartyPowerCalculator.CalculateCharacterPower(Cleric(1), null, new[] { template });

        const float expected = PartyPowerCalculator.POWER_PER_LEVEL
                               + (100 * PartyPowerCalculator.POWER_PER_HP)
                               + (25 * PartyPowerCalculator.POWER_PER_DAMAGE);

        Assert.Equal(expected, priced);
    }

    [Fact]
    public void ACloneKeepsEverythingTheAbilityDoesForItsSide()
    {
        // The clone is what every character's copy of an ability is. A field it forgets is a field no
        // character ever has - the saved-ability clone bug dropped CollisionScale exactly this way.
        var clone = AbilityResolver.CloneTemplate(MendTemplate());

        Assert.Equal(AbilitySupportTargeting.MostWounded, clone.HealTargets);
        Assert.Equal(35, clone.Healing.GetCurrentValue());
        Assert.Equal(1, clone.HealTargetCount.GetCurrentValue());
        Assert.Equal(3f, clone.SupportRadius.GetCurrentValue());
        Assert.Equal(0.3f, clone.WardReduction);
        Assert.Equal(5f, clone.WardSeconds);
        Assert.True(clone.IsSupport);
    }

    [Fact]
    public void AwakeningDeepensTheHealAndThenWidensIt()
    {
        var signatures = new[] { MendSignature() };
        var templates = new[] { MendTemplate() };

        var stageTwo = AbilityResolver.Resolve(new AbilitySaveData { AbilityLinkName = Mend }, templates,
            AffinityTypes.Light, AwakeningContext.For(signatures, "cleric_mend", AffinityTypes.Light, CharacterRarity.Common, 10));
        Assert.Equal(45, stageTwo.Healing.GetCurrentValue()); // 35 x 1.3, truncated like every whole-number property
        Assert.Equal(1, stageTwo.HealTargetCount.GetCurrentValue());

        var stageThree = AbilityResolver.Resolve(new AbilitySaveData { AbilityLinkName = Mend }, templates,
            AffinityTypes.Light, AwakeningContext.For(signatures, "cleric_mend", AffinityTypes.Light, CharacterRarity.Rare, 20));
        Assert.Equal(2, stageThree.HealTargetCount.GetCurrentValue());
    }

    [Fact]
    public void AnAwakenedClericIsWorthMoreThroughItsHealingToo()
    {
        var below = PartyPowerCalculator.CalculateCharacterPower(Cleric(9), null, new[] { MendTemplate() }, new[] { MendSignature() });
        var awakened = PartyPowerCalculator.CalculateCharacterPower(Cleric(10), null, new[] { MendTemplate() }, new[] { MendSignature() });

        // Level is 100 of it; stage II adds 30% to both halves of the 60 - healing 35 -> 45, and the
        // damage its retuned share.
        Assert.Equal(PartyPowerCalculator.POWER_PER_LEVEL + (7 + 11) * PartyPowerCalculator.POWER_PER_DAMAGE, awakened - below);
    }

    // -----------------------------------------------------------------
    // The content that ships
    // -----------------------------------------------------------------

    private static JObject Content(string name)
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MuggaLuggaTD_2D.API", "GameContent", name + ".json")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "MuggaLuggaTD_2D.API", "GameContent", name + ".json"));
        return JObject.Parse(text, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
    }

    [Fact]
    public void TheShippedClericIsAClassWithTwoHybridSignatures()
    {
        var abilities = Content("AbilityData")["Abilities"]!.ToObject<List<GameAbility>>()!;
        var signatures = Content("SignatureData")["Signatures"]!.ToObject<List<SignatureDefinition>>()!;
        var classes = Content("CharacterData")["Classes"]!;

        Assert.Contains(classes, c => (string?)c["ClassName"] == "Cleric");

        var cleric = SignatureRules.ForClass(signatures, "Cleric");
        Assert.Equal(2, cleric.Count);

        foreach (var signature in cleric)
        {
            var ability = abilities.Single(a => a.AbilityLinkName == signature.BaseAbilityLinkName);

            // A hybrid: it strikes, and it does something for its side.
            Assert.True(ability.AffinityStats.Sum(s => s.Damage.GetCurrentValue() ?? 0) > 0, $"{signature.SignatureId}: damage");
            Assert.True(ability.IsSupport, $"{signature.SignatureId}: support");
            Assert.DoesNotContain(AffinityTypes.Physical, signature.AllowedAffinities);
        }
    }

    [Fact]
    public void TheTavernCanRollAClericOnceItHasAFace()
    {
        var signatures = Content("SignatureData")["Signatures"]!.ToObject<List<SignatureDefinition>>()!;
        var sheets = new List<RecruitSheet> { new() { Sheet = "Ally_Human_Cleric_1", Class = "Cleric" } };

        var roll = RecruitRoller.Roll(sheets, signatures, 1, null, new System.Random(7));

        Assert.NotNull(roll);
        Assert.Equal("Cleric", roll!.Class);
        Assert.StartsWith("cleric_", roll.SignatureId);
    }
}
