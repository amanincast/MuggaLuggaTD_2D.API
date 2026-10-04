using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using Xunit;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Side companies that fight on their own (docs/design/auto-fight.md; Mike, 2026-10-04): only below
/// their level, at a third of the pay, gear a rarity down, roaming rather than repeating, fed by
/// provisions and Bloodied by a loss.
/// </summary>
public class AutoFightRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private static AutoFightCandidate Site(string id, int level, int roadMinutes, LocationType type = LocationType.Dungeon) =>
        new(id, type, level, TimeSpan.FromMinutes(roadMinutes));

    [Fact]
    public void ACompanysLevelIsItsMembersAverageRoundedDown()
    {
        Assert.Equal(7, AutoFightRules.CompanyLevel(new[] { 10, 8, 6, 5 }));   // 7.25
        Assert.Equal(9, AutoFightRules.CompanyLevel(new[] { 10, 9 }));         // 9.5
        Assert.Equal(0, AutoFightRules.CompanyLevel(Array.Empty<int>()));
    }

    [Fact]
    public void ACompanyFightsOnlyWhatIsBelowIt()
    {
        Assert.True(AutoFightRules.CanFight(10, 9));
        Assert.False(AutoFightRules.CanFight(10, 10));
        Assert.False(AutoFightRules.CanFight(10, 12));
        Assert.Equal(0, AutoFightRules.WinChance(10, 10));
    }

    [Fact]
    public void TheWiderTheGapTheBetterTheChanceButNeverCertain()
    {
        Assert.Equal(0.55, AutoFightRules.WinChance(10, 9), 6);
        Assert.Equal(0.71, AutoFightRules.WinChance(10, 7), 6);
        Assert.Equal(0.87, AutoFightRules.WinChance(10, 5), 6);
        Assert.Equal(AutoFightRules.MaximumChance, AutoFightRules.WinChance(30, 1));
        Assert.True(AutoFightRules.WinChance(10, 6) > AutoFightRules.WinChance(10, 7));
    }

    [Fact]
    public void ARegionsMobsAreItsSitesAverageLevel()
    {
        var sites = new[] { new SiteSpec { Level = 3 }, new SiteSpec { Level = 4 }, new SiteSpec { Level = 8 } };
        Assert.Equal(5, AutoFightRules.MobLevel(sites));
        Assert.Equal(1, AutoFightRules.MobLevel(Array.Empty<SiteSpec>()));
    }

    [Fact]
    public void OnlyPortalsAndDungeonsAreFoughtInAutoMode()
    {
        Assert.True(AutoFightRules.IsFightable(LocationType.Portal));
        Assert.True(AutoFightRules.IsFightable(LocationType.Dungeon));
        Assert.False(AutoFightRules.IsFightable(LocationType.ResourceNode));
        Assert.False(AutoFightRules.IsFightable(LocationType.Castle));
        Assert.False(AutoFightRules.IsFightable(LocationType.Ruin));
    }

    [Fact]
    public void AFightIsRolledOnceWhateverTimesItIsSettled()
    {
        for (int fight = 0; fight < 50; fight++)
            Assert.Equal(AutoFightRules.RollWin(0.6, "company-a", fight), AutoFightRules.RollWin(0.6, "company-a", fight));

        int wins = Enumerable.Range(0, 2000).Count(f => AutoFightRules.RollWin(0.6, "company-a", f));
        Assert.InRange(wins, 1080, 1320);
        Assert.DoesNotContain(Enumerable.Range(0, 200), f => AutoFightRules.RollWin(0, "company-a", f));
    }

    [Fact]
    public void ItNeverFightsTheSameSiteTwiceInARow()
    {
        var sites = new[] { Site("a", 3, 1), Site("b", 3, 5) };
        Assert.Equal("b", AutoFightRules.NextSite(sites, 10, new[] { "a" }));
        Assert.Equal("a", AutoFightRules.NextSite(sites, 10, new[] { "b", "a" }));
    }

    [Fact]
    public void ItRoamsALoopRatherThanBouncingBetweenTwo()
    {
        var sites = new[] { Site("a", 3, 1), Site("b", 3, 2), Site("c", 3, 3), Site("d", 3, 4), Site("e", 3, 9) };
        var recent = new List<string>();
        var order = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            var next = AutoFightRules.NextSite(sites, 10, recent)!;
            order.Add(next);
            recent.Insert(0, next);
        }

        // With five sites and its last three avoided, it cycles the four nearest.
        Assert.Equal(new[] { "a", "b", "c", "d", "a", "b", "c", "d" }, order);
    }

    [Fact]
    public void WithOneSiteItWaitsRatherThanRepeat()
    {
        var sites = new[] { Site("a", 3, 1) };
        Assert.Equal("a", AutoFightRules.NextSite(sites, 10, Array.Empty<string>()));
        Assert.Null(AutoFightRules.NextSite(sites, 10, new[] { "a" }));
    }

    [Fact]
    public void ItPassesOverWhatItMayNotFight()
    {
        var sites = new[]
        {
            Site("too-strong", 10, 1),
            Site("mine", 2, 1, LocationType.ResourceNode),
            Site("portal", 4, 6, LocationType.Portal),
        };
        Assert.Equal("portal", AutoFightRules.NextSite(sites, 10, Array.Empty<string>()));
        Assert.Null(AutoFightRules.NextSite(new[] { Site("too-strong", 10, 1) }, 10, Array.Empty<string>()));
    }

    [Fact]
    public void ABossTakesLongestAndAPortalShortest()
    {
        Assert.Equal(TimeSpan.FromMinutes(4), AutoFightRules.FightDuration(LocationType.Portal, false));
        Assert.Equal(TimeSpan.FromMinutes(6), AutoFightRules.FightDuration(LocationType.Dungeon, false));
        Assert.Equal(TimeSpan.FromMinutes(8), AutoFightRules.FightDuration(LocationType.Dungeon, true));
    }

    [Fact]
    public void ItPaysAThirdRoundedByChance()
    {
        Assert.Equal(33, AutoFightRules.Share(100, null));
        var dice = new Random(7);
        long total = Enumerable.Range(0, 3000).Sum(_ => AutoFightRules.Share(1, dice));
        Assert.InRange(total, 900, 1080);
    }

    [Fact]
    public void ItsGearComesOneRarityDown()
    {
        Assert.Equal(ItemRarityTypes.Magic, AutoFightRules.StepDown(ItemRarityTypes.Rare));
        Assert.Equal(ItemRarityTypes.Common, AutoFightRules.StepDown(ItemRarityTypes.Uncommon));
        Assert.Equal(ItemRarityTypes.Common, AutoFightRules.StepDown(ItemRarityTypes.Common));
    }

    [Fact]
    public void AFightEatsGrainAndADungeonHidesToo()
    {
        Assert.Equal(new[] { (ResourceNodeRules.Grain, 2) }, ProvisionRules.CostOfFight(LocationType.Portal));
        Assert.Equal(new[] { (ResourceNodeRules.Grain, 2), (ResourceNodeRules.Hides, 1) },
            ProvisionRules.CostOfFight(LocationType.Dungeon));
    }

    [Fact]
    public void APatrolEatsGrainByTheHourInWholeUnits()
    {
        Assert.Equal(0, ProvisionRules.PatrolGrainEaten(TimeSpan.FromMinutes(19)));
        Assert.Equal(1, ProvisionRules.PatrolGrainEaten(TimeSpan.FromMinutes(20)));
        Assert.Equal(3, ProvisionRules.PatrolGrainEaten(TimeSpan.FromHours(1)));
        Assert.Equal(0, ProvisionRules.PatrolGrainEaten(TimeSpan.FromHours(-1)));
    }

    [Fact]
    public void AnEmptyLarderCannotPayForAFight()
    {
        var cost = ProvisionRules.CostOfFight(LocationType.Dungeon);
        Assert.True(ProvisionRules.CanAfford(new Dictionary<string, long> { ["Grain"] = 2, ["Hides"] = 1 }, cost));
        Assert.False(ProvisionRules.CanAfford(new Dictionary<string, long> { ["Grain"] = 2 }, cost));
        Assert.False(ProvisionRules.CanAfford(new Dictionary<string, long> { ["Grain"] = 1, ["Hides"] = 5 }, cost));
        Assert.False(ProvisionRules.CanAfford(null!, cost));
    }

    [Fact]
    public void ALossBloodiesForHalfAnHour()
    {
        var recovers = BloodiedRules.RecoversAt(Now);
        Assert.Equal(Now.AddMinutes(30), recovers);
        Assert.True(BloodiedRules.IsBloodied(recovers, Now.AddMinutes(29)));
        Assert.False(BloodiedRules.IsBloodied(recovers, Now.AddMinutes(30)));
        Assert.False(BloodiedRules.IsBloodied(null, Now));
        Assert.Equal(TimeSpan.FromMinutes(10), BloodiedRules.Remaining(recovers, Now.AddMinutes(20)));
        Assert.Equal(TimeSpan.Zero, BloodiedRules.Remaining(recovers, Now.AddHours(1)));
    }

    [Fact]
    public void APatrolHalvesTheAmbushChanceAndASecondAddsNothing()
    {
        var walk = TimeSpan.FromMinutes(3);
        double open = AmbushRules.ChanceFor(2, true, walk);
        Assert.Equal(open, AmbushRules.ChanceFor(2, true, walk, patrolled: false));
        Assert.Equal(open * AmbushRules.PatrolFactor, AmbushRules.ChanceFor(2, true, walk, patrolled: true), 9);

        double route = AmbushRules.ChanceForRoute(new[] { (2, true, walk) });
        Assert.Equal(open, route, 9);
        Assert.True(AmbushRules.ChanceForRoute(new[] { (2, true, walk, true) }) < route);
    }

    [Fact]
    public void AutoGearIsRolledARarityDownAndAHandClearIsUnchanged()
    {
        // Nothing in the calculator changes for a hand clear: the step defaults to none.
        var tuning = new RunTuning();
        var hand = RunRewardCalculator.Calculate(5, 2, tuning, Array.Empty<ItemTemplate>(), new Random(1));
        var auto = RunRewardCalculator.Calculate(5, 2, tuning, Array.Empty<ItemTemplate>(), new Random(1), AutoFightRules.RarityStepsDown);
        Assert.Equal(hand.Experience, auto.Experience);
        Assert.Equal(hand.Gold, auto.Gold);
    }

    [Fact]
    public void AnAutoRoadIsRolledTheSameEveryTimeAndStrikesMidway()
    {
        var departed = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        int struck = 0;
        for (int i = 0; i < 400; i++)
        {
            var at = departed.AddMinutes(i);
            var once = AutoFightRules.RollAmbush(0.25, "company-a", at);
            Assert.Equal(once, AutoFightRules.RollAmbush(0.25, "company-a", at));
            if (once is double share)
            {
                struck++;
                Assert.InRange(share, AmbushRules.EarliestStrike, AmbushRules.LatestStrike);
            }
        }
        Assert.InRange(struck, 60, 140);   // about a quarter of 400
        Assert.Null(AutoFightRules.RollAmbush(0, "company-a", departed));
    }
}
