using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// What happens to characters who were defending a region their owner lost.
///
/// <para>The rule worth protecting is that <b>captivity ends</b>, and that asking the rule is not the
/// same as reading the list — the ids outlive the captivity, so anything that trusts
/// <see cref="SiteOverride.CapturedCharacterIds"/> on its own would hold somebody for ever.</para>
/// </summary>
public class CaptivityRulesTests
{
    private static readonly DateTime Noon = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    // -----------------------------------------------------------------
    // The clock
    // -----------------------------------------------------------------

    [Fact]
    public void APrisonerIsHeldAndThenComesHome()
    {
        long taken = Noon.Ticks;

        Assert.True(CaptivityRules.IsHeld(taken, Noon));
        Assert.True(CaptivityRules.IsHeld(taken, Noon.AddHours(CaptivityRules.PrisonerReturnHours - 0.01)));
        Assert.False(CaptivityRules.IsHeld(taken, Noon.AddHours(CaptivityRules.PrisonerReturnHours)));
        Assert.False(CaptivityRules.IsHeld(taken, Noon.AddDays(3)));
    }

    [Fact]
    public void AnUnstampedCaptureReadsAsAlreadyHome()
    {
        // The self-healing direction: anybody imprisoned before this rule existed is free, rather than
        // held since the year zero.
        Assert.False(CaptivityRules.IsHeld(0, Noon));
        Assert.False(CaptivityRules.IsHeld(-1, Noon));
    }

    [Fact]
    public void ASiteWithNamesButNoLiveStampIsHoldingNobody()
    {
        var lapsed = new SiteOverride
        {
            CapturedCharacterIds = new List<string> { "hero-1", "hero-2" },
            CapturedAtUtcTicks = Noon.Ticks
        };

        Assert.True(CaptivityRules.IsHolding(lapsed, Noon));
        Assert.False(CaptivityRules.IsHolding(lapsed, Noon.AddDays(1)));
    }

    [Fact]
    public void AnEmptyCellIsNotHolding()
    {
        Assert.False(CaptivityRules.IsHolding(null, Noon));
        Assert.False(CaptivityRules.IsHolding(new SiteOverride(), Noon));
        Assert.False(CaptivityRules.IsHolding(
            new SiteOverride { CapturedAtUtcTicks = Noon.Ticks }, Noon));
    }

    // -----------------------------------------------------------------
    // The ransom
    // -----------------------------------------------------------------

    [Fact]
    public void ARansomIsPricedPerPrisoner()
    {
        Assert.Equal(0, CaptivityRules.RansomCostGold(0));
        Assert.Equal(0, CaptivityRules.RansomCostGold(-2));
        Assert.Equal(CaptivityRules.RansomPerPrisonerGold, CaptivityRules.RansomCostGold(1));
        Assert.Equal(CaptivityRules.RansomPerPrisonerGold * 4, CaptivityRules.RansomCostGold(4));
    }

    [Fact]
    public void BuyingBackAnArmyCostsLessThanARosterSlot()
    {
        // It buys back something the player already owns, so it must not be priced like something new.
        Assert.True(CaptivityRules.RansomCostGold(4) < RosterCapRules.FirstSlotCostGold);
    }

    // -----------------------------------------------------------------
    // Asking the whole world
    // -----------------------------------------------------------------

    private static WorldRegionData Holding(string regionId, string siteId, DateTime takenAt, params string[] ids)
    {
        var region = TestWorld.OwnedBy(TestIds.Rival, regionId);
        region.SiteOverrides[siteId] = new SiteOverride
        {
            CapturedCharacterIds = ids.ToList(),
            CapturedAtUtcTicks = takenAt.Ticks
        };
        return region;
    }

    [Fact]
    public void MyHeldCharactersAreFoundWhereverInTheWorldTheyAre()
    {
        // The bug this replaces: the client asked the region it happened to have open, so a company
        // captured when a region fell was only discovered by walking back into it.
        var far = Holding("r9", "r9:1", Noon, "mine-1", "theirs-1");
        var near = Holding("r2", "r2:0", Noon, "mine-2");

        var held = CaptivityRules.HeldIdsIn(
            new[] { far, near }, new HashSet<string> { "mine-1", "mine-2" }, Noon);

        Assert.Equal(2, held.Count);
        Assert.Equal(new[] { "mine-1" }, held["r9:1"]);
        Assert.Equal(new[] { "mine-2" }, held["r2:0"]);
    }

    [Fact]
    public void ALapsedCellIsNotReportedAtAll()
    {
        var region = Holding("r1", "r1:0", Noon, "mine-1");

        Assert.Single(CaptivityRules.HeldIdsIn(new[] { region }, new HashSet<string> { "mine-1" }, Noon));
        Assert.Empty(CaptivityRules.HeldIdsIn(
            new[] { region }, new HashSet<string> { "mine-1" }, Noon.AddDays(1)));
    }

    [Fact]
    public void SomebodyElsesPrisonersAreNotMine()
    {
        var region = Holding("r1", "r1:0", Noon, "theirs-1", "theirs-2");

        Assert.Empty(CaptivityRules.HeldIdsIn(new[] { region }, new HashSet<string> { "mine-1" }, Noon));
    }

    [Fact]
    public void NothingToLookThroughIsEmptyRatherThanAThrow()
    {
        Assert.Empty(CaptivityRules.HeldIdsIn(null!, new HashSet<string> { "a" }, Noon));
        Assert.Empty(CaptivityRules.HeldIdsIn(Array.Empty<WorldRegionData>(), null!, Noon));
        Assert.Empty(CaptivityRules.HeldIdsIn(Array.Empty<WorldRegionData>(), new HashSet<string>(), Noon));
    }

    // -----------------------------------------------------------------
    // Garrisons, also a whole-world question
    // -----------------------------------------------------------------

    [Fact]
    public void AGarrisonHoldsItsCharactersWhereverItsOwnerIsStanding()
    {
        var mine = TestWorld.OwnedBy(TestIds.Player, "r1");
        mine.SiteOverrides["r1:0"] = new SiteOverride
        {
            GarrisonCharacterIds = new List<string> { "hero-1", "hero-2" }
        };

        var alsoMine = TestWorld.OwnedBy(TestIds.Player, "r2");
        alsoMine.SiteOverrides["r2:0"] = new SiteOverride
        {
            GarrisonCharacterIds = new List<string> { "hero-3" }
        };

        var stationed = CaptivityRules.GarrisonedIdsOf(new[] { mine, alsoMine }, TestIds.Player);

        Assert.Equal(3, stationed.Count);
        Assert.Contains("hero-3", stationed);
    }

    [Fact]
    public void ARivalsGarrisonIsMadeOfTheirCharactersNotMine()
    {
        var theirs = TestWorld.OwnedBy(TestIds.Rival, "r1");
        theirs.SiteOverrides["r1:0"] = new SiteOverride
        {
            GarrisonCharacterIds = new List<string> { "their-hero" }
        };

        Assert.Empty(CaptivityRules.GarrisonedIdsOf(new[] { theirs }, TestIds.Player));
    }
}
