using System.Collections.Generic;
using System.IO;
using System.Linq;
using Abilities.Models;
using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StateManagement.Models;
using Xunit;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The Warrior spec, phase 3 (§5): the server prices a strike by its shape, so a Warrior's sweep and
/// Whirlwind are not priced as one hit while they cut a crowd, and Bulwark is priced as more health.
/// Garrisons, raids and sieges are all built on this number.
/// </summary>
public class WarriorPowerTests
{
    private static GameAbility Ability(string link, long damage, AbilityShapes shape = AbilityShapes.Point, long ticks = 0) => new()
    {
        AbilityName = link,
        AbilityLinkName = link,
        Shape = shape,
        AuraTicks = new AbilityModifiableProperty<long?> { BaseValue = ticks },
        AffinityStats = new List<AbilityAffinityStat>
        {
            new() { AffinityType = AffinityTypes.Physical, Damage = new AbilityModifiableProperty<long?> { BaseValue = damage } }
        }
    };

    private static CharacterSaveData Hero(string signatureId, params string[] abilities) => new()
    {
        Id = "h",
        Level = 1,
        SignatureId = signatureId,
        MaxHealth = new ModifiablePropertySaveData<long?> { BaseValue = 200 },
        Abilities = abilities.Select(a => new AbilitySaveData { AbilityLinkName = a }).ToList(),
    };

    private static readonly List<SignatureDefinition> Signatures = new()
    {
        new() { SignatureId = "warrior_x", Class = "Warrior", BaseAbilityLinkName = "Nothing" },
        new() { SignatureId = "archer_x", Class = "Archer", BaseAbilityLinkName = "Nothing" },
    };

    [Fact]
    public void AnArc_IsPricedForThreeTargets()
    {
        var templates = new List<GameAbility> { Ability("Point", 40), Ability("Sweep", 40, AbilityShapes.Arc) };

        float point = PartyPowerCalculator.CalculateCharacterPower(Hero(null, "Point"), null, templates);
        float sweep = PartyPowerCalculator.CalculateCharacterPower(Hero(null, "Sweep"), null, templates);

        Assert.Equal(40 * PartyPowerCalculator.POWER_PER_DAMAGE * 2, sweep - point, 3);   // 3 hits, not 1
    }

    [Fact]
    public void AnAura_IsPricedForEachCutTimesThreeTargets()
    {
        var templates = new List<GameAbility> { Ability("Spin", 30, AbilityShapes.Aura, ticks: 5) };
        float spin = PartyPowerCalculator.CalculateCharacterPower(Hero(null, "Spin"), null, templates);

        float expected = 1 * PartyPowerCalculator.POWER_PER_LEVEL + 200 * PartyPowerCalculator.POWER_PER_HP
                         + 30 * PartyPowerCalculator.POWER_PER_DAMAGE * 5 * 3;
        Assert.Equal(expected, spin, 3);
    }

    [Fact]
    public void AWarriorsBulwark_IsPricedAsMoreHealth()
    {
        var templates = new List<GameAbility>();
        float warrior = PartyPowerCalculator.CalculateCharacterPower(Hero("warrior_x"), null, templates, Signatures);
        float archer = PartyPowerCalculator.CalculateCharacterPower(Hero("archer_x"), null, templates, Signatures);

        Assert.Equal(200 * PartyPowerCalculator.POWER_PER_HP * PartyPowerCalculator.BULWARK_HEALTH_SHARE, warrior - archer, 3);
    }

    [Fact]
    public void ACloneKeepsTheCuts()
    {
        var clone = AbilityResolver.CloneTemplate(Ability("Spin", 30, AbilityShapes.Aura, ticks: 5));
        Assert.Equal(AbilityShapes.Aura, clone.Shape);
        Assert.Equal(5, clone.AuraTicks.GetCurrentValue());
    }

    // ---- The shipped content ----

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
    public void Whirlwind_ResolvesForAllEightAffinities_AndTheApexAddsTwoCuts()
    {
        var abilities = Content("AbilityData")["Abilities"]!.ToObject<List<GameAbility>>()!;
        var signatures = Content("SignatureData")["Signatures"]!.ToObject<List<SignatureDefinition>>()!;
        var whirl = signatures.Single(s => s.SignatureId == "warrior_whirl");
        Assert.Equal("Warrior", whirl.Class);
        Assert.Equal(8, SignatureRules.AffinitiesFor(whirl).Count);

        var save = new AbilitySaveData { AbilityLinkName = whirl.BaseAbilityLinkName };
        foreach (var affinity in SignatureRules.AffinitiesFor(whirl))
        {
            var spin = AbilityResolver.Resolve(save, abilities, affinity);
            Assert.NotNull(spin);
            Assert.Equal(AbilityShapes.Aura, spin!.Shape);
            Assert.Equal(5, spin.AuraTicks.GetCurrentValue());
            Assert.Equal(affinity, spin.AffinityStats.Single().AffinityType);
            Assert.Equal(30, spin.AffinityStats.Single().Damage.GetCurrentValue());
        }

        var apex = AwakeningContext.For(signatures, "warrior_whirl", AffinityTypes.Fire, CharacterRarity.Legendary, 30);
        var awakened = AbilityResolver.Resolve(save, abilities, AffinityTypes.Fire, apex);
        Assert.Equal(7, awakened!.AuraTicks.GetCurrentValue());
    }

    [Fact]
    public void GroundSlam_IsBuffed_AndTheDeadDataIsGone()
    {
        var abilities = Content("AbilityData")["Abilities"]!.ToObject<List<GameAbility>>()!;
        var slam = abilities.Single(a => a.AbilityLinkName == "Ground_Slam_1");
        Assert.Equal(110, slam.AffinityStats.Single().Damage.GetCurrentValue());
        Assert.Equal(3.2, slam.CollisionScale.GetCurrentValue()!.Value, 3);

        foreach (var dead in new[] { "Shield_Bash_1", "Fortify_1", "Evasion_1", "Arcane_Shield_1" })
            Assert.DoesNotContain(abilities, a => a.AbilityLinkName == dead);
    }

    [Fact]
    public void TheWarriorRollsAmongThreeSignatures()
    {
        var signatures = Content("SignatureData")["Signatures"]!.ToObject<List<SignatureDefinition>>()!;
        var warrior = SignatureRules.ForClass(signatures, "Warrior").Select(s => s.SignatureId).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "warrior_cleave", "warrior_slam", "warrior_whirl" }, warrior);
    }
}
