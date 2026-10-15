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
/// The Weekly Trial (Active Content E): one fight a week, the same everywhere; three tries a day; a try held to what could
/// have happened; the best kept; a chest by finish paid once into the realm the player comes back to.
/// </summary>
public class TrialTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private readonly FakeGameContent _content = new()
    {
        DroppableItems = new[] { new ItemTemplate { ItemName = "Cinder Blade", ItemType = ItemTypes.Weapon } }
    };

    /// <summary>A Wednesday afternoon, well inside a week.</summary>
    private DateTime _now = new(2026, 10, 14, 15, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _db.Dispose();

    private ItemLedgerService Items => new(_db, _log, NullLogger<ItemLedgerService>.Instance);
    private TrialService Trial => new(_db, _content, Items, _log, NullLogger<TrialService>.Instance) { Clock = () => _now };
    private LeaderboardService Boards => new(_db, _content, NullLogger<LeaderboardService>.Instance) { Clock = () => _now };

    private async Task<Guid> RealmAsync()
    {
        var instance = await _db.AddInstanceAsync();
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, TestSave.ToJson(TestSave.Roster(TestSave.Character("hero"))));
        await _db.SaveChangesAsync();
        return instance.Id;
    }

    private async Task UsersAsync(params string[] ids)
    {
        foreach (var id in ids)
            _db.Users.Add(new ApplicationUser { Id = id, UserName = id, DisplayName = id });
        await _db.SaveChangesAsync();
    }

    /// <summary>Begins and finishes a try, <paramref name="seconds"/> into the fight.</summary>
    private async Task<TrialFinishResponse> TryAsync(string user, int kills, int waves, int seconds, int hero = 0)
    {
        var begun = await Trial.BeginAsync(user, hero, null);
        _now = _now.AddSeconds(seconds);
        return await Trial.FinishAsync(user, new TrialFinishRequest(begun.AttemptId, kills, waves, seconds));
    }

    // -----------------------------------------------------------------
    // The week
    // -----------------------------------------------------------------

    [Fact]
    public void AWeek_RunsMondayToMonday_UTC()
    {
        int week = TrialRules.WeekOf(_now);
        Assert.Equal(DayOfWeek.Monday, TrialRules.StartOf(week).DayOfWeek);
        Assert.Equal(new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), TrialRules.StartOf(week));
        Assert.Equal(week + 1, TrialRules.WeekOf(TrialRules.EndOf(week)));
        Assert.Equal(week, TrialRules.WeekOf(TrialRules.EndOf(week).AddTicks(-1)));
    }

    [Fact]
    public void AWeek_IsTheSameForEveryone_AndWeeksDiffer()
    {
        int week = TrialRules.WeekOf(_now);
        Assert.Equal(TrialRules.Biome(week), TrialRules.Biome(week));
        Assert.Equal(TrialRules.ArenaSeed(week), TrialRules.ArenaSeed(week));
        Assert.Equal(TrialRules.Mutators(week), TrialRules.Mutators(week));
        Assert.Equal(TrialRules.MutatorCount, TrialRules.Mutators(week).Distinct().Count());

        // Over a year of weeks, the land and the twists move about.
        var weeks = Enumerable.Range(week, 52).ToList();
        Assert.True(weeks.Select(TrialRules.Biome).Distinct().Count() >= 5);
        Assert.True(weeks.Select(w => string.Join(",", TrialRules.Mutators(w))).Distinct().Count() >= 10);
        Assert.Equal(52, weeks.Select(TrialRules.ArenaSeed).Distinct().Count());
    }

    [Fact]
    public void TheHeroes_AreOneOfEachClass_WithAnAllowedAffinity()
    {
        var signatures = new List<SignatureDefinition>
        {
            new() { SignatureId = "cleave", Class = "Warrior" },
            new() { SignatureId = "slam", Class = "Warrior" },
            new() { SignatureId = "snipe", Class = "Archer" },
            new() { SignatureId = "bolt", Class = "Mage", AllowedAffinities = { AffinityTypes.Fire, AffinityTypes.Water } },
            new() { SignatureId = "mend", Class = "Cleric" },
        };
        int week = TrialRules.WeekOf(_now);
        var heroes = TrialRules.Heroes(week, signatures);

        Assert.Equal(TrialRules.Classes, heroes.Select(h => h.Class).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, heroes.Select(h => h.Index).ToArray());
        Assert.Contains(heroes[2].Affinity, new[] { AffinityTypes.Fire, AffinityTypes.Water });
        // The same week gives the same heroes, whatever order the content lists them in.
        var again = TrialRules.Heroes(week, Enumerable.Reverse(signatures));
        Assert.Equal(heroes.Select(h => h.SignatureId + h.Affinity), again.Select(h => h.SignatureId + h.Affinity));
    }

    // -----------------------------------------------------------------
    // The score and its check
    // -----------------------------------------------------------------

    [Fact]
    public void TheScore_IsKillsTimesSeconds()
    {
        Assert.Equal(300L * 240, TrialRules.Score(300, 240));
        Assert.Equal(0, TrialRules.Score(-5, 100));
    }

    [Fact]
    public void ABelievableTry_IsKeptWhole()
    {
        var check = TrialRules.Check(_content.RunTuning, kills: 120, waves: 6, seconds: 200, serverSeconds: 205);
        Assert.False(check.Trimmed);
        Assert.Equal(120L * 200, check.Score);
    }

    [Fact]
    public void AnImpossibleTry_IsHeldToWhatCouldHaveHappened()
    {
        // More seconds than passed on the server's clock.
        var slow = TrialRules.Check(_content.RunTuning, kills: 50, waves: 3, seconds: 900, serverSeconds: 60);
        Assert.True(slow.Trimmed);
        Assert.Equal(60 + TrialRules.ClockSlackSeconds, slow.Seconds);

        // More kills than the waves held.
        var greedy = TrialRules.Check(_content.RunTuning, kills: 100_000, waves: 2, seconds: 60, serverSeconds: 60);
        Assert.True(greedy.Trimmed);
        Assert.Equal(TrialRules.KillBudget(_content.RunTuning, 2), greedy.Kills);

        // More waves than the time allows.
        var hasty = TrialRules.Check(_content.RunTuning, kills: 10, waves: 50, seconds: 30, serverSeconds: 30);
        Assert.True(hasty.Trimmed);
        Assert.Equal(TrialRules.MostWaves(_content.RunTuning, 30), hasty.Waves);
    }

    [Theory]
    [InlineData(1, 1, ItemRarityTypes.Legendary)]
    [InlineData(1, 500, ItemRarityTypes.Legendary)]
    [InlineData(5, 500, ItemRarityTypes.Legendary)]
    [InlineData(6, 500, ItemRarityTypes.Rare)]
    [InlineData(50, 500, ItemRarityTypes.Rare)]
    [InlineData(51, 500, ItemRarityTypes.Magic)]
    [InlineData(250, 500, ItemRarityTypes.Magic)]
    [InlineData(251, 500, ItemRarityTypes.Uncommon)]
    [InlineData(2, 3, ItemRarityTypes.Magic)]
    [InlineData(3, 3, ItemRarityTypes.Uncommon)]
    public void TheChest_GoesByTheShareAhead(int rank, int total, ItemRarityTypes chest) =>
        Assert.Equal(chest, TrialRules.ChestFor(rank, total));

    // -----------------------------------------------------------------
    // Tries
    // -----------------------------------------------------------------

    [Fact]
    public async Task ThreeTriesADay_ThenMidnight()
    {
        for (int i = 0; i < TrialRules.AttemptsPerDay; i++)
            await Trial.BeginAsync(TestIds.Player, 0, null);
        await Assert.ThrowsAsync<TrialRefusal>(() => Trial.BeginAsync(TestIds.Player, 0, null));

        var view = await Trial.ViewAsync(TestIds.Player, null);
        Assert.Equal(0, view.TriesLeft);
        Assert.Equal(new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc), view.NextTryAt);

        _now = new DateTime(2026, 10, 15, 0, 1, 0, DateTimeKind.Utc);
        Assert.Equal(TrialRules.AttemptsPerDay, (await Trial.ViewAsync(TestIds.Player, null)).TriesLeft);
        await Trial.BeginAsync(TestIds.Player, 1, null);
    }

    [Fact]
    public async Task TheBest_IsKept_AndAWorseTryLeavesIt()
    {
        await UsersAsync(TestIds.Player);
        var first = await TryAsync(TestIds.Player, kills: 100, waves: 5, seconds: 150, hero: 2);
        Assert.True(first.IsBest);
        Assert.Equal(15_000, first.Score);

        var worse = await TryAsync(TestIds.Player, kills: 20, waves: 2, seconds: 50);
        Assert.False(worse.IsBest);
        Assert.Equal(15_000, worse.Best!.Score);
        Assert.Equal(2, worse.Best.Hero);

        var view = await Trial.ViewAsync(TestIds.Player, null);
        Assert.Equal(15_000, view.Best!.Score);
        Assert.Equal(1, view.Best.Rank);
        Assert.Single(view.Top);
        Assert.True(view.Top[0].Mine);
    }

    [Fact]
    public async Task ATry_IsScoredOnce_AndOnlyByItsPlayer()
    {
        var begun = await Trial.BeginAsync(TestIds.Player, 0, null);
        _now = _now.AddSeconds(60);
        await Assert.ThrowsAsync<TrialRefusal>(() => Trial.FinishAsync(TestIds.Rival, new TrialFinishRequest(begun.AttemptId, 10, 1, 60)));
        await Trial.FinishAsync(TestIds.Player, new TrialFinishRequest(begun.AttemptId, 10, 1, 60));
        await Assert.ThrowsAsync<TrialRefusal>(() => Trial.FinishAsync(TestIds.Player, new TrialFinishRequest(begun.AttemptId, 10, 1, 60)));
    }

    [Fact]
    public async Task AReportedTimeLongerThanTheServerSaw_IsTrimmed_AndLogged()
    {
        var begun = await Trial.BeginAsync(TestIds.Player, 0, null);
        _now = _now.AddSeconds(40);
        var done = await Trial.FinishAsync(TestIds.Player, new TrialFinishRequest(begun.AttemptId, 30, 2, 3600));
        Assert.True(done.Trimmed);
        Assert.Equal(40 + TrialRules.ClockSlackSeconds, done.Seconds);
        var attempt = await _db.TrialAttempts.SingleAsync();
        Assert.True(attempt.Trimmed);
        Assert.Equal("30k 2w 3600s", attempt.Reported);
    }

    // -----------------------------------------------------------------
    // The week's end
    // -----------------------------------------------------------------

    [Fact]
    public async Task AFinishedWeek_PaysItsChestOnce_IntoTheRealmCameBackTo()
    {
        var realm = await RealmAsync();
        await UsersAsync(TestIds.Player, TestIds.Rival, "third");
        await TryAsync(TestIds.Player, kills: 200, waves: 6, seconds: 200);
        await TryAsync(TestIds.Rival, kills: 300, waves: 7, seconds: 250);
        await TryAsync("third", kills: 10, waves: 1, seconds: 30);

        // Within the week nothing is paid.
        Assert.Empty((await Trial.ViewAsync(TestIds.Player, realm)).Paid);

        _now = TrialRules.EndOf(TrialRules.WeekOf(_now)).AddHours(2);
        var view = await Trial.ViewAsync(TestIds.Player, realm);
        var paid = Assert.Single(view.Paid);
        Assert.Equal(2, paid.Rank);
        Assert.Equal(3, paid.Total);
        Assert.Equal(ItemRarityTypes.Magic, paid.Chest);
        Assert.Equal(realm, paid.PaidInstanceId);
        Assert.NotEmpty(await _db.ItemGrants.Where(g => g.GameInstanceId == realm && g.UserId == TestIds.Player).ToListAsync());
        Assert.Equal(2, view.LastWeek!.Rank);

        // Asking again pays nothing more.
        int grants = await _db.ItemGrants.CountAsync();
        Assert.Empty((await Trial.ViewAsync(TestIds.Player, realm)).Paid);
        Assert.Equal(grants, await _db.ItemGrants.CountAsync());
    }

    [Fact]
    public async Task WithNoRealm_NothingIsPaid_AndTheChestWaits()
    {
        var realm = await RealmAsync();
        await TryAsync(TestIds.Player, kills: 50, waves: 3, seconds: 100);
        _now = _now.AddDays(7);

        Assert.Empty((await Trial.ViewAsync(TestIds.Player, null)).Paid);
        Assert.Single((await Trial.ViewAsync(TestIds.Player, realm)).Paid);
    }

    [Fact]
    public async Task TheBoard_RanksEachPlayersBest_AcrossRealms()
    {
        await UsersAsync(TestIds.Player, TestIds.Rival);
        await TryAsync(TestIds.Player, kills: 100, waves: 5, seconds: 100, hero: 3);
        await TryAsync(TestIds.Rival, kills: 200, waves: 6, seconds: 200, hero: 1);
        await TryAsync(TestIds.Player, kills: 10, waves: 1, seconds: 20);

        var page = await Boards.TrialAsync(TrialRules.WeekOf(_now), TestIds.Player);
        Assert.Equal(2, page.Total);
        Assert.Equal(TestIds.Rival, page.Rows[0].UserId);
        Assert.Equal(40_000, page.Rows[0].Value);
        Assert.Equal(1, page.Rows[0].Level);
        Assert.Equal(2, page.Me!.Rank);
        Assert.Equal("100:5:100", page.Me.Detail);
    }
}
