using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The Tavern's roll.
///
/// <para>Odds are asserted over a large sample with generous bands: the point is that the dial is
/// wired to the thing it names, not that a pseudo-random sequence hits a target exactly. A test that
/// pinned an exact count would fail on a seed change and tell nobody anything.</para>
/// </summary>
public class RecruitRollerTests
{
    private static readonly List<RecruitSheet> Sheets = new()
    {
        new RecruitSheet { Sheet = "Ally_Warrior_1", Class = "Warrior" },
        new RecruitSheet { Sheet = "Ally_Warrior_2", Class = "Warrior" },
        new RecruitSheet { Sheet = "Ally_Mage_1", Class = "Mage" },
        // A class with a sheet but no signature. Nothing should ever be rolled onto it.
        new RecruitSheet { Sheet = "Ally_Cleric_1", Class = "Cleric" }
    };

    private static readonly List<SignatureDefinition> Signatures = new()
    {
        new SignatureDefinition { SignatureId = "warrior_cleave", Class = "Warrior", BaseAbilityLinkName = "Cleaving_Blow_1" },
        new SignatureDefinition { SignatureId = "warrior_slam", Class = "Warrior", BaseAbilityLinkName = "Ground_Slam_1" },
        new SignatureDefinition
        {
            SignatureId = "mage_blast",
            Class = "Mage",
            BaseAbilityLinkName = "Magic_Burst_1",
            AllowedAffinities = new List<AffinityTypes>
            {
                AffinityTypes.Fire, AffinityTypes.Water, AffinityTypes.Earth, AffinityTypes.Air,
                AffinityTypes.Light, AffinityTypes.Dark, AffinityTypes.Arcane
            }
        }
    };

    private static List<RecruitRoll> Sample(int count, int tier = 1, BiomeType? biome = null, int seed = 20260923)
    {
        var random = new Random(seed);
        var rolls = new List<RecruitRoll>();
        for (int i = 0; i < count; i++)
            rolls.Add(RecruitRoller.Roll(Sheets, Signatures, tier, biome, random)!);
        return rolls;
    }

    // -----------------------------------------------------------------
    // What a roll is made of
    // -----------------------------------------------------------------

    [Fact]
    public void ARecruitIsAlwaysSomethingTheGameCanBuild()
    {
        foreach (var roll in Sample(500))
        {
            Assert.False(string.IsNullOrEmpty(roll.Name));
            Assert.False(string.IsNullOrEmpty(roll.Sheet));

            var signature = SignatureRules.Find(Signatures, roll.SignatureId);
            Assert.NotNull(signature);
            Assert.Equal(roll.Class, signature!.Class);
            Assert.Contains(roll.Sheet, Sheets.Where(s => s.Class == roll.Class).Select(s => s.Sheet));
            Assert.True(SignatureRules.IsAffinityAllowed(signature, roll.Affinity));
        }
    }

    [Fact]
    public void AClassWithNoSignature_IsNeverRolled()
    {
        // A Cleric has a sheet and nothing to do with it; hiring one would be hiring a character
        // with no ability at all.
        Assert.DoesNotContain(Sample(500), r => r.Class == "Cleric");
    }

    [Fact]
    public void AMageIsNeverPhysical()
    {
        Assert.DoesNotContain(Sample(1000).Where(r => r.Class == "Mage"), r => r.Affinity == AffinityTypes.Physical);
    }

    [Fact]
    public void ContentWithNoRollableClass_ProducesNothingRatherThanAHalfCharacter()
    {
        Assert.Null(RecruitRoller.Roll(Sheets, new List<SignatureDefinition>(), 1, null, new Random(1)));
        Assert.Null(RecruitRoller.Roll(new List<RecruitSheet>(), Signatures, 1, null, new Random(1)));
    }

    // -----------------------------------------------------------------
    // Rarity, and what the dungeon's tier buys
    // -----------------------------------------------------------------

    [Fact]
    public void RarityIsRoughlyTheAdvertisedOdds()
    {
        var rolls = Sample(5000);

        double common = rolls.Count(r => r.Rarity == CharacterRarity.Common) / 5000.0;
        double legendary = rolls.Count(r => r.Rarity == CharacterRarity.Legendary) / 5000.0;

        Assert.InRange(common, 0.55, 0.65);
        Assert.InRange(legendary, 0.005, 0.04);
    }

    [Fact]
    public void ADeeperDungeonRaisesTheCeiling_AndTakesItOutOfCommon()
    {
        var shallow = TavernRules.RarityOdds(1);
        var deep = TavernRules.RarityOdds(4);

        Assert.True(Weight(deep, CharacterRarity.Epic) > Weight(shallow, CharacterRarity.Epic));
        Assert.True(Weight(deep, CharacterRarity.Legendary) > Weight(shallow, CharacterRarity.Legendary));
        Assert.True(Weight(deep, CharacterRarity.Common) < Weight(shallow, CharacterRarity.Common));

        // The middle is untouched: the tier raises the ceiling, it does not hollow the board out.
        Assert.Equal(Weight(shallow, CharacterRarity.Rare), Weight(deep, CharacterRarity.Rare));
    }

    [Fact]
    public void TierOne_IsTheAdvertisedBaseOdds()
    {
        var odds = TavernRules.RarityOdds(1);

        Assert.Equal(600, Weight(odds, CharacterRarity.Common));
        Assert.Equal(280, Weight(odds, CharacterRarity.Rare));
        Assert.Equal(100, Weight(odds, CharacterRarity.Epic));
        Assert.Equal(20, Weight(odds, CharacterRarity.Legendary));
        Assert.Equal(1000, odds.Sum(o => o.Weight));
    }

    [Fact]
    public void TheOddsAlwaysSumToTheSameThing_SoATierCannotInventProbability()
    {
        for (int tier = 1; tier <= 8; tier++)
            Assert.Equal(1000, TavernRules.RarityOdds(tier).Sum(o => o.Weight));
    }

    // -----------------------------------------------------------------
    // Biome
    // -----------------------------------------------------------------

    [Fact]
    public void ABiomeFavoursItsOwnAffinity_WithoutGuaranteeingIt()
    {
        var volcanic = Sample(3000, biome: BiomeType.Volcanic).Where(r => r.Class == "Mage").ToList();
        var anywhere = Sample(3000, biome: null, seed: 7).Where(r => r.Class == "Mage").ToList();

        double fireThere = volcanic.Count(r => r.Affinity == AffinityTypes.Fire) / (double)volcanic.Count;
        double fireAnywhere = anywhere.Count(r => r.Affinity == AffinityTypes.Fire) / (double)anywhere.Count;

        Assert.True(fireThere > fireAnywhere, $"volcanic {fireThere:P0} should beat unfavoured {fireAnywhere:P0}");

        // A nudge, not a guarantee - the guarantee is what the crystal lures are for.
        Assert.True(fireThere < 0.5, $"volcanic {fireThere:P0} should still leave most of the board to other elements");
        Assert.Contains(volcanic, r => r.Affinity != AffinityTypes.Fire);
    }

    [Fact]
    public void ABiomeFavouringAnAffinityASignatureCannotHave_ChangesNothing()
    {
        // A mage's Blast has no Physical form. Nothing favours Physical, but the rule has to hold for
        // any biome/signature pair, so assert it on the mechanism.
        var blast = Signatures.Single(s => s.SignatureId == "mage_blast");
        var random = new Random(99);

        for (int i = 0; i < 200; i++)
            Assert.NotEqual(AffinityTypes.Physical, RecruitRoller.RollAffinity(blast, BiomeType.Volcanic, random));
    }

    // -----------------------------------------------------------------
    // The board
    // -----------------------------------------------------------------

    [Fact]
    public void ABoardIsSixRecruits()
    {
        Assert.Equal(TavernRules.BoardSize,
            RecruitRoller.RollBoard(Sheets, Signatures, 1, null, new Random(3)).Count);
    }

    // -----------------------------------------------------------------
    // Cost
    // -----------------------------------------------------------------

    [Fact]
    public void EveryRarityCostsSomething_AndTheRarerOnesCostMore()
    {
        foreach (CharacterRarity rarity in Enum.GetValues<CharacterRarity>())
        {
            var cost = TavernRules.HireCost(rarity);
            Assert.NotEmpty(cost);
            Assert.All(cost, c => Assert.True(c.Quantity > 0));
            Assert.All(cost, c => Assert.False(string.IsNullOrEmpty(c.MaterialName)));
        }

        // A Common is paid for in the material that falls out of everything; the top two need shards,
        // which is what makes them a decision rather than a purchase.
        Assert.Contains(TavernRules.HireCost(CharacterRarity.Common), c => c.MaterialName == "Lesser Essence");
        Assert.Contains(TavernRules.HireCost(CharacterRarity.Epic), c => c.MaterialName == "Rare Shard");
        Assert.Contains(TavernRules.HireCost(CharacterRarity.Legendary), c => c.MaterialName == "Legendary Shard");
    }

    [Fact]
    public void OnlyAFightableSiteBringsARecruit()
    {
        Assert.True(TavernRules.BringsARecruit(LocationType.Dungeon));
        Assert.True(TavernRules.BringsARecruit(LocationType.Portal));

        // Taking a keep takes a region. It must not also buy a night at the inn.
        Assert.False(TavernRules.BringsARecruit(LocationType.Castle));
        Assert.False(TavernRules.BringsARecruit(LocationType.Outpost));
    }

    private static int Weight(IReadOnlyList<(CharacterRarity Rarity, int Weight)> odds, CharacterRarity rarity)
        => odds.Single(o => o.Rarity == rarity).Weight;
}
