using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Elites: the lever a horde game needs so that ramping does not just mean more of the same.
///
/// <para>These are shared rules because the server prices a run from the fight the location demands.
/// If the client spawned elites and the pricing did not know about them, a run would be paid for an
/// easier fight than the one fought - the same mismatch <see cref="EnemyStatScaling"/> exists to
/// prevent.</para>
/// </summary>
public class EliteRulesTests
{
    private static RunTuning Tuning(int from = 3, int interval = 2) => new()
    {
        ElitesFromWave = from,
        EliteIntervalWaves = interval
    };

    [Fact]
    public void TheFirstWavesHaveNoElites()
    {
        // A run should start as a trickle. Meeting an elite in wave one is not a ramp.
        Assert.Equal(0, EliteRules.ElitesInWave(Tuning(), 1));
        Assert.Equal(0, EliteRules.ElitesInWave(Tuning(), 2));
    }

    [Fact]
    public void ElitesStartAtTheWaveTheTuningNames_AndBuildUp()
    {
        var tuning = Tuning();

        Assert.Equal(1, EliteRules.ElitesInWave(tuning, 3));
        Assert.Equal(1, EliteRules.ElitesInWave(tuning, 4));
        Assert.Equal(2, EliteRules.ElitesInWave(tuning, 5));
        Assert.Equal(3, EliteRules.ElitesInWave(tuning, 7));
    }

    [Fact]
    public void TheScheduleIsTunable()
    {
        var earlyAndSteep = Tuning(from: 2, interval: 1);

        Assert.Equal(0, EliteRules.ElitesInWave(earlyAndSteep, 1));
        Assert.Equal(1, EliteRules.ElitesInWave(earlyAndSteep, 2));
        Assert.Equal(3, EliteRules.ElitesInWave(earlyAndSteep, 4));
    }

    [Fact]
    public void NoTuningMeansNoElites_RatherThanACrash()
    {
        Assert.Equal(0, EliteRules.ElitesInWave(null, 5));
        Assert.Equal(0, EliteRules.ElitesInWave(Tuning(), 0));
        Assert.Equal(0, EliteRules.ElitesInWave(Tuning(), -1));
    }

    // -----------------------------------------------------------------
    // A siege's champions
    // -----------------------------------------------------------------

    [Fact]
    public void ASiegesChampionsAllGetIntoTheAssault()
    {
        int total = 0;
        for (int wave = 1; wave <= 5; wave++)
            total += EliteRules.SiegeElitesInWave(3, totalWaves: 5, waveNumber: wave);

        Assert.Equal(3, total);
    }

    [Fact]
    public void TheDefenceGetsHeavierTowardsTheEnd_NotThinner()
    {
        // Three champions over five waves: the last three waves, not the first three.
        Assert.Equal(0, EliteRules.SiegeElitesInWave(3, 5, 1));
        Assert.Equal(0, EliteRules.SiegeElitesInWave(3, 5, 2));
        Assert.Equal(1, EliteRules.SiegeElitesInWave(3, 5, 3));
        Assert.Equal(1, EliteRules.SiegeElitesInWave(3, 5, 5));
    }

    [Fact]
    public void MoreChampionsThanWaves_SpreadEvenlyWithTheRemainderAtTheEnd()
    {
        // Eight champions over three waves: 2, 3, 3.
        Assert.Equal(2, EliteRules.SiegeElitesInWave(8, 3, 1));
        Assert.Equal(3, EliteRules.SiegeElitesInWave(8, 3, 2));
        Assert.Equal(3, EliteRules.SiegeElitesInWave(8, 3, 3));
    }

    [Fact]
    public void AnUndefendedRegionAddsNoChampions()
    {
        Assert.Equal(0, EliteRules.SiegeElitesInWave(0, 5, 3));
        Assert.Equal(0, EliteRules.SiegeElitesInWave(-2, 5, 3));
        Assert.Equal(0, EliteRules.SiegeElitesInWave(3, 5, 9));
    }

    // -----------------------------------------------------------------
    // What an elite is
    // -----------------------------------------------------------------

    [Fact]
    public void AnEliteIsTougherHarderAndWorthMore()
    {
        Assert.Equal(200, EliteRules.EliteHealth(100));
        Assert.Equal(25, EliteRules.EliteDamage(20));
        Assert.Equal(150, EliteRules.EliteExperience(100));
    }

    [Fact]
    public void AnEliteIsNeverHarmlessByRounding()
    {
        Assert.Equal(1, EliteRules.EliteHealth(0));
        Assert.Equal(1, EliteRules.EliteDamage(0));
    }
}
