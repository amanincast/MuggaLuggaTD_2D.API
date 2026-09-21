using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// When a cleared site offers a fight again.
///
/// <para>This rule exists to close an imbalance rather than to add content: clearing a region's own
/// hostile sites is the only way to restore its resolve, so a site that stayed cleared forever made
/// a region's defence finite while raiding it was not. The tests that matter most here are the ones
/// about that arithmetic, at the bottom.</para>
/// </summary>
public class SiteRespawnRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private static SiteOverride ClearedAt(DateTime when) => new()
    {
        Cleared = true,
        ClearedAtUtcTicks = when.Ticks
    };

    [Fact]
    public void ASiteNobodyHasClearedIsAvailable()
    {
        Assert.False(SiteRespawnRules.IsCleared(null, Now));
        Assert.False(SiteRespawnRules.IsCleared(new SiteOverride(), Now));
    }

    [Fact]
    public void AGarrisonedSiteThatWasNeverClearedIsStillAvailable()
    {
        // Overrides carry more than clearance. A keep with defenders in it has not been "spent".
        var over = new SiteOverride { GarrisonPower = 500f };

        Assert.False(SiteRespawnRules.IsCleared(over, Now));
    }

    [Fact]
    public void AJustClearedSiteIsSpent()
    {
        Assert.True(SiteRespawnRules.IsCleared(ClearedAt(Now), Now));
    }

    [Fact]
    public void ASiteStaysSpentForTheWholeWindow()
    {
        var over = ClearedAt(Now);

        Assert.True(SiteRespawnRules.IsCleared(over, Now.AddHours(1)));
        Assert.True(SiteRespawnRules.IsCleared(over, Now.AddHours(SiteRespawnRules.RespawnHours - 1)));
        Assert.True(SiteRespawnRules.IsCleared(over, Now.AddHours(SiteRespawnRules.RespawnHours).AddSeconds(-1)));
    }

    [Fact]
    public void ASiteComesBackWhenTheWindowLapses()
    {
        var over = ClearedAt(Now);

        Assert.False(SiteRespawnRules.IsCleared(over, Now.AddHours(SiteRespawnRules.RespawnHours)));
        Assert.False(SiteRespawnRules.IsCleared(over, Now.AddDays(3)));
    }

    [Fact]
    public void ASiteClearedBeforeThisRuleExistedIsTreatedAsRecovered()
    {
        // Those carry no timestamp. Treating them as spent forever would leave permanently dead
        // ground in every world that predates the change; this direction is self-healing.
        var legacy = new SiteOverride { Cleared = true, ClearedAtUtcTicks = 0 };

        Assert.False(SiteRespawnRules.IsCleared(legacy, Now));
        Assert.Equal(DateTime.MinValue, SiteRespawnRules.RecoversAt(legacy));
    }

    [Fact]
    public void AClearedSiteSaysWhenItComesBack()
    {
        Assert.Equal(Now.AddHours(SiteRespawnRules.RespawnHours), SiteRespawnRules.RecoversAt(ClearedAt(Now)));
    }

    [Fact]
    public void AnAvailableSiteHasNoRecoveryTime()
    {
        Assert.Equal(DateTime.MinValue, SiteRespawnRules.RecoversAt(new SiteOverride()));
        Assert.Equal(DateTime.MinValue, SiteRespawnRules.RecoversAt(null));
    }

    // -----------------------------------------------------------------
    // The arithmetic this rule exists to fix
    // -----------------------------------------------------------------

    [Fact]
    public void ADefendersAnswerIsNoLongerFinite()
    {
        // The bug: a region held N dungeons, each restoring resolve once, so a defender could return
        // at most N x RestoredPerClear resolve *ever*. Raiding had no such ceiling, so an attacker
        // won by arithmetic however well the defence was played.
        var region = TestSupport.TestWorld.OwnedBy(TestSupport.TestIds.Player);
        var dungeons = TestSupport.TestWorld.SitesIn(region)
            .Where(s => s.IsFightable).ToList();

        Assert.NotEmpty(dungeons);

        // Clear every one of them.
        foreach (var site in dungeons)
            region.SiteOverrides[site.SiteId] = ClearedAt(Now);

        // Immediately afterwards the region has nothing left to offer — that much is unchanged, and
        // is what bounds farming.
        Assert.All(dungeons, s => Assert.True(SiteRespawnRules.IsCleared(region.SiteOverrides[s.SiteId], Now)));

        // But a window later they are all fights again, so the defender's answer renews.
        var later = Now.AddHours(SiteRespawnRules.RespawnHours);
        Assert.All(dungeons, s => Assert.False(SiteRespawnRules.IsCleared(region.SiteOverrides[s.SiteId], later)));
    }

    [Fact]
    public void ACommittedDefenderRoughlyMatchesACommittedAttacker()
    {
        // The rates are meant to be comparable across the same stretch of real time: neither side
        // should win on arithmetic alone. This pins that they are within reach of each other, not
        // that they are equal — see SiteRespawnRules for why the asymmetry that remains is honest.
        var region = TestSupport.TestWorld.OwnedBy(TestSupport.TestIds.Player);
        int fightableSites = TestSupport.TestWorld.SitesIn(region).Count(s => s.IsFightable);

        int defenderPerWindow = fightableSites * RegionResolveRules.RestoredPerClear;

        // An attacker raids once per cooldown, taking at most the maximum each time.
        int raidsPerWindow = SiteRespawnRules.RespawnHours / RaidResolver.CooldownHours;
        int attackerPerWindow = raidsPerWindow * RaidResolver.MaximumResolveDamage;

        Assert.True(defenderPerWindow >= attackerPerWindow,
            $"A defender playing every site returns {defenderPerWindow} resolve per window, " +
            $"against an attacker's best case of {attackerPerWindow}. The defence cannot keep up.");

        // And not so far ahead that raiding becomes pointless against anyone who shows up.
        Assert.True(defenderPerWindow <= attackerPerWindow * 3,
            $"A defender returns {defenderPerWindow} against {attackerPerWindow}; raiding is futile.");
    }

    [Fact]
    public void ANeglectedRegionStillFalls()
    {
        // The rule must not turn into immunity: recovery does nothing on its own, it only makes the
        // answer available. A player who never comes back still loses the ground.
        int resolve = RegionResolveRules.Maximum;
        long hold = RegionHoldCalculator.Hold(garrisonPower: 0, tier: 2, entrenchment: 0, supply: 1.0, resolve: resolve);

        for (int raid = 0; raid < 20 && resolve > 0; raid++)
            resolve = RaidResolver.Resolve(hold, hold, resolve, d20Roll: 20).ResolveAfter;

        Assert.Equal(0, resolve);
    }
}
