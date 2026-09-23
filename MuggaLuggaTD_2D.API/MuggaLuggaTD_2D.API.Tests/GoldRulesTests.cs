using MuggaLuggaTD.Shared.Gameplay;
using Enums;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// What gold is worth and where it comes from.
///
/// <para>The two rules worth pinning are the ones a later balance pass could quietly break: a run's
/// gold is derived from the experience that run pays, so the two cannot drift; and land is weighted
/// exactly as the season weights it, but on an independent base rate, so retuning the scoreboard
/// does not reprice the Tavern.</para>
/// </summary>
public class GoldRulesTests
{
    private static WorldRegionData Region(int tier = 1, int entrenchment = 0, int q = 9, int r = 9)
        => new()
        {
            RegionId = "r1",
            Tier = tier,
            Entrenchment = entrenchment,
            Hex = new HexCoord(q, r),
            Ownership = LocationOwnership.Player,
            OwnerUserId = "player-1"
        };

    // -----------------------------------------------------------------
    // A cleared run
    // -----------------------------------------------------------------

    [Fact]
    public void AClearPaysAFractionOfTheExperienceItIsWorth()
    {
        Assert.Equal(50, GoldRules.GoldForClear(1000));
    }

    [Fact]
    public void AClearWorthNothingPaysNothing()
    {
        Assert.Equal(0, GoldRules.GoldForClear(0));
        Assert.Equal(0, GoldRules.GoldForClear(-500));
    }

    [Fact]
    public void GoldTracksTheLengthOfTheRunBecauseExperienceDoes()
    {
        // The point of pricing off experience: a longer, harder clear pays more without gold
        // needing its own notion of what "longer" means.
        long shortRun = GoldRules.GoldForClear(2_000);
        long longRun = GoldRules.GoldForClear(6_000);

        Assert.Equal(3 * shortRun, longRun);
    }

    // -----------------------------------------------------------------
    // Land held
    // -----------------------------------------------------------------

    [Fact]
    public void DeeperLandPaysMore()
    {
        Assert.True(GoldRules.RateFor(Region(tier: 3)) > GoldRules.RateFor(Region(tier: 1)));
    }

    [Fact]
    public void FortifiedLandPaysMore()
    {
        Assert.True(GoldRules.RateFor(Region(entrenchment: 5)) > GoldRules.RateFor(Region(entrenchment: 0)));
    }

    [Fact]
    public void LandIsWeightedExactlyAsTheSeasonWeightsIt()
    {
        // The shape is a fact about the map, not about what is being paid out, so the ratio between
        // any two regions must be identical under both. Only the base rate may differ.
        var cheap = Region(tier: 1, entrenchment: 0);
        var dear = Region(tier: 4, entrenchment: 5);

        double goldRatio = GoldRules.RateFor(dear) / GoldRules.RateFor(cheap);
        double pointRatio = SeasonScoreRules.RateFor(dear) / SeasonScoreRules.RateFor(cheap);

        Assert.Equal(pointRatio, goldRatio, 6);
    }

    [Fact]
    public void GoldHasItsOwnBaseRate_SoScoringAndTheEconomyAreSeparateDials()
    {
        Assert.NotEqual(SeasonScoreRules.BaseRatePerHour, GoldRules.BaseGoldPerHour);
    }

    [Fact]
    public void OnlyLandYouHoldPays()
    {
        var mine = Region();
        var theirs = Region();
        theirs.OwnerUserId = "player-2";

        double rate = GoldRules.RateForHoldings("player-1", new[] { mine, theirs });

        Assert.Equal(GoldRules.RateFor(mine), rate, 6);
    }

    [Fact]
    public void HoldingNothingPaysNothing()
    {
        Assert.Equal(0, GoldRules.RateForHoldings("player-1", Array.Empty<WorldRegionData>()));
        Assert.Equal(0, GoldRules.RateForHoldings("player-1", null!));
        Assert.Equal(0, GoldRules.RateForHoldings(null!, new[] { Region() }));
    }

    // -----------------------------------------------------------------
    // Accrual
    // -----------------------------------------------------------------

    [Fact]
    public void AccrualIsLinearInTime()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(120, GoldRules.Accrued(60, from, from.AddHours(2)), 6);
        Assert.Equal(30, GoldRules.Accrued(60, from, from.AddMinutes(30)), 6);
    }

    [Fact]
    public void TimeRunningBackwardsPaysNothingRatherThanTakingItBack()
    {
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0, GoldRules.Accrued(60, at, at.AddHours(-3)));
        Assert.Equal(0, GoldRules.Accrued(60, at, at));
    }

    [Fact]
    public void HoldingNoLandAccruesNothingHoweverLongYouWait()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0, GoldRules.Accrued(0, from, from.AddDays(30)));
    }
}
