using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// How much room a player has for characters.
///
/// <para>The rule these tests are really protecting is that <b>the three sources are bounded and sum
/// to the absolute cap</b> — there is no arithmetic, however a player plays, that gets them past it.
/// The rest is arithmetic worth pinning because it is what the Guild Hall quotes and what the server
/// charges.</para>
/// </summary>
public class RosterCapRulesTests
{
    // -----------------------------------------------------------------
    // The base
    // -----------------------------------------------------------------

    [Fact]
    public void APlayerWhoHoldsNothingStillHasABench()
    {
        // The starting roster is the ally templates, so the base has to leave room to field a party
        // and rotate it. It does not have to leave room to collect.
        Assert.Equal(RosterCapRules.BaseSlots, RosterCapRules.CapFor(0, 0));
        Assert.Equal(RosterCapRules.BaseSlots, RosterCapRules.CapFor(-3, -3));
    }

    // -----------------------------------------------------------------
    // Land
    // -----------------------------------------------------------------

    [Fact]
    public void EachRegionHeldIsWorthASlot()
    {
        Assert.Equal(0, RosterCapRules.FromTerritory(0));
        Assert.Equal(1, RosterCapRules.FromTerritory(1));
        Assert.Equal(5, RosterCapRules.FromTerritory(5));
        Assert.Equal(RosterCapRules.BaseSlots + 5, RosterCapRules.CapFor(5, 0));
    }

    [Fact]
    public void TerritoryStopsPayingOutSoALeaderDoesNotRunAway()
    {
        Assert.Equal(RosterCapRules.MaximumFromTerritory,
            RosterCapRules.FromTerritory(RosterCapRules.MaximumFromTerritory));

        Assert.Equal(RosterCapRules.MaximumFromTerritory, RosterCapRules.FromTerritory(40));
        Assert.Equal(RosterCapRules.MaximumFromTerritory, RosterCapRules.FromTerritory(int.MaxValue));
    }

    [Fact]
    public void TheCapFallsWithTheLand()
    {
        // This is the whole reason the cap gates hiring rather than holding: it can go down, and it
        // must never be able to take a character away.
        int held = RosterCapRules.CapFor(6, 0);
        int lost = RosterCapRules.CapFor(1, 0);

        Assert.True(lost < held);
        Assert.Equal(RosterCapRules.BaseSlots + 1, lost);
    }

    [Fact]
    public void BoughtSlotsSurviveLosingEverything()
    {
        // The floor a bad season cannot push a player below.
        Assert.Equal(RosterCapRules.BaseSlots + 3, RosterCapRules.CapFor(0, 3));
    }

    // -----------------------------------------------------------------
    // Gold
    // -----------------------------------------------------------------

    [Fact]
    public void EachBoughtSlotCostsMoreThanTheLast()
    {
        Assert.Equal(2_500, RosterCapRules.SlotCostFor(0));
        Assert.Equal(5_000, RosterCapRules.SlotCostFor(1));
        Assert.Equal(7_500, RosterCapRules.SlotCostFor(2));
        Assert.Equal(15_000, RosterCapRules.SlotCostFor(RosterCapRules.MaximumPurchasedSlots - 1));
    }

    [Fact]
    public void TheSlotsRunOutBeforeTheMoneyDoes()
    {
        // The hard count is the bound, not the price. A player with land prints gold by the hour, so
        // a price alone would bound nothing.
        Assert.True(RosterCapRules.CanBuyAnother(RosterCapRules.MaximumPurchasedSlots - 1));
        Assert.False(RosterCapRules.CanBuyAnother(RosterCapRules.MaximumPurchasedSlots));

        Assert.Equal(0, RosterCapRules.SlotCostFor(RosterCapRules.MaximumPurchasedSlots));
        Assert.Equal(0, RosterCapRules.SlotCostFor(99));
    }

    // -----------------------------------------------------------------
    // The ceiling
    // -----------------------------------------------------------------

    [Fact]
    public void TheThreeSourcesSumToTheAbsoluteCapAndNothingGetsPastIt()
    {
        Assert.Equal(
            RosterCapRules.BaseSlots + RosterCapRules.MaximumFromTerritory + RosterCapRules.MaximumPurchasedSlots,
            RosterCapRules.AbsoluteCap);

        Assert.Equal(RosterCapRules.AbsoluteCap,
            RosterCapRules.CapFor(RosterCapRules.MaximumFromTerritory, RosterCapRules.MaximumPurchasedSlots));

        Assert.Equal(RosterCapRules.AbsoluteCap, RosterCapRules.CapFor(int.MaxValue, int.MaxValue));
    }

    // -----------------------------------------------------------------
    // Counting what a player holds
    // -----------------------------------------------------------------

    [Fact]
    public void OnlyRegionsThisPlayerHoldsCount()
    {
        var regions = new[]
        {
            TestWorld.OwnedBy(TestIds.Player, "r1"),
            TestWorld.OwnedBy(TestIds.Player, "r2"),
            TestWorld.OwnedBy(TestIds.Rival, "r3"),
            TestWorld.Region("r4")
        };

        Assert.Equal(2, RosterCapRules.RegionsHeldBy(TestIds.Player, regions));
        Assert.Equal(1, RosterCapRules.RegionsHeldBy(TestIds.Rival, regions));
        Assert.Equal(0, RosterCapRules.RegionsHeldBy("nobody", regions));
    }

    [Fact]
    public void AnOwnerIdAloneIsNotOwnership()
    {
        // IsOwnedByPlayer needs the ownership flag as well as the id, and a region that carries only
        // one of them is a region nobody holds.
        var stamped = TestWorld.Region("r1", ownerUserId: TestIds.Player);

        Assert.Equal(LocationOwnership.Neutral, stamped.Ownership);
        Assert.Equal(0, RosterCapRules.RegionsHeldBy(TestIds.Player, new[] { stamped }));
    }

    [Fact]
    public void NothingToCountIsZeroRatherThanAThrow()
    {
        Assert.Equal(0, RosterCapRules.RegionsHeldBy(TestIds.Player, null!));
        Assert.Equal(0, RosterCapRules.RegionsHeldBy(null!, Array.Empty<WorldRegionData>()));
        Assert.Equal(0, RosterCapRules.RegionsHeldBy("", Array.Empty<WorldRegionData>()));
    }
}
