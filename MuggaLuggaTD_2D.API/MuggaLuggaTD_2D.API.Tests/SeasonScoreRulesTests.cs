using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// What a realm is scored on (<c>docs/design/seasons-and-scoring.md</c> §3).
///
/// <para>These are the weights that decide who wins, so what is pinned here is the <i>shape</i> of
/// them rather than their exact values: deeper land beats shallow, tended land beats neglected, the
/// heartland beats ordinary ground, and a deed pays enough to matter to someone with nothing. Every
/// number is a first pass - nobody has played this game against anybody yet - so a test that pinned
/// "a tier-3 region earns 20.0" would fail the first time the balance was touched and would be
/// telling the truth about nothing.</para>
/// </summary>
public class SeasonScoreRulesTests
{
    // -----------------------------------------------------------------
    // What ground is worth
    // -----------------------------------------------------------------

    [Fact]
    public void DeeperLandIsWorthMoreThanShallow()
    {
        double shallow = SeasonScoreRules.RateFor(Region(tier: 1));
        double deep = SeasonScoreRules.RateFor(Region(tier: 4));

        Assert.True(deep > shallow, $"tier 4 earned {deep}, tier 1 earned {shallow}");
    }

    [Fact]
    public void TendingARegionPaysAsWellAsProtectingIt()
    {
        // Entrenchment is in the rate on purpose: developing a region should be a way to score, not
        // only a way to keep it. It is what gives the repair loop a second reason to exist.
        double bare = SeasonScoreRules.RateFor(Region(entrenchment: 0));
        double fortified = SeasonScoreRules.RateFor(Region(entrenchment: 5));

        Assert.True(fortified > bare, $"entrenched earned {fortified}, bare earned {bare}");
    }

    [Fact]
    public void EntrenchmentBeyondTheCapAddsNothing()
    {
        Assert.Equal(
            SeasonScoreRules.RateFor(Region(entrenchment: 5)),
            SeasonScoreRules.RateFor(Region(entrenchment: 50)));
    }

    [Fact]
    public void TheHeartlandIsWorthAPremiumOverOrdinaryGround()
    {
        // The map already reserves the centre: players are seated at distance 2-4, so it is ground
        // everybody borders and nobody starts in. Paying a premium for it is what makes it the thing
        // worth fighting over.
        var centre = Region(q: 0, r: 0);
        var ordinary = Region(q: 4, r: 0);

        Assert.True(SeasonScoreRules.IsHeartland(centre));
        Assert.False(SeasonScoreRules.IsHeartland(ordinary));
        Assert.True(SeasonScoreRules.RateFor(centre) > SeasonScoreRules.RateFor(ordinary) * 2);
    }

    [Fact]
    public void TheHeartlandIsTheCentreAndItsSixNeighbours()
    {
        var ring = new[]
        {
            new HexCoord(1, 0), new HexCoord(1, -1), new HexCoord(0, -1),
            new HexCoord(-1, 0), new HexCoord(-1, 1), new HexCoord(0, 1)
        };

        Assert.All(ring, hex => Assert.True(SeasonScoreRules.IsHeartland(Region(q: hex.Q, r: hex.R))));
        Assert.False(SeasonScoreRules.IsHeartland(Region(q: 2, r: 0)));
    }

    [Fact]
    public void GroundYouDoNotHoldEarnsYouNothing()
    {
        var regions = new[]
        {
            TestWorld.OwnedBy(TestIds.Player, "r1"),
            TestWorld.OwnedBy(TestIds.Rival, "r2"),
            TestWorld.Region("r3")
        };

        double mine = SeasonScoreRules.RateForHoldings(TestIds.Player, regions);

        Assert.Equal(SeasonScoreRules.RateFor(regions[0]), mine, 3);
    }

    // -----------------------------------------------------------------
    // Deeds
    // -----------------------------------------------------------------

    [Fact]
    public void EveryDeedPaysSomething()
    {
        // Clearing is the bounce-back lever and raiding is the reason to contest. A deed worth zero
        // would be a line in the design that the code does not implement.
        Assert.True(SeasonScoreRules.PointsFor(SeasonDeed.SiteCleared) > 0);
        Assert.True(SeasonScoreRules.PointsFor(SeasonDeed.RaidLanded) > 0);
        Assert.True(SeasonScoreRules.PointsFor(SeasonDeed.RaidRepelled) > 0);
    }

    [Fact]
    public void DefendingSuccessfullyPaysAsWellAsAttackingSuccessfully()
    {
        // siege.md §6 asks for this directly: a defence that holds has to be worth something, or
        // the only way to score against another player is to be the one who marched.
        Assert.Equal(
            SeasonScoreRules.PointsFor(SeasonDeed.RaidLanded),
            SeasonScoreRules.PointsFor(SeasonDeed.RaidRepelled));
    }

    // -----------------------------------------------------------------
    // Accrual
    // -----------------------------------------------------------------

    [Fact]
    public void PointsAccrueInProportionToTimeHeld()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(100, SeasonScoreRules.Accrued(10, from, from.AddHours(10)), 3);
        Assert.Equal(5, SeasonScoreRules.Accrued(10, from, from.AddMinutes(30)), 3);
    }

    [Fact]
    public void TimeThatRunsBackwardsEarnsNothing()
    {
        // Clamping to the season end can hand this method a "to" before its "from". It must be
        // harmless, because otherwise a settle after the bell would subtract points.
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0, SeasonScoreRules.Accrued(10, from, from.AddHours(-5)));
        Assert.Equal(0, SeasonScoreRules.Accrued(0, from, from.AddHours(5)));
    }

    [Fact]
    public void AccrualStopsAtTheSeasonEndNoMatterWhenItIsAskedFor()
    {
        // This is what lets the result be computed lazily: whoever looks first, and whenever they
        // look, sees the standings anybody else would have seen at the bell.
        var end = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(end.AddMinutes(-1), SeasonScoreRules.AccrueUntil(end.AddMinutes(-1), end));
        Assert.Equal(end, SeasonScoreRules.AccrueUntil(end.AddDays(3), end));
    }

    private static WorldRegionData Region(int tier = 2, int entrenchment = 1, int q = 5, int r = 0)
    {
        var region = TestWorld.Region("r-test", q: q, r: r, tier: tier);
        region.Entrenchment = entrenchment;
        return region;
    }
}
