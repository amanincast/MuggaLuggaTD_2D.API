using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The Trainer (design 6c): a point a level plus one at 10, 20 and 30, spent on one stat tree, and
/// only the server writes what a character has learnt.
/// </summary>
public class TalentTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private static readonly Guid Realm = Guid.NewGuid();
    private const string Player = "player-1";

    public void Dispose() => _db.Dispose();

    private GoldService Gold => new(_db, new FakeSessionLog(), NullLogger<GoldService>.Instance);
    private TalentService Service => new(_db, Gold, new FakeSessionLog());

    private static Dictionary<string, int> Ranks(params (string id, int rank)[] ranks)
        => ranks.ToDictionary(r => r.id, r => r.rank);

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(9, 9)]
    [InlineData(10, 11)]
    [InlineData(28, 30)]     // the design's own example: LV 28 = 28 + 2 bonus
    [InlineData(30, 33)]
    [InlineData(99, 33)]     // past the cap earns nothing more
    public void PointsAreALevelPlusTheBonuses(long level, int points)
        => Assert.Equal(points, TalentRules.PointsFor(level));

    [Fact]
    public void TheSecondRowOpensAtTenSpentAboveIt()
    {
        var nine = Ranks(("iron_hide", 5), ("keen_edge", 4));
        Assert.NotNull(TalentRules.WhyNot(nine, 20, "shield_wall"));

        var ten = Ranks(("iron_hide", 5), ("keen_edge", 5));
        Assert.Null(TalentRules.WhyNot(ten, 20, "shield_wall"));
    }

    [Fact]
    public void NoRankPastTheMaximum_AndNoPointPastTheLevel()
    {
        Assert.NotNull(TalentRules.WhyNot(Ranks(("quick_feet", 3)), 30, "quick_feet"));
        Assert.NotNull(TalentRules.WhyNot(Ranks(("iron_hide", 2)), 2, "keen_edge"));
        Assert.NotNull(TalentRules.WhyNot(null, 30, "no_such_node"));
    }

    [Fact]
    public void ALegalSetIsOneThatCouldHaveBeenLearnt()
    {
        Assert.True(TalentRules.IsLegal(Ranks(("iron_hide", 5), ("keen_edge", 5), ("shield_wall", 1)), 11));
        Assert.False(TalentRules.IsLegal(Ranks(("shield_wall", 1)), 30), "row two with nothing above it");
        Assert.False(TalentRules.IsLegal(Ranks(("iron_hide", 5), ("keen_edge", 5)), 5), "more points than earned");
        Assert.False(TalentRules.IsLegal(Ranks(("invented", 1)), 30));
    }

    [Fact]
    public void RanksTurnIntoStatFactors()
    {
        var ranks = Ranks(("iron_hide", 5), ("keen_edge", 2), ("shield_wall", 5), ("unbreakable", 1));
        Assert.Equal(1.30f, TalentRules.HealthFactor(ranks), 3);        // 5 x 4% + the capstone's 10%
        Assert.Equal(1.06f, TalentRules.DamageFactor(ranks), 3);        // the design's "rank 2: +6%"
        Assert.Equal(0.80f, TalentRules.DamageTakenFactor(ranks), 3);   // 5 x 3% + the capstone's 5%
        Assert.Equal(1f, TalentRules.MoveSpeedFactor(ranks), 3);
    }

    [Fact]
    public void EachPointIsWorthTwentyFivePower()
    {
        var bare = TestSave.Character("hero-1");
        bare.Level = 12;
        var built = TestSave.Character("hero-2");
        built.Level = 12;
        built.Talents = Ranks(("iron_hide", 5), ("keen_edge", 3));

        float difference = PartyPowerCalculator.CalculateCharacterPower(built, null, null)
                           - PartyPowerCalculator.CalculateCharacterPower(bare, null, null);

        Assert.Equal(8 * TalentRules.PowerPerPoint, difference, 3);
    }

    [Fact]
    public void HoldTheLineCountsOnlyOnAGarrison()
    {
        var hero = TestSave.Character("hero-1");
        hero.Level = 25;
        hero.Talents = Ranks(("iron_hide", 5), ("keen_edge", 5), ("shield_wall", 5), ("signature_focus", 3),
            ("quick_feet", 2), ("hold_the_line", 3));
        var save = TestSave.Roster(hero);

        float marching = PartyPowerCalculator.CalculatePartyPower(save, new[] { "hero-1" }, null);
        float walls = PartyPowerCalculator.CalculatePartyPower(save, new[] { "hero-1" }, null, onAGarrison: true);

        Assert.Equal(marching * 1.15f, walls, 1);
    }

    // -----------------------------------------------------------------
    // Learning and unlearning
    // -----------------------------------------------------------------

    private async Task SeedAsync(long level, Dictionary<string, int>? talents = null)
    {
        var hero = TestSave.Character("hero-1");
        hero.Level = level;
        hero.Talents = talents ?? new Dictionary<string, int>();
        _db.PlayerGameData.Add(new PlayerGameData
        {
            GameInstanceId = Realm,
            UserId = Player,
            GameData = Newtonsoft.Json.JsonConvert.SerializeObject(TestSave.Roster(hero)),
        });
        await _db.SaveChangesAsync();
    }

    private async Task<Dictionary<string, int>> StoredTalentsAsync()
    {
        var row = await _db.PlayerGameData.SingleAsync();
        var character = (JsonObject)JsonNode.Parse(row.GameData)!["Characters"]![0]!;
        return TalentService.ReadTalents(character);
    }

    [Fact]
    public async Task LearningWritesTheRankIntoTheSave()
    {
        await SeedAsync(level: 3);

        var outcome = await Service.LearnAsync(Realm, Player, "hero-1", "keen_edge");

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(1, outcome.Talents!["keen_edge"]);
        Assert.Equal(2, outcome.Unspent);
        Assert.Equal(1, (await StoredTalentsAsync())["keen_edge"]);
    }

    [Fact]
    public async Task ARefusalSaysWhy_AndChangesNothing()
    {
        await SeedAsync(level: 1, Ranks(("iron_hide", 1)));

        var outcome = await Service.LearnAsync(Realm, Player, "hero-1", "iron_hide");

        Assert.Equal(TalentError.Refused, outcome.Error);
        Assert.Contains("No points", outcome.Message);
        Assert.Equal(1, (await StoredTalentsAsync())["iron_hide"]);
    }

    [Fact]
    public async Task ACharacterNotInTheRosterLearnsNothing()
    {
        await SeedAsync(level: 5);
        Assert.Equal(TalentError.NoSuchCharacter, (await Service.LearnAsync(Realm, Player, "stranger", "keen_edge")).Error);
    }

    [Fact]
    public async Task ARespecCostsGold_AndRefundsEveryPoint()
    {
        await SeedAsync(level: 12, Ranks(("iron_hide", 5), ("keen_edge", 5)));
        await Gold.GrantAsync(Realm, Player, 1000, "test");

        var outcome = await Service.RespecAsync(Realm, Player, "hero-1");

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(1000 - 12 * TalentRules.RespecGoldPerLevel, outcome.GoldBalance);
        Assert.Empty(await StoredTalentsAsync());
    }

    [Fact]
    public async Task ARespecThePlayerCannotAfford_KeepsTheTalents()
    {
        await SeedAsync(level: 12, Ranks(("iron_hide", 5)));

        var outcome = await Service.RespecAsync(Realm, Player, "hero-1");

        Assert.Equal(TalentError.InsufficientGold, outcome.Error);
        _db.ChangeTracker.Clear();
        Assert.Equal(5, (await StoredTalentsAsync())["iron_hide"]);
    }

    [Fact]
    public async Task NothingToUnlearn_CostsNothing()
    {
        await SeedAsync(level: 12);
        await Gold.GrantAsync(Realm, Player, 1000, "test");

        Assert.Equal(TalentError.NothingToUnlearn, (await Service.RespecAsync(Realm, Player, "hero-1")).Error);
        Assert.Equal(1000, await Gold.BalanceAsync(Realm, Player));
    }

    // -----------------------------------------------------------------
    // A save cannot teach itself
    // -----------------------------------------------------------------

    private static string StoredWith(Dictionary<string, int> talents)
    {
        var hero = TestSave.Character("hero-1");
        hero.Talents = talents;
        return Newtonsoft.Json.JsonConvert.SerializeObject(TestSave.Roster(hero));
    }

    [Fact]
    public void ASaveThatWritesItsOwnRanks_IsPutBack()
    {
        var incoming = TestSave.AsNode(TestSave.Roster(TestSave.Character("hero-1")));
        incoming["Characters"]![0]!["Talents"] = new JsonObject { ["keen_edge"] = 5, ["unbreakable"] = 1 };

        int corrected = TalentService.ReconcileTalents(incoming, StoredWith(Ranks(("keen_edge", 2))));

        Assert.Equal(1, corrected);
        var ranks = TalentService.ReadTalents((JsonObject)incoming["Characters"]![0]!);
        Assert.Equal(Ranks(("keen_edge", 2)), ranks);
    }

    [Fact]
    public void ASaveThatForgetsItsRanks_KeepsThemAnyway()
    {
        var incoming = TestSave.AsNode(TestSave.Roster(TestSave.Character("hero-1")));
        ((JsonObject)incoming["Characters"]![0]!).Remove("Talents");

        TalentService.ReconcileTalents(incoming, StoredWith(Ranks(("iron_hide", 3))));

        Assert.Equal(3, TalentService.ReadTalents((JsonObject)incoming["Characters"]![0]!)["iron_hide"]);
    }

    [Fact]
    public void ANewCharacterStartsWithNone()
    {
        var incoming = TestSave.AsNode(TestSave.Roster(TestSave.Character("newcomer")));
        incoming["Characters"]![0]!["Talents"] = new JsonObject { ["keen_edge"] = 3 };

        TalentService.ReconcileTalents(incoming, StoredWith(Ranks(("keen_edge", 2))));

        Assert.Empty(TalentService.ReadTalents((JsonObject)incoming["Characters"]![0]!));
    }
}
