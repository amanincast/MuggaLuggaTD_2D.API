using MuggaLuggaTD.Shared.Gameplay;
using Xunit;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// A cleared site locks only its clearer, for ten minutes, and shapes the realm once per eight
/// hours (Mike, 2026-10-03: keep players moving, never waiting).
/// </summary>
public class SiteRotationRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ASiteNeverClearedIsOpenAndPaysTheRealm()
    {
        Assert.False(SiteRotationRules.IsLocked(null, Now));
        Assert.Equal(DateTime.MinValue, SiteRotationRules.LockedUntil(null));
        Assert.True(SiteRotationRules.WorldRewardsDue(null, Now));
    }

    [Fact]
    public void AClearLocksItsClearerForTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), SiteRotationRules.Lockout);
        var cleared = Now.AddMinutes(-9);
        Assert.True(SiteRotationRules.IsLocked(cleared, Now));
        Assert.Equal(cleared.AddMinutes(10), SiteRotationRules.LockedUntil(cleared));
        Assert.False(SiteRotationRules.IsLocked(Now.AddMinutes(-10), Now));
    }

    [Fact]
    public void TheRealmIsShapedOnceEveryEightHours()
    {
        Assert.Equal(TimeSpan.FromHours(8), SiteRotationRules.WorldRewardWindow);
        Assert.False(SiteRotationRules.WorldRewardsDue(Now.AddHours(-7), Now));
        Assert.True(SiteRotationRules.WorldRewardsDue(Now.AddHours(-8), Now));
        Assert.Equal(Now.AddHours(1), SiteRotationRules.WorldRewardsBackAt(Now.AddHours(-7)));
    }

    [Fact]
    public void TheLockoutIsFarShorterThanTheRewardWindow()
    {
        // The rotation is the farming loop; the realm rewards are what is rationed.
        Assert.True(SiteRotationRules.Lockout * 10 < SiteRotationRules.WorldRewardWindow);
    }
}
