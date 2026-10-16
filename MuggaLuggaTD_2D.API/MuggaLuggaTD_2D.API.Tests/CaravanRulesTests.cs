using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Caravans, the protection fight (Active Content F): the build points, the hunters, the lanes, the wave clock and the
/// share of the cargo that gets through.
/// </summary>
public class CaravanRulesTests
{
    [Fact]
    public void EveryPieceHasAPriceTheStartingPointsCanPay()
    {
        foreach (var piece in CaravanRules.Pieces)
            Assert.InRange(CaravanRules.Cost(piece), 1, CaravanRules.StartingPoints);
        Assert.Equal(4, CaravanRules.Pieces.Length);
    }

    [Fact]
    public void PointsComeWithTimeOnceTheWavesBegin()
    {
        Assert.Equal(8, CaravanRules.PointsEarned(0));
        Assert.Equal(8, CaravanRules.PointsEarned(9.9));
        Assert.Equal(9, CaravanRules.PointsEarned(10));
        Assert.Equal(18, CaravanRules.PointsEarned(100));
        Assert.Equal(8, CaravanRules.PointsEarned(-3));
        Assert.Equal(10, CaravanRules.SecondsToNextPoint(0), 3);
        Assert.Equal(5, CaravanRules.SecondsToNextPoint(35), 3);
    }

    [Fact]
    public void ElitesBossesAndEveryFourthEnemyHuntTheHeroes()
    {
        Assert.True(CaravanRules.HuntsHeroes(0, elite: true, boss: false));
        Assert.True(CaravanRules.HuntsHeroes(0, elite: false, boss: true));
        var hunters = Enumerable.Range(0, 100).Count(i => CaravanRules.HuntsHeroes(i, false, false));
        Assert.Equal(25, hunters);
        Assert.False(CaravanRules.HuntsHeroes(0, false, false));
        Assert.True(CaravanRules.HuntsHeroes(3, false, false));
    }

    [Fact]
    public void AWaveComesByTwoOrThreeDistinctSides_TheSameEveryTime()
    {
        for (int seed = 0; seed < 50; seed++)
            for (int wave = 1; wave <= CaravanRules.Waves; wave++)
            {
                var lanes = CaravanRules.Lanes(seed, wave);
                Assert.InRange(lanes.Count, CaravanRules.FewestLanes, CaravanRules.MostLanes);
                Assert.Equal(lanes.Count, lanes.Distinct().Count());
                Assert.Equal(lanes, CaravanRules.Lanes(seed, wave));
            }
    }

    [Fact]
    public void LanesVaryFromWaveToWave()
    {
        var seen = new HashSet<string>();
        for (int wave = 1; wave <= 20; wave++)
            seen.Add(string.Join(",", CaravanRules.Lanes(7, wave)));
        Assert.True(seen.Count > 3);
    }

    [Fact]
    public void TheFirstWaveWaitsForTheSetup()
    {
        Assert.False(CaravanRules.NextWaveDue(0, 29.9, 29.9));
        Assert.True(CaravanRules.NextWaveDue(0, 30, -1));
    }

    [Fact]
    public void LaterWavesComeAfterALull_OrWhenTheClockRunsOut()
    {
        Assert.False(CaravanRules.NextWaveDue(1, 12, -1));
        Assert.False(CaravanRules.NextWaveDue(1, 12, 7.9));
        Assert.True(CaravanRules.NextWaveDue(1, 12, 8));
        Assert.True(CaravanRules.NextWaveDue(2, 40, -1));
    }

    [Fact]
    public void EachStandingWagonDeliversAThird()
    {
        Assert.Equal(1.0, CaravanRules.DeliveredShare(3));
        Assert.Equal(2.0 / 3, CaravanRules.DeliveredShare(2), 6);
        Assert.Equal(1.0 / 3, CaravanRules.DeliveredShare(1), 6);
        Assert.Equal(0.0, CaravanRules.DeliveredShare(0));
        Assert.Equal(1.0, CaravanRules.DeliveredShare(9));
        Assert.True(CaravanRules.IsLost(0));
        Assert.False(CaravanRules.IsLost(1));
    }

    [Fact]
    public void APerfectDefenceKeepsEveryWagonAboveHalf()
    {
        Assert.True(CaravanRules.IsPerfect(3, 0.5));
        Assert.False(CaravanRules.IsPerfect(3, 0.49));
        Assert.False(CaravanRules.IsPerfect(2, 1));
    }

    [Fact]
    public void SiteIdsRoundTrip()
    {
        Assert.True(CaravanRules.TryParseSiteId(CaravanRules.SiteId("abc"), out var id));
        Assert.Equal("abc", id);
        Assert.False(CaravanRules.TryParseSiteId("caravan:", out _));
        Assert.False(CaravanRules.TryParseSiteId("trial:3", out _));
    }
}
