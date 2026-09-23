using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// How an enemy grows with its level.
///
/// <para>Two things were wrong before this rule was shared. The client scaled an enemy's health by
/// <c>1 + 0.15xL</c> while the server priced the reward by <c>1 + 0.15x(L-1)</c>, so every clear paid
/// for a weaker enemy than the one the player fought. And an enemy dealt an ability's <i>full</i>
/// listed damage - the same number the player hits for - compounding 10% a level against player
/// health that never moved.</para>
/// </summary>
public class EnemyStatScalingTests
{
    // -----------------------------------------------------------------
    // Health
    // -----------------------------------------------------------------

    [Fact]
    public void ALevelOneEnemyHasExactlyWhatTheContentSays()
    {
        // The content number has to mean something on its own, or balancing by data is guesswork.
        Assert.Equal(100, EnemyStatScaling.ScaledHealth(100, 1));
    }

    [Fact]
    public void HealthGrowsByASharePerLevel_Additively()
    {
        Assert.Equal(115, EnemyStatScaling.ScaledHealth(100, 2));
        Assert.Equal(235, EnemyStatScaling.ScaledHealth(100, 10));
        Assert.Equal(385, EnemyStatScaling.ScaledHealth(100, 20));
    }

    [Fact]
    public void ACharacterCanOverrideItsOwnHealthGrowth()
    {
        Assert.Equal(200, EnemyStatScaling.ScaledHealth(100, 3, healthPerLevel: 0.5f));
    }

    [Fact]
    public void ALevelBelowOneIsTreatedAsLevelOne()
    {
        // Nothing should ever be below level 1, but a blended location level rounds, and a negative
        // multiplier would hand out an enemy with less health than its content says.
        Assert.Equal(100, EnemyStatScaling.ScaledHealth(100, 0));
        Assert.Equal(100, EnemyStatScaling.ScaledHealth(100, -5));
    }

    // -----------------------------------------------------------------
    // Damage
    // -----------------------------------------------------------------

    [Fact]
    public void AnEnemyHitsForAFractionOfWhatThePlayerWouldWithTheSameAbility()
    {
        // Magic Burst lists 50. A player casting it hits for 50; a level-1 enemy hits for 20, because
        // the player faces a wave of them and a mage has 40 health.
        Assert.Equal(20, EnemyStatScaling.EnemyAbilityDamage(50, 1));
    }

    [Fact]
    public void EnemyDamageGrowsLinearly_NotByCompounding()
    {
        // 20 at level 1, and 6% of that base per level after it. The old curve reached 122 by level 20.
        Assert.Equal(21, EnemyStatScaling.EnemyAbilityDamage(50, 2));
        Assert.Equal(31, EnemyStatScaling.EnemyAbilityDamage(50, 10));
        Assert.Equal(43, EnemyStatScaling.EnemyAbilityDamage(50, 20));
    }

    [Fact]
    public void AHitNeverRoundsAwayToNothing()
    {
        // A 1-damage ability would otherwise scale to 0.4 and round to zero, making the enemy
        // harmless rather than weak.
        Assert.Equal(1, EnemyStatScaling.EnemyAbilityDamage(1, 1));
        Assert.Equal(1, EnemyStatScaling.EnemyAbilityDamage(0, 1));
    }

    [Fact]
    public void TheDamageCurveIsGentlerThanTheHealthCurve()
    {
        // Deliberate: a levelled enemy should take longer to kill before it starts one-shotting the
        // party, because the player's own health does not yet scale with the map's level.
        double healthGrowth = (double)EnemyStatScaling.ScaledHealth(100, 20) / EnemyStatScaling.ScaledHealth(100, 1);
        double damageGrowth = (double)EnemyStatScaling.EnemyAbilityDamage(100, 20) / EnemyStatScaling.EnemyAbilityDamage(100, 1);

        Assert.True(damageGrowth < healthGrowth, "damage should grow slower than health");
    }
}
