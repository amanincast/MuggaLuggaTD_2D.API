using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The shared siege rules on their own - what the dossier uses to tell a player whether they may
/// declare, and why not. The service tests prove the server applies them; these prove the rules
/// themselves say what the design says.
/// </summary>
public class SiegeRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SeasonEnd = Now.AddDays(10);

    [Fact]
    public void EverySiegeDeclaredBeforeTheCutoffFitsBeforeTheBell()
    {
        // The cutoff is only honest if a siege declared a moment before it still resolves in time.
        Assert.True(SiegeRules.SeasonCutoffHours >= SiegeRules.MusterHours + SiegeRules.AssaultWindowHours);
    }

    [Fact]
    public void AWornDownRegionWithinReachMayBeDeclaredOn()
    {
        Assert.Equal(SiegeRefusal.None, Check(Rival(resolve: 40), power: 10_000, hold: 1_000));
    }

    [Theory]
    [InlineData(SiegeRules.DeclareResolveThreshold, SiegeRefusal.None)]
    [InlineData(SiegeRules.DeclareResolveThreshold + 1, SiegeRefusal.ResolveTooHigh)]
    public void TheResolveThresholdIsInclusive(int resolve, SiegeRefusal expected)
    {
        Assert.Equal(expected, Check(Rival(resolve), power: 10_000, hold: 1_000));
    }

    [Fact]
    public void NeutralGroundIsNotASiegeTarget()
    {
        // Neutral land is taken by clearing its keep, which anyone may simply go and do.
        Assert.Equal(SiegeRefusal.NotAPlayerRegion, Check(TestWorld.Region("r1"), 10_000, 1_000));
    }

    [Fact]
    public void YourOwnRegionIsNotATarget()
    {
        var mine = TestWorld.OwnedBy(TestIds.Player, "r1");
        mine.Resolve = 10;

        Assert.Equal(SiegeRefusal.OwnRegion, Check(mine, 10_000, 1_000));
    }

    [Fact]
    public void ACapitalIsNeverATargetHoweverWornDown()
    {
        var seat = Rival(resolve: 0);
        seat.IsCapital = true;

        Assert.Equal(SiegeRefusal.Capital, Check(seat, 1_000_000, 1));
    }

    [Fact]
    public void TheGateIsTheFullSixtyPercent()
    {
        Assert.Equal(SiegeRefusal.None, Check(Rival(40), power: 600, hold: 1_000));
        Assert.Equal(SiegeRefusal.BelowGate, Check(Rival(40), power: 599, hold: 1_000));
    }

    [Fact]
    public void TheTruceLastsItsFullLengthAndNoLonger()
    {
        var region = Rival(40);
        region.ClaimedAtUtcTicks = Now.Ticks;

        Assert.True(SiegeRules.IsUnderTruce(region, Now.AddHours(SiegeRules.TruceHours) - TimeSpan.FromSeconds(1)));
        Assert.False(SiegeRules.IsUnderTruce(region, Now.AddHours(SiegeRules.TruceHours)));
    }

    [Fact]
    public void ARegionThatNeverChangedHandsIsNotUnderTruce()
    {
        // Unstamped land - including everything captured before this rule existed - stays
        // contestable. The rule only ever protects; it never retroactively locks the map.
        Assert.False(SiegeRules.IsUnderTruce(Rival(40), Now));
        Assert.Null(SiegeRules.TruceEndsAt(Rival(40)));
    }

    [Fact]
    public void TheLastDayIsClosedToDeclarations()
    {
        Assert.Equal(SiegeRefusal.SeasonClosing,
            SiegeRules.CheckDeclare(Rival(40), TestIds.Player, 10_000, 1_000, Now, Now.AddHours(23)));
        Assert.Equal(SiegeRefusal.None,
            SiegeRules.CheckDeclare(Rival(40), TestIds.Player, 10_000, 1_000, Now, Now.AddHours(25)));
    }

    [Fact]
    public void EveryRefusalHasSomethingToSayToThePlayer()
    {
        foreach (SiegeRefusal refusal in Enum.GetValues(typeof(SiegeRefusal)))
            Assert.False(string.IsNullOrWhiteSpace(SiegeRules.Explain(refusal)));
    }

    [Fact]
    public void TakingARegionBySiegePaysMoreThanAnyRaid()
    {
        // A siege costs days of raiding and a locked army. If it paid less than the raids it took,
        // the season would reward never finishing the job.
        Assert.True(SeasonScoreRules.PointsFor(SeasonDeed.SiegeWon) > 4 * SeasonScoreRules.PointsFor(SeasonDeed.RaidLanded));
        Assert.True(SeasonScoreRules.PointsFor(SeasonDeed.SiegeRepelled) > 0);
    }

    private static SiegeRefusal Check(WorldRegionData region, double power, long hold)
        => SiegeRules.CheckDeclare(region, TestIds.Player, power, hold, Now, SeasonEnd);

    private static WorldRegionData Rival(int resolve)
    {
        var region = TestWorld.OwnedBy(TestIds.Rival, "r1");
        region.Resolve = resolve;
        return region;
    }
}
