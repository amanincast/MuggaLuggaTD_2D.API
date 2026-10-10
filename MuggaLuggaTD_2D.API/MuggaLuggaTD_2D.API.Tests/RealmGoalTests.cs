using Enums;
using Items.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Realm goals (Active Content B, daily). What a player should be able to rely on: the whole realm's
/// deeds count toward one goal; reaching it pays everyone who did at least 2%, once; a bigger share
/// earns a better chest; and nobody is paid for a goal they did not help with.
/// </summary>
public class RealmGoalTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private readonly FakeHubContext _hub = new();
    private readonly DateTime _now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
    private Guid _realm;

    private readonly FakeGameContent _content = new()
    {
        DroppableItems = new[] { new ItemTemplate { ItemName = "Cinder Blade", ItemType = ItemTypes.Weapon } }
    };

    public void Dispose() => _db.Dispose();

    private ItemLedgerService Ledger => new(_db, _log, NullLogger<ItemLedgerService>.Instance);

    private RealmGoalService Goals()
    {
        var warLog = new WarLogService(_db, _hub, NullLogger<WarLogService>.Instance, new FakeClock(_now));
        return new RealmGoalService(_db, _content, Ledger, _log, NullLogger<RealmGoalService>.Instance, warLog) { Clock = () => _now };
    }

    private async Task<RealmGoal> RealmWithGoalAsync(RealmGoalKind kind, int target)
    {
        var realm = await _db.AddInstanceAsync();
        _realm = realm.Id;
        await _db.AddWorldAsync(_realm, TestWorld.Blob(TestWorld.Region()));

        // Today's goal, set by hand so the test controls what it asks.
        var goal = new RealmGoal
        {
            GameInstanceId = _realm, Day = RealmGoalRules.DayOf(_now), Kind = kind,
            Subject = kind == RealmGoalKind.Slay ? "Goblin" : null, Target = target
        };
        _db.RealmGoals.Add(goal);
        await _db.SaveChangesAsync();
        return goal;
    }

    private static QuestDeed Slew(int goblins) => new() { Kills = new Dictionary<string, int> { ["Goblin"] = goblins } };

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void TheDaysGoal_IsTheSameWhoeverAsks_AndChangesDayToDay()
    {
        var peoples = new[] { "Goblin", "Wolf", "Troll" };
        var a = RealmGoalRules.For("realm-1", 20261010, peoples, 3);
        var b = RealmGoalRules.For("realm-1", 20261010, peoples.Reverse().ToArray(), 3);
        Assert.Equal((a.Kind, a.Subject, a.Target), (b.Kind, b.Subject, b.Target));

        var week = Enumerable.Range(10, 14).Select(d => RealmGoalRules.For("realm-1", 20261000 + d, peoples, 3)).ToList();
        Assert.True(week.Select(g => (g.Kind, g.Subject)).Distinct().Count() > 1);
    }

    [Fact]
    public void ARealmWithNoPeoples_IsNeverAskedToSlay()
    {
        for (int day = 1; day <= 28; day++)
            Assert.NotEqual(RealmGoalKind.Slay, RealmGoalRules.For("realm-1", 20261100 + day, Array.Empty<string>(), 4).Kind);
    }

    [Fact]
    public void TheTargetGrowsWithThePlayers_AndAsksForTwoAtLeast()
    {
        for (int day = 1; day <= 28; day++)
        {
            var solo = RealmGoalRules.For("r", 20261100 + day, new[] { "Goblin" }, 1);
            var two = RealmGoalRules.For("r", 20261100 + day, new[] { "Goblin" }, 2);
            var ten = RealmGoalRules.For("r", 20261100 + day, new[] { "Goblin" }, 10);
            Assert.Equal(solo.Target, two.Target);
            Assert.Equal(two.Target * 5, ten.Target);
        }
    }

    [Theory]
    [InlineData(1, 100, null)]
    [InlineData(2, 100, ItemRarityTypes.Magic)]
    [InlineData(9, 100, ItemRarityTypes.Magic)]
    [InlineData(10, 100, ItemRarityTypes.Rare)]
    public void AChest_IsEarnedByShare(int done, int target, ItemRarityTypes? chest) =>
        Assert.Equal(chest, RealmGoalRules.ChestFor(done, target));

    // -----------------------------------------------------------------
    // The service
    // -----------------------------------------------------------------

    [Fact]
    public async Task EveryonesDeeds_AddUp()
    {
        var goal = await RealmWithGoalAsync(RealmGoalKind.Slay, 100);

        await Goals().RecordAsync(_realm, TestIds.Player, Slew(20));
        await Goals().RecordAsync(_realm, TestIds.Rival, Slew(15));
        await Goals().RecordAsync(_realm, TestIds.Player, new QuestDeed { Kills = new() { ["Wolf"] = 50 } });

        var stored = await _db.RealmGoals.AsNoTracking().SingleAsync();
        Assert.Equal(35, stored.Count);
        var view = await Goals().ViewAsync(_realm, TestIds.Player);
        Assert.Equal(20, view!.MyCount);
        Assert.Equal(ItemRarityTypes.Rare, view.MyChest);
    }

    [Fact]
    public async Task ReachingTheGoal_PaysEveryShareOfTwoPercent_Once()
    {
        await RealmWithGoalAsync(RealmGoalKind.Slay, 100);

        await Goals().RecordAsync(_realm, TestIds.Rival, Slew(1));     // 1%: not paid
        await Goals().RecordAsync(_realm, TestIds.Owner, Slew(5));     // 5%: Magic
        await Goals().RecordAsync(_realm, TestIds.Player, Slew(94));   // reaches it: Rare
        await Goals().RecordAsync(_realm, TestIds.Player, Slew(30));   // past it: no second payout

        Assert.NotEmpty(await Ledger.HeldAsync(_realm, TestIds.Owner));
        Assert.NotEmpty(await Ledger.HeldAsync(_realm, TestIds.Player));
        Assert.Empty(await Ledger.HeldAsync(_realm, TestIds.Rival));

        var shares = await _db.RealmGoalShares.AsNoTracking().ToDictionaryAsync(s => s.UserId);
        Assert.Equal(ItemRarityTypes.Magic, shares[TestIds.Owner].ChestRarity);
        Assert.Equal(ItemRarityTypes.Rare, shares[TestIds.Player].ChestRarity);
        Assert.Null(shares[TestIds.Rival].ChestRarity);
        Assert.Single(_log.Of("REALM-GOAL"));
    }

    [Fact]
    public async Task EachQuarter_IsAnnouncedOnce_AndTheGoalAtTheEnd()
    {
        await RealmWithGoalAsync(RealmGoalKind.Clear, 4);

        for (int i = 0; i < 6; i++)
            await Goals().RecordAsync(_realm, TestIds.Player, new QuestDeed { ClearedSiteId = $"site-{i}" });

        var lines = await _db.WarLog.AsNoTracking().OrderBy(w => w.RecordedTicks).Select(w => w.Kind).ToListAsync();
        Assert.Equal(new[] { "RealmGoalProgress", "RealmGoalProgress", "RealmGoalProgress", "RealmGoalReached" }, lines);
    }

    [Fact]
    public async Task ADeedThatDoesNotServeTheGoal_CountsForNothing()
    {
        await RealmWithGoalAsync(RealmGoalKind.Ambush, 10);

        await Goals().RecordAsync(_realm, TestIds.Player, new QuestDeed { ClearedSiteId = "site-1" });

        Assert.Equal(0, (await _db.RealmGoals.AsNoTracking().SingleAsync()).Count);
        Assert.False(await _db.RealmGoalShares.AnyAsync());
    }

    [Fact]
    public async Task TodaysGoal_IsMadeWhenFirstAsked_FromTheRealmsOwnLand()
    {
        var realm = await _db.AddInstanceAsync();
        await _db.AddWorldAsync(realm.Id, TestWorld.Blob(TestWorld.Region()));

        var goal = await Goals().TodayAsync(realm.Id);
        var again = await Goals().TodayAsync(realm.Id);

        Assert.NotNull(goal);
        Assert.Equal(goal!.Id, again!.Id);
        Assert.True(goal.Target > 0);
        if (goal.Kind == RealmGoalKind.Slay) Assert.Contains(goal.Subject, new[] { "Goblin", "Drakan", "Wolf", "Troll" });
    }

    [Fact]
    public async Task TheQuestHook_FeedsTheGoal()
    {
        await RealmWithGoalAsync(RealmGoalKind.Slay, 100);
        var gold = new GoldService(_db, _log, NullLogger<GoldService>.Instance);
        var wallet = new MaterialWalletService(_db, _log, NullLogger<MaterialWalletService>.Instance);
        var quests = new QuestService(_db, _content, Ledger, gold, wallet, _log, goals: Goals());

        await quests.RecordAsync(_realm, TestIds.Player, Slew(12));

        Assert.Equal(12, (await _db.RealmGoals.AsNoTracking().SingleAsync()).Count);
    }
}
