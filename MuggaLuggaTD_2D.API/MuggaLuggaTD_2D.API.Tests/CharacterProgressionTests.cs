using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// What a level costs.
///
/// <para>The old curve grew at 1.5x a level while a run's payout grows at roughly 1.2x, so the two
/// pulled apart in both directions: the first levels fell in a handful of runs and then it walled.
/// Matching the curve's growth to the reward's is what makes runs-per-level roughly flat, which is
/// the property these tests actually pin - the constants themselves are meant to be retuned after
/// playtests.</para>
/// </summary>
public class CharacterProgressionTests
{
    [Fact]
    public void TheFirstLevelCostsTheBase()
    {
        Assert.Equal(CharacterProgression.BaseExperienceToLevel, CharacterProgression.RequiredExperienceFor(1));
    }

    [Fact]
    public void EachLevelCostsMoreThanTheOneBefore()
    {
        for (long level = 1; level < CharacterProgression.MaxLevel - 1; level++)
        {
            Assert.True(CharacterProgression.RequiredExperienceFor(level + 1)
                        > CharacterProgression.RequiredExperienceFor(level),
                "a later level should cost more, at level " + level);
        }
    }

    [Fact]
    public void TheCapCostsNothing_WhichIsWhatStopsTheLevelUpLoop()
    {
        Assert.Equal(0, CharacterProgression.RequiredExperienceFor(CharacterProgression.MaxLevel));
        Assert.Equal(0, CharacterProgression.RequiredExperienceFor(CharacterProgression.MaxLevel + 10));
        Assert.True(CharacterProgression.IsAtCap(CharacterProgression.MaxLevel));
        Assert.False(CharacterProgression.IsAtCap(CharacterProgression.MaxLevel - 1));
    }

    [Fact]
    public void ALevelBelowOneIsTreatedAsLevelOne()
    {
        Assert.Equal(CharacterProgression.RequiredExperienceFor(1), CharacterProgression.RequiredExperienceFor(0));
        Assert.Equal(CharacterProgression.RequiredExperienceFor(1), CharacterProgression.RequiredExperienceFor(-3));
    }

    [Fact]
    public void CumulativeExperienceIsTheSumOfTheLevelsBelowIt()
    {
        Assert.Equal(0, CharacterProgression.CumulativeExperienceFor(1));
        Assert.Equal(CharacterProgression.RequiredExperienceFor(1), CharacterProgression.CumulativeExperienceFor(2));
        Assert.Equal(
            CharacterProgression.RequiredExperienceFor(1)
            + CharacterProgression.RequiredExperienceFor(2)
            + CharacterProgression.RequiredExperienceFor(3),
            CharacterProgression.CumulativeExperienceFor(4));
    }

    [Fact]
    public void PastTheCapCostsNoMore()
    {
        Assert.Equal(
            CharacterProgression.CumulativeExperienceFor(CharacterProgression.MaxLevel),
            CharacterProgression.CumulativeExperienceFor(CharacterProgression.MaxLevel + 25));
    }

    [Fact]
    public void RunsPerLevelStayRoughlyFlatAcrossTheWholeCurve()
    {
        // The point of the whole change. A run's payout grows with the levels it is fought at, so if
        // the curve grows at the same rate, the number of runs a level takes barely moves. Compare
        // how much the cost of a level grows against how much a run's value grows.
        const double rewardGrowthPerLevel = 1.2;

        for (long level = 1; level < CharacterProgression.MaxLevel - 1; level++)
        {
            double costGrowth = (double)CharacterProgression.RequiredExperienceFor(level + 1)
                                / CharacterProgression.RequiredExperienceFor(level);

            // Within 1%: the curve tracks the payout rather than outrunning it.
            Assert.InRange(costGrowth / rewardGrowthPerLevel, 0.99, 1.01);
        }
    }

    [Fact]
    public void TheLastLevelIsNotAWall()
    {
        // The shape, not the number. On the old 1.5x curve the last level of a 30-level climb cost
        // about 127,000 times the first, against a payout that had grown a few hundredfold - which is
        // what "it walls" means arithmetically. Here it is a couple of hundred, matching the payout.
        double spread = (double)CharacterProgression.RequiredExperienceFor(CharacterProgression.MaxLevel - 1)
                        / CharacterProgression.RequiredExperienceFor(1);

        Assert.InRange(spread, 50, 400);
    }
}
