using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The set piece a run ends on.
///
/// <para>A run that finishes when a counter reaches ten has no climax, and a boss with twelve times
/// an enemy's health and one attack is a wall rather than a fight. The phases are the answer: the
/// same enemy asks a different question at 100%, at 66% and at 33%.</para>
/// </summary>
public class BossRulesTests
{
    private static RunTuning Tuning(int bossFromTier = 3) => new() { BossFromTier = bossFromTier };

    [Fact]
    public void ABossOpensInItsFirstPhase()
    {
        Assert.Equal(1, BossRules.PhaseFor(1f));
        Assert.Equal(1, BossRules.PhaseFor(0.67f));
    }

    [Fact]
    public void EachThresholdIsANewPhase()
    {
        Assert.Equal(2, BossRules.PhaseFor(0.66f));
        Assert.Equal(2, BossRules.PhaseFor(0.5f));
        Assert.Equal(3, BossRules.PhaseFor(0.33f));
        Assert.Equal(3, BossRules.PhaseFor(0.01f));
    }

    [Fact]
    public void ThereIsOnePhaseMoreThanThereAreThresholds()
    {
        Assert.Equal(BossRules.PhaseThresholds.Length + 1, BossRules.PhaseCount);
        Assert.Equal(3, BossRules.PhaseCount);
    }

    [Fact]
    public void ADeadBossIsStillInItsLastPhase_NotPastIt()
    {
        // Clamped by the caller, but a negative fraction should not run off the end of the table.
        Assert.Equal(BossRules.PhaseCount, BossRules.PhaseFor(0f));
        Assert.Equal(BossRules.PhaseCount, BossRules.PhaseFor(-1f));
    }

    // -----------------------------------------------------------------
    // Which runs get one
    // -----------------------------------------------------------------

    [Fact]
    public void TheLowTiersAreAnErrand_NotASetPiece()
    {
        Assert.False(BossRules.HasBoss(Tuning(), 1));
        Assert.False(BossRules.HasBoss(Tuning(), 2));
    }

    [Fact]
    public void TheHighTiersEndOnABoss()
    {
        Assert.True(BossRules.HasBoss(Tuning(), 3));
        Assert.True(BossRules.HasBoss(Tuning(), 4));
    }

    [Fact]
    public void BossesCanBeTurnedOffEntirelyByContent()
    {
        Assert.False(BossRules.HasBoss(Tuning(bossFromTier: 0), 4));
        Assert.False(BossRules.HasBoss(null, 4));
    }

    // -----------------------------------------------------------------
    // What a boss is
    // -----------------------------------------------------------------

    [Fact]
    public void ABossIsFarTougherThanAnElite()
    {
        Assert.Equal(1200, BossRules.BossHealth(100));
        Assert.True(BossRules.BossHealth(100) > EliteRules.EliteHealth(100));
    }

    [Fact]
    public void ABossHitsHarderAndPaysMore()
    {
        Assert.Equal(30, BossRules.BossDamage(20));
        Assert.Equal(200, BossRules.BossExperience(100));
    }

    [Fact]
    public void ABossIsNeverHarmlessByRounding()
    {
        Assert.Equal(1, BossRules.BossHealth(0));
        Assert.Equal(1, BossRules.BossDamage(0));
    }

    // -----------------------------------------------------------------
    // What it is worth
    // -----------------------------------------------------------------

    [Fact]
    public void ARunWithABossPaysMoreThanTheSameRunWithout()
    {
        var withBoss = new RunTuning { BossFromTier = 3 };
        var withoutBoss = new RunTuning { BossFromTier = 0 };

        var paid = RunRewardCalculator.Calculate(5, 4, withBoss, null, new Random(1)).Experience;
        var unpaid = RunRewardCalculator.Calculate(5, 4, withoutBoss, null, new Random(1)).Experience;

        Assert.True(paid > unpaid, "a run that ends on a boss should be paid for one");
    }

    [Fact]
    public void ALowTierRunIsPricedTheSameEitherWay()
    {
        var withBoss = new RunTuning { BossFromTier = 3 };
        var withoutBoss = new RunTuning { BossFromTier = 0 };

        Assert.Equal(
            RunRewardCalculator.Calculate(5, 1, withoutBoss, null, new Random(1)).Experience,
            RunRewardCalculator.Calculate(5, 1, withBoss, null, new Random(1)).Experience);
    }
}
