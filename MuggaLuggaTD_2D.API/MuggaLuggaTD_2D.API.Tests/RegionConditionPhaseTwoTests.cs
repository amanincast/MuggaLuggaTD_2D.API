using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Fairs and storms, phase 2 (Active Content C): Storm, Plague of Rats and Pilgrimage, and the fair's Gather gold.
/// What a player should be able to rely on: each new condition turns up now and then, one of each at most; a storm
/// slows only the walk through its own region; an ambush beaten in one counts twice; a plague pays more only in caves
/// and ruins; a pilgrimage brings out both of its region's givers; and a fair's Gather quests pay half as much again.
/// </summary>
public class RegionConditionPhaseTwoTests
{
    private static readonly string[] Held = Enumerable.Range(0, 30).Select(i => $"r{i}").ToArray();

    private static readonly RegionCondition[] PhaseTwo =
        { RegionCondition.Storm, RegionCondition.PlagueOfRats, RegionCondition.Pilgrimage };

    [Fact]
    public void EachNewCondition_TurnsUpSomeHours_AtMostOnceInTheRealm()
    {
        var seen = new HashSet<RegionCondition>();
        for (long hour = 500_000; hour < 502_000; hour++)
        {
            var conditions = RegionConditionRules.For("realm-1", hour, Held);
            foreach (var condition in PhaseTwo)
                Assert.True(conditions.Count(c => c.Value == condition) <= 1);
            seen.UnionWith(conditions.Values);
        }
        Assert.All(PhaseTwo, c => Assert.Contains(c, seen));
    }

    [Fact]
    public void TheNewConditions_NeverTakeAFairOrAHuntsPlace()
    {
        // The fairs and the hunt are picked first, from their own rolls, so a region with one keeps it.
        for (long hour = 500_000; hour < 501_000; hour++)
        {
            var conditions = RegionConditionRules.For("realm-1", hour, Held);
            var older = conditions.Where(c => c.Value is RegionCondition.HarvestFair or RegionCondition.HuntingSeason).ToList();
            Assert.True(older.Count(c => c.Value == RegionCondition.HarvestFair) <= RegionConditionRules.MostFairs);
            foreach (var (id, condition) in older)
                Assert.Equal(condition, RegionConditionRules.For("realm-1", hour, Held)[id]);
        }
    }

    [Fact]
    public void AStorm_SlowsTheWalkThroughItsOwnRegion_AndNoOther()
    {
        var home = TestWorld.Region("r0");
        var next = TestWorld.Region("r1", q: 1, r: 0, biome: BiomeType.Forest);
        var byHex = new[] { home, next }.ToDictionary(r => r.Hex);
        WorldRegionData? At(HexCoord hex) => byHex.TryGetValue(hex, out var r) ? r : null;
        var from = RegionGenerator.Generate(home).Sites.First().Cell;
        var to = RegionGenerator.Generate(next).Sites.First().Cell;

        var fair = TravelRules.PlanRoute(At!, home, from, next, to);
        var stormy = TravelRules.PlanRoute(At!, home, from, next, to,
            RegionConditionRules.TravelFactors(new Dictionary<string, RegionCondition> { ["r1"] = RegionCondition.Storm }));
        Assert.NotNull(fair);
        Assert.NotNull(stormy);

        // The walk out of home is untouched; the walk through the storm takes half as long again.
        Assert.Equal(fair!.Legs[0].Seconds, stormy!.Legs[0].Seconds);
        double fairWalk = fair.Legs[1].Seconds[^1] - fair.Legs[1].Seconds[0];
        double stormWalk = stormy.Legs[1].Seconds[^1] - stormy.Legs[1].Seconds[0];
        Assert.Equal(fairWalk * RegionConditionRules.StormTravelFactor, stormWalk, 1);
        Assert.True(stormy.Duration > fair.Duration);
    }

    [Fact]
    public void NoStormAnywhere_MeansNoWeatherAtAll()
    {
        Assert.Null(RegionConditionRules.TravelFactors(new Dictionary<string, RegionCondition> { ["r1"] = RegionCondition.HarvestFair }));
        Assert.Null(RegionConditionRules.TravelFactors(null!));
    }

    [Fact]
    public void AnAmbushBeatenInAStorm_CountsTwiceForAnAmbushQuest_NeverPastTheCount()
    {
        var quest = new QuestOffer { Kind = QuestKind.Ambush, Count = 3 };
        var storm = new QuestDeed { AmbushWon = true, AmbushCount = RegionConditionRules.AmbushQuestCount(RegionCondition.Storm) };
        var calm = new QuestDeed { AmbushWon = true, AmbushCount = RegionConditionRules.AmbushQuestCount(RegionCondition.None) };

        Assert.Equal(2, QuestRules.Advance(quest, 0, storm));
        Assert.Equal(1, QuestRules.Advance(quest, 0, calm));
        Assert.Equal(3, QuestRules.Advance(quest, 2, storm));
        Assert.Equal(1, QuestRules.Advance(quest, 0, new QuestDeed { AmbushWon = true, AmbushCount = 0 }));
    }

    [Theory]
    [InlineData(LocationType.Dungeon, 1.25)]
    [InlineData(LocationType.Ruin, 1.25)]
    [InlineData(LocationType.Camp, 1.0)]
    [InlineData(LocationType.Castle, 1.0)]
    public void APlague_FillsAndPaysOnlyCavesAndRuins(LocationType site, double factor)
    {
        Assert.Equal(factor, RegionConditionRules.ClearPayFactor(RegionCondition.PlagueOfRats, site));
        Assert.Equal(factor, RegionConditionRules.EnemyFactor(RegionCondition.PlagueOfRats, site));
        Assert.Equal(1.0, RegionConditionRules.ClearPayFactor(RegionCondition.Storm, site));
    }

    // -----------------------------------------------------------------
    // The quest board
    // -----------------------------------------------------------------

    private static IReadOnlyDictionary<BiomeType, IReadOnlyList<string>> Peoples => new FakeGameContent().EnemyPeoples;

    private static WorldRegionData[] World() => new[]
    {
        TestWorld.OwnedBy(TestIds.Player, "r0"),
        TestWorld.Region("r1", q: 1, r: 0, biome: BiomeType.Forest),
    };

    private static List<QuestOffer> Board(long hour, Dictionary<string, RegionCondition>? conditions = null) =>
        QuestRules.Board("realm", TestIds.Player, hour, 0, World(), Peoples, new RunTuning(), conditions);

    [Fact]
    public void APilgrimage_BringsOutBothOfItsRegionsGivers()
    {
        for (long hour = 0; hour < 50; hour++)
        {
            var pilgrims = Board(hour, new Dictionary<string, RegionCondition> { ["r1"] = RegionCondition.Pilgrimage });
            var givers = pilgrims.Where(o => QuestRules.IsNpcGiver(o.GiverId)).Select(o => o.GiverId).ToList();
            Assert.Contains(QuestRules.NpcGiverId("r1", 0), givers);
            Assert.Contains(QuestRules.NpcGiverId("r1", 1), givers);

            // Nobody else's giver is turned away for them.
            var ordinary = Board(hour).Where(o => QuestRules.IsNpcGiver(o.GiverId)).Select(o => o.GiverId);
            Assert.All(ordinary, id => Assert.Contains(id, givers));
        }
    }

    [Fact]
    public void AFairsGatherQuests_PayHalfAsMuchGoldAgain_AndNothingElseChanges()
    {
        int checkedGathers = 0;
        for (long hour = 0; hour < 200; hour++)
        {
            var calm = Board(hour);
            var fair = Board(hour, new Dictionary<string, RegionCondition> { ["r0"] = RegionCondition.HarvestFair });
            Assert.Equal(calm.Select(o => o.Id), fair.Select(o => o.Id));
            foreach (var (before, after) in calm.Zip(fair))
            {
                bool paysMore = before.Kind == QuestKind.Gather && before.RegionId == "r0";
                long expected = paysMore ? (long)Math.Round(before.Gold * RegionConditionRules.FairGatherGoldFactor) : before.Gold;
                Assert.Equal(expected, after.Gold);
                if (paysMore) checkedGathers++;
            }
        }
        Assert.True(checkedGathers > 0, "no Gather quest in the fair's region turned up to check");
    }
}
