using System.Collections.Generic;
using System.Linq;
using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using Xunit;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// What a party built around one affinity is worth. Design doc 05 §3.
///
/// <para>Resonance is the demand side of the Tavern: it is the reason to hunt a <i>particular</i>
/// recruit rather than the strongest one on the board. These pin the thresholds and, more
/// importantly, that a party of unrelated rolls gets nothing — if scattered parties resonated, the
/// hunt would have no point.</para>
/// </summary>
public class ResonanceRulesTests
{
    private static AffinityTypes?[] Party(params AffinityTypes[] affinities)
        => affinities.Select(a => (AffinityTypes?)a).ToArray();

    [Fact]
    public void OneOfAnAffinityIsNotResonance()
    {
        var bands = ResonanceRules.Assess(Party(
            AffinityTypes.Water, AffinityTypes.Fire, AffinityTypes.Earth, AffinityTypes.Air));

        Assert.Empty(bands);
    }

    [Theory]
    [InlineData(2, 0.10f)]
    [InlineData(3, 0.20f)]
    [InlineData(4, 0.30f)]
    public void SharingAnAffinityPaysMoreTheMoreShareIt(int sharing, float expected)
    {
        Assert.Equal(expected, ResonanceRules.DamageBonusFor(sharing));
    }

    [Fact]
    public void BelowTwoPaysNothing()
    {
        Assert.Equal(0f, ResonanceRules.DamageBonusFor(1));
        Assert.Equal(0f, ResonanceRules.DamageBonusFor(0));
    }

    [Fact]
    public void AboveTheFullPartyIsCapped()
    {
        // The party is four. A fifth sharer cannot exist, but the rule must not run away if one does.
        Assert.Equal(ResonanceRules.DamageBonusFor(4), ResonanceRules.DamageBonusFor(9));
    }

    [Fact]
    public void ThreeSharingMakesTheStatusLastLonger()
    {
        var two = new ResonanceBand(AffinityTypes.Water, 2);
        var three = new ResonanceBand(AffinityTypes.Water, 3);

        Assert.Equal(1f, two.StatusDurationMultiplier);
        Assert.Equal(ResonanceRules.LongerStatusMultiplier, three.StatusDurationMultiplier);
    }

    [Fact]
    public void OnlyAFullPartyAttunes()
    {
        Assert.False(new ResonanceBand(AffinityTypes.Water, 3).Attunes);
        Assert.True(new ResonanceBand(AffinityTypes.Water, 4).Attunes);
    }

    [Fact]
    public void APartySplitTwoAndTwoResonatesWithBoth()
    {
        var bands = ResonanceRules.Assess(Party(
            AffinityTypes.Water, AffinityTypes.Water, AffinityTypes.Fire, AffinityTypes.Fire));

        Assert.Equal(2, bands.Count);
        Assert.All(bands, b => Assert.Equal(0.10f, b.DamageBonus));
        Assert.Contains(bands, b => b.Affinity == AffinityTypes.Water);
        Assert.Contains(bands, b => b.Affinity == AffinityTypes.Fire);
    }

    [Fact]
    public void TheStrongestBandComesFirst()
    {
        var bands = ResonanceRules.Assess(Party(
            AffinityTypes.Water, AffinityTypes.Water, AffinityTypes.Water, AffinityTypes.Fire,
            AffinityTypes.Fire));

        Assert.Equal(AffinityTypes.Water, bands[0].Affinity);
        Assert.Equal(3, bands[0].Sharing);
    }

    [Fact]
    public void ARollWithNoAffinityCountsForNothing()
    {
        // An enemy, or an ally from before signatures existed.
        var bands = ResonanceRules.Assess(new AffinityTypes?[]
        {
            AffinityTypes.Water, AffinityTypes.Water, null, null
        });

        Assert.Single(bands);
        Assert.Equal(2, bands[0].Sharing);
    }

    [Fact]
    public void AnEmptyOrMissingPartyResonatesWithNothing()
    {
        Assert.Empty(ResonanceRules.Assess(null));
        Assert.Empty(ResonanceRules.Assess(new AffinityTypes?[0]));
    }

    [Fact]
    public void ForFindsTheBandOrReturnsNothing()
    {
        var party = Party(AffinityTypes.Dark, AffinityTypes.Dark, AffinityTypes.Light);

        Assert.Equal(2, ResonanceRules.For(party, AffinityTypes.Dark)?.Sharing);
        Assert.Null(ResonanceRules.For(party, AffinityTypes.Light));
    }
}
