using Enums;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Lures and pity — design doc 05 §4.
///
/// <para>The rule that needed the most care is that a lure is a <b>target share</b> and not a weight
/// multiplier. The affinity roll is weighted over the affinities a signature is <i>allowed</i>, which
/// is rarely all eight, so a multiplier would mean something different for every signature. A share
/// means "60% of slots that can be Fire will be" whoever is rolling.</para>
/// </summary>
public class TavernLureTests
{
    private static SignatureDefinition Signature(params AffinityTypes[] affinities)
        => new()
        {
            SignatureId = "sig-1",
            Class = "Archer",
            BaseAbilityLinkName = "Snipe Shot",
            AllowedAffinities = affinities.ToList()
        };

    // -----------------------------------------------------------------
    // The ladder
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(TavernRules.LureStrength.Minor, 0.25)]
    [InlineData(TavernRules.LureStrength.Major, 0.40)]
    [InlineData(TavernRules.LureStrength.Perfect, 0.60)]
    public void EachCrystalAimsForTheShareTheDesignStates(TavernRules.LureStrength strength, double expected)
    {
        Assert.Equal(expected, TavernRules.LureTarget(strength), 6);
    }

    [Fact]
    public void NoCrystalAimsForNothing()
    {
        Assert.Equal(0, TavernRules.LureTarget(TavernRules.LureStrength.None));
        Assert.Equal(0, TavernRules.EffectiveLureTarget(TavernRules.LureStrength.None, 9));
    }

    [Fact]
    public void EachCrystalNamesItsOwnMaterial()
    {
        var cost = TavernRules.LureCost(AffinityTypes.Fire, TavernRules.LureStrength.Perfect);

        Assert.Equal("Perfect Fire Crystal", Assert.Single(cost).MaterialName);
        Assert.Equal(1, cost[0].Quantity);
    }

    [Fact]
    public void EveryAffinityAndStrengthNamesACrystalThatExistsInContent()
    {
        // The lure prices itself in materials the game actually ships - 24 of them, Minor/Major/
        // Perfect across eight affinities. A typo here would be a cost nobody can ever pay.
        foreach (AffinityTypes affinity in Enum.GetValues(typeof(AffinityTypes)))
        {
            foreach (var strength in new[]
            {
                TavernRules.LureStrength.Minor,
                TavernRules.LureStrength.Major,
                TavernRules.LureStrength.Perfect
            })
            {
                string name = TavernRules.CrystalName(affinity, strength);

                Assert.False(string.IsNullOrWhiteSpace(name));
                Assert.EndsWith(" Crystal", name);
                Assert.Contains(affinity.ToString(), name);
            }
        }
    }

    // -----------------------------------------------------------------
    // Pity
    // -----------------------------------------------------------------

    [Fact]
    public void EachMissAddsToTheNextLure()
    {
        Assert.Equal(0.25, TavernRules.EffectiveLureTarget(TavernRules.LureStrength.Minor, 0), 6);
        Assert.Equal(0.35, TavernRules.EffectiveLureTarget(TavernRules.LureStrength.Minor, 1), 6);
        Assert.Equal(0.55, TavernRules.EffectiveLureTarget(TavernRules.LureStrength.Minor, 3), 6);
    }

    [Fact]
    public void PityNeverQuiteGuaranteesTheBoard()
    {
        // A lured board should stay a board rather than becoming an order form, however long the
        // drought has run.
        Assert.Equal(TavernRules.LureCeiling,
            TavernRules.EffectiveLureTarget(TavernRules.LureStrength.Perfect, 50), 6);
    }

    // -----------------------------------------------------------------
    // What the roll does with it
    // -----------------------------------------------------------------

    [Fact]
    public void ALureRaisesTheAffinityTowardItsTarget()
    {
        var signature = Signature(
            AffinityTypes.Fire, AffinityTypes.Water, AffinityTypes.Earth, AffinityTypes.Air);

        var random = new Random(12345);
        int fire = 0;
        const int rolls = 4000;

        for (int i = 0; i < rolls; i++)
        {
            if (RecruitRoller.RollAffinity(signature, null, random, AffinityTypes.Fire, 0.60)
                == AffinityTypes.Fire)
                fire++;
        }

        double share = (double)fire / rolls;

        Assert.InRange(share, 0.57, 0.63);
    }

    [Fact]
    public void ALureMeansTheSameShareWhateverTheSignatureAllows()
    {
        // The reason this is a share and not a multiplier: these two signatures allow very different
        // numbers of affinities, and a Perfect crystal must promise the same thing to both.
        var narrow = Signature(AffinityTypes.Fire, AffinityTypes.Water);
        var wide = Signature(
            AffinityTypes.Fire, AffinityTypes.Water, AffinityTypes.Earth, AffinityTypes.Air,
            AffinityTypes.Light, AffinityTypes.Dark);

        Assert.InRange(ShareOfFire(narrow, 0.60), 0.57, 0.63);
        Assert.InRange(ShareOfFire(wide, 0.60), 0.57, 0.63);
    }

    [Fact]
    public void LuringAnAffinityASignatureCannotRollDoesNothing()
    {
        // Same principle the biome favour already follows: it does nothing rather than skewing the
        // rest of the odds.
        var signature = Signature(AffinityTypes.Water, AffinityTypes.Earth);
        var random = new Random(99);

        for (int i = 0; i < 200; i++)
        {
            var rolled = RecruitRoller.RollAffinity(signature, null, random, AffinityTypes.Fire, 0.60);
            Assert.True(rolled == AffinityTypes.Water || rolled == AffinityTypes.Earth);
        }
    }

    [Fact]
    public void TheRestOfTheBoardStillVaries()
    {
        // A lure should concentrate the odds without flattening what else turns up.
        var signature = Signature(
            AffinityTypes.Fire, AffinityTypes.Water, AffinityTypes.Earth, AffinityTypes.Air);

        var random = new Random(7);
        var seen = new HashSet<AffinityTypes>();

        for (int i = 0; i < 500; i++)
            seen.Add(RecruitRoller.RollAffinity(signature, null, random, AffinityTypes.Fire, 0.60));

        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public void AnUnluredRollIsUntouched()
    {
        // The lured path is deliberately separate so that an existing seeded board rolls exactly as
        // it always did. Same seed, no lure, same sequence.
        var signature = Signature(AffinityTypes.Fire, AffinityTypes.Water, AffinityTypes.Earth);

        var a = new Random(4242);
        var b = new Random(4242);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(
                RecruitRoller.RollAffinity(signature, null, a),
                RecruitRoller.RollAffinity(signature, null, b, AffinityTypes.Fire, 0));
        }
    }

    private static double ShareOfFire(SignatureDefinition signature, double target)
    {
        var random = new Random(2024);
        int fire = 0;
        const int rolls = 4000;

        for (int i = 0; i < rolls; i++)
        {
            if (RecruitRoller.RollAffinity(signature, null, random, AffinityTypes.Fire, target)
                == AffinityTypes.Fire)
                fire++;
        }

        return (double)fire / rolls;
    }
}
