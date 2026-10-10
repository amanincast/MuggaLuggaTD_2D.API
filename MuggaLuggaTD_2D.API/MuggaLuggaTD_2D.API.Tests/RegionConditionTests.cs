using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Fairs and storms (Active Content C, hourly). What a player should be able to rely on: everyone sees the
/// same conditions for an hour; they fall only on land players hold; there are never more than the cap; and
/// Hunting Season makes a road likelier to be struck without passing the cap on any road.
/// </summary>
public class RegionConditionTests
{
    private static readonly string[] Held = Enumerable.Range(0, 30).Select(i => $"r{i}").ToArray();

    [Fact]
    public void AnHoursConditions_AreTheSameWhoeverAsks_InWhateverOrder()
    {
        for (long hour = 500_000; hour < 500_050; hour++)
        {
            var a = RegionConditionRules.For("Realm-1", hour, Held);
            var b = RegionConditionRules.For("realm-1", hour, Held.Reverse());
            Assert.Equal(a.OrderBy(p => p.Key), b.OrderBy(p => p.Key));
        }
    }

    [Fact]
    public void ThereAreNeverMoreThanTheCap_AndOneRegionCarriesOneCondition()
    {
        for (long hour = 500_000; hour < 502_000; hour++)
        {
            var conditions = RegionConditionRules.For("realm-1", hour, Held);
            Assert.True(conditions.Count(c => c.Value == RegionCondition.HarvestFair) <= RegionConditionRules.MostFairs);
            Assert.True(conditions.Count(c => c.Value == RegionCondition.HuntingSeason) <= 1);
            Assert.All(conditions.Keys, id => Assert.Contains(id, Held));
        }
    }

    [Fact]
    public void AHeldRegion_SeesAFairSomeHours_NotAll()
    {
        int fairHours = Enumerable.Range(0, 24 * 30)
            .Count(h => RegionConditionRules.Of("realm-1", 500_000 + h, "r0", new[] { "r0", "r1", "r2" }) == RegionCondition.HarvestFair);
        // About 2 hours a day over 30 days.
        Assert.InRange(fairHours, 20, 120);
    }

    [Fact]
    public void LandNobodyHolds_CarriesNoCondition()
    {
        var regions = new[]
        {
            TestSupport.TestWorld.Region("a"),
            TestSupport.TestWorld.Region("b", q: 1, r: 0, ownership: LocationOwnership.Player, ownerUserId: "u1"),
        };
        Assert.Equal(new[] { "b" }, RegionConditionRules.HeldIds(regions));
        for (long hour = 500_000; hour < 500_500; hour++)
            Assert.False(RegionConditionRules.For("realm-1", hour, regions).ContainsKey("a"));
    }

    [Fact]
    public void AFairsHours_PayHalfAsMuchAgain()
    {
        var from = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        long first = HarassmentRules.HourOf(from);
        var sheet = new WorkerSheet();
        Func<long, double> fairAtTwo = h => h == first + 2 ? RegionConditionRules.FairGoodsFactor : 1.0;

        double plain = WorkerLevelRules.Gathered(10, from, from.AddHours(4), sheet, "w1", null, null);
        double fair = WorkerLevelRules.Gathered(10, from, from.AddHours(4), sheet, "w1", null, null, fairAtTwo);

        Assert.Equal(40, plain, 6);
        Assert.Equal(45, fair, 6);
    }

    [Fact]
    public void HuntingSeason_MakesARoadLikelier_ButNeverPastTheCap()
    {
        var walk = TimeSpan.FromMinutes(3);
        double plain = AmbushRules.ChanceFor(2, false, walk, false);
        double hunt = AmbushRules.ChanceFor(2, false, walk, false, RegionConditionRules.HuntChanceFactor);
        Assert.True(hunt > plain);
        Assert.Equal(Math.Min(AmbushRules.MaximumChance, plain * RegionConditionRules.HuntChanceFactor), hunt, 6);

        double longRoad = AmbushRules.ChanceFor(5, false, TimeSpan.FromMinutes(30), false, RegionConditionRules.HuntChanceFactor);
        Assert.Equal(AmbushRules.MaximumChance, longRoad, 6);
    }
}
