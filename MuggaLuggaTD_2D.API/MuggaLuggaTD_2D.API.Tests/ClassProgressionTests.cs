using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// How a player character's health grows.
///
/// <para>It did not, until now: a level bought ability upgrades and nothing else, while enemies
/// gained 15% health a level. The whole of a character's durability came from gear, and a level-20
/// party was as fragile as a level-1 one. Design doc 03 §2b.</para>
/// </summary>
public class ClassProgressionTests
{
    [Fact]
    public void ALevelOneCharacterHasExactlyWhatItsClassSays()
    {
        Assert.Equal(175, ClassProgression.ScaledHealth(175, 1, 0.08f));
    }

    [Fact]
    public void HealthGrowsByTheClassesOwnRate()
    {
        // A Warrior (8%) pulls away from a Mage (4%) over a climb, which is what makes the classes
        // feel different rather than just look different.
        Assert.Equal(301, ClassProgression.ScaledHealth(175, 10, 0.08f));
        Assert.Equal(136, ClassProgression.ScaledHealth(100, 10, 0.04f));

        Assert.Equal(441, ClassProgression.ScaledHealth(175, 20, 0.08f));
        Assert.Equal(176, ClassProgression.ScaledHealth(100, 20, 0.04f));
    }

    [Fact]
    public void GrowthIsAdditive_TheSameShapeEnemiesUse()
    {
        // So the two curves can be read against each other rather than diverging by construction.
        long atTen = ClassProgression.ScaledHealth(100, 10, 0.05f);
        long atNineteen = ClassProgression.ScaledHealth(100, 19, 0.05f);

        Assert.Equal(atTen - 100, atNineteen - atTen);
    }

    [Fact]
    public void ANonsenseLevelOrRateIsTreatedAsTheFloor()
    {
        Assert.Equal(100, ClassProgression.ScaledHealth(100, 0, 0.05f));
        Assert.Equal(100, ClassProgression.ScaledHealth(100, -4, 0.05f));
        Assert.Equal(100, ClassProgression.ScaledHealth(100, 10, -1f));
    }

    [Fact]
    public void ACharacterIsNeverLeftWithNoHealthAtAll()
    {
        Assert.Equal(1, ClassProgression.ScaledHealth(0, 5, 0.05f));
    }

    [Fact]
    public void AClassThatStatesNoGrowthStillGetsSome()
    {
        Assert.True(ClassProgression.DefaultHealthGrowthPerLevel > 0f);
        Assert.True(ClassProgression.ScaledHealth(100, 10, ClassProgression.DefaultHealthGrowthPerLevel) > 100);
    }

    [Fact]
    public void APlayerGrowsSlowerThanAnEnemyOfTheSameLevel()
    {
        // Deliberate: the player's answer to a levelled enemy is meant to be gear, talents and
        // upgrades, not raw health. This pins that the health curve alone does not outrun them.
        long player = ClassProgression.ScaledHealth(100, 20, 0.08f);
        long enemy = EnemyStatScaling.ScaledHealth(100, 20);

        Assert.True(player < enemy, $"player {player} should not outgrow enemy {enemy} on health alone");
    }
}
