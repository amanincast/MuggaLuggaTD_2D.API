using Abilities.Models;
using Enums;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// What an upgrade that says "Damage" actually does.
///
/// <para>It used to increase the ability's <b>range</b>. Damage is not a property of a GameAbility -
/// there is one per affinity the ability deals - so the strategy that reflects over property names
/// could not reach it, and a name map sent "Damage" to "Range" instead. Every "Increase damage by
/// 10%" in the content, on every ability, silently bought range and left damage where it started.</para>
///
/// <para>The upgrade pipeline is shared: the server recomputes party power from these same numbers,
/// so what a damage upgrade is worth has to mean the same thing on both sides.</para>
/// </summary>
public class AbilityDamageUpgradeTests
{
    private static GameAbility Ability(params (AffinityTypes Affinity, long Damage)[] affinities)
    {
        var ability = new GameAbility
        {
            AbilityName = "Test Bolt",
            AbilityLinkName = "Test_Bolt_1",
            Range = new AbilityModifiableProperty<float?> { BaseValue = 10f },
            ActivationCooldown = new AbilityModifiableProperty<double?> { BaseValue = 5.0 },
            AffinityStats = new List<AbilityAffinityStat>()
        };

        foreach (var (affinity, damage) in affinities)
        {
            ability.AffinityStats.Add(new AbilityAffinityStat
            {
                AffinityType = affinity,
                Damage = new AbilityModifiableProperty<long?> { BaseValue = damage }
            });
        }

        return ability;
    }

    private static AbilityUpgrade Upgrade(string name, AbilityUpgradeModifierTypes type, double value, string property)
        => new()
        {
            Name = name,
            Description = name,
            Modifiers = new List<AbilityModifier>
            {
                new() { UpgradeModifierType = type, Value = value, Property = property }
            }
        };

    private static long DamageOf(GameAbility ability, int index = 0) => ability.AffinityStats[index].GetDamageValue();

    [Fact]
    public void ADamageMultiplier_IncreasesDamage()
    {
        var ability = Ability((AffinityTypes.Fire, 100));

        ability.ApplyAbilityUpgrade(Upgrade("Increased Damage",
            AbilityUpgradeModifierTypes.BaseMultiplierIncrease, 0.1, "Damage"));

        Assert.Equal(110, DamageOf(ability));
    }

    [Fact]
    public void ADamageUpgrade_LeavesRangeAlone()
    {
        // The regression itself: this was the only thing a damage upgrade used to do.
        var ability = Ability((AffinityTypes.Fire, 100));

        ability.ApplyAbilityUpgrade(Upgrade("Increased Damage",
            AbilityUpgradeModifierTypes.BaseMultiplierIncrease, 0.1, "Damage"));

        Assert.Equal(10f, ability.Range.GetCurrentValue());
    }

    [Fact]
    public void AFlatDamageIncrease_AddsToDamage()
    {
        var ability = Ability((AffinityTypes.Physical, 50));

        ability.ApplyAbilityUpgrade(Upgrade("Deeper Wound",
            AbilityUpgradeModifierTypes.FlatIncrease, 8, "Damage"));

        Assert.Equal(58, DamageOf(ability));
    }

    [Fact]
    public void TwoDamageUpgrades_AreBothMeasuredFromTheAbilitysOwnDamage()
    {
        // Every applied upgrade is re-applied from the base on each new pick, so without resetting
        // affinity damage first the second 10% would have been 10% of 110.
        var ability = Ability((AffinityTypes.Fire, 100));

        ability.ApplyAbilityUpgrade(Upgrade("Increased Damage",
            AbilityUpgradeModifierTypes.BaseMultiplierIncrease, 0.1, "Damage"));
        ability.ApplyAbilityUpgrade(Upgrade("Greater Damage",
            AbilityUpgradeModifierTypes.BaseMultiplierIncrease, 0.1, "Damage"));

        Assert.Equal(120, DamageOf(ability));
    }

    [Fact]
    public void ADamageUpgrade_MovesEveryAffinityTheAbilityDeals()
    {
        // An earlier upgrade can add a second affinity; a later damage pick should be worth the same
        // to the ability either way.
        var ability = Ability((AffinityTypes.Fire, 100), (AffinityTypes.Water, 40));

        ability.ApplyAbilityUpgrade(Upgrade("Increased Damage",
            AbilityUpgradeModifierTypes.BaseMultiplierIncrease, 0.1, "Damage"));

        Assert.Equal(110, DamageOf(ability, 0));
        Assert.Equal(44, DamageOf(ability, 1));
    }

    [Fact]
    public void AnAbilityThatDealsNoDamage_IsNotACrash()
    {
        var ability = Ability();
        ability.AffinityStats = null;

        ability.ApplyAbilityUpgrade(Upgrade("Increased Damage",
            AbilityUpgradeModifierTypes.BaseMultiplierIncrease, 0.1, "Damage"));

        Assert.Empty(ability.AppliedUpgrades.SelectMany(u => u.Modifiers).Where(m => m.Value == null));
    }

    [Fact]
    public void RangeAndCooldownUpgrades_StillWork()
    {
        var ability = Ability((AffinityTypes.Fire, 100));

        ability.ApplyAbilityUpgrade(Upgrade("Increased Range",
            AbilityUpgradeModifierTypes.BaseMultiplierIncrease, 0.05, "Range"));
        ability.ApplyAbilityUpgrade(Upgrade("Reduced Cooldown",
            AbilityUpgradeModifierTypes.BaseMultiplierDecrease, 0.1, "Cooldown"));

        Assert.Equal(10.5f, ability.Range.GetCurrentValue()!.Value, 3);
        Assert.Equal(4.5, ability.ActivationCooldown.GetCurrentValue()!.Value, 3);
        Assert.Equal(100, DamageOf(ability));
    }
}
