using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The cross-world boards (Leaderboards spec, revised 2026-10-10). Pinned:
/// <list type="bullet">
/// <item>who qualifies, the brackets and the months;</item>
/// <item>a season's close writes what the boards read;</item>
/// <item>the rate boards: each player's best, ties sharing a rank, the asker's own row, crowns first all-time;</item>
/// <item>the living boards: kills by the day, rebuilt hourly, heroes and regions by bracket;</item>
/// <item>a result outlives its realm.</item>
/// </list>
/// </summary>
public class LeaderboardTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private readonly FakeHubContext _hub = new();
    private DateTime _now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _db.Dispose();

    private LeaderboardService Boards => new(_db, new FakeGameContent(), NullLogger<LeaderboardService>.Instance) { Clock = () => _now };

    private SeasonScoreService Seasons => new(
        _db,
        new GoldService(_db, _log, NullLogger<GoldService>.Instance),
        new WorldProvisioningService(_db, NullLogger<WorldProvisioningService>.Instance),
        _hub, _log, NullLogger<SeasonScoreService>.Instance);

    private async Task UserAsync(string id, string? display = null)
    {
        if (await _db.Users.AnyAsync(u => u.Id == id)) return;
        _db.Users.Add(new ApplicationUser { Id = id, UserName = id, DisplayName = display });
        await _db.SaveChangesAsync();
    }

    private async Task ResultAsync(string user, double rate, int month = 202610, bool qualifies = true, bool crowned = false,
        int season = 1, string realm = "Realm")
    {
        await UserAsync(user);
        _db.SeasonResults.Add(new SeasonResult
        {
            UserId = user, SeasonNumber = season, Rank = crowned ? 1 : 2, RegionsHeld = 2,
            SeasonStartedAt = _now.AddDays(-31), SeasonEndedAt = _now.AddDays(-1),
            AverageRate = rate, Qualifies = qualifies, Crowned = crowned, EndedMonth = month,
            RealmName = realm, SeasonLengthDays = 30, HoursInSeason = 720,
        });
        await _db.SaveChangesAsync();
    }

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void ASeasonQualifies_OnlyLongEnough_PlayedEnough_AndWithLand()
    {
        Assert.True(LeaderboardRules.Qualifies(7, 48, 1));
        Assert.False(LeaderboardRules.Qualifies(6, 200, 3));
        Assert.False(LeaderboardRules.Qualifies(30, 47.9, 3));
        Assert.False(LeaderboardRules.Qualifies(30, 200, 0));
        Assert.Contains("7 days", LeaderboardRules.WhyNot(6, 200, 3));
    }

    [Fact]
    public void TheBrackets_MatchTheRealmLengths()
    {
        Assert.Equal(SeasonBracket.Short, LeaderboardRules.BracketOf(7));
        Assert.Equal(SeasonBracket.Standard, LeaderboardRules.BracketOf(8));
        Assert.Equal(SeasonBracket.Standard, LeaderboardRules.BracketOf(30));
        Assert.Equal(SeasonBracket.Long, LeaderboardRules.BracketOf(31));
    }

    [Fact]
    public void ASeasonCountsInTheUtcMonthItEnded()
    {
        Assert.Equal(202610, LeaderboardRules.MonthOf(new DateTime(2026, 10, 31, 23, 59, 59, DateTimeKind.Utc)));
        Assert.Equal(202611, LeaderboardRules.MonthOf(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(202512, LeaderboardRules.MonthBefore(202601));
    }

    [Fact]
    public void TheNowRate_WaitsThreeDays_AndCountsFromWhenThePlayerJoined()
    {
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Null(LeaderboardRules.NowRate(500, start, null, start.AddDays(2)));
        Assert.Equal(100.0 / 96, LeaderboardRules.NowRate(100, start, null, start.AddDays(4))!.Value, 6);
        Assert.Equal(100.0 / 48, LeaderboardRules.NowRate(100, start, start.AddDays(2), start.AddDays(4))!.Value, 6);
    }

    // -----------------------------------------------------------------
    // The close
    // -----------------------------------------------------------------

    [Fact]
    public async Task AClose_WritesTheRate_TheRealm_TheName_AndTheCrown()
    {
        var instance = await _db.AddInstanceAsync(TestIds.Player, "Hesper");
        instance.Name = "Ashfall";
        instance.SeasonStartedAt = DateTime.UtcNow.AddDays(-10);
        instance.SeasonLengthDays = 30;
        await _db.SaveChangesAsync();
        await UserAsync(TestIds.Player, "Hesper");
        await UserAsync(TestIds.Rival);
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, "{}");
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Rival, "{}");
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(
            TestWorld.OwnedBy(TestIds.Player, "p", tier: 4), TestWorld.OwnedBy(TestIds.Rival, "r", tier: 1)));

        await Seasons.SettleAllAsync(instance.Id, at: instance.SeasonStartedAt);
        Assert.True(await Seasons.DebugRingBellAsync(instance.Id));

        var won = await _db.SeasonResults.AsNoTracking().SingleAsync(r => r.UserId == TestIds.Player);
        var lost = await _db.SeasonResults.AsNoTracking().SingleAsync(r => r.UserId == TestIds.Rival);
        Assert.Equal("Ashfall", won.RealmName);
        Assert.Equal("Hesper", won.PlayerName);
        // Everyone ranked, the factions too, as the rank counts them.
        Assert.Equal(2 + await _db.FactionSeasonScores.CountAsync(f => f.Rank != null), won.PlayersInSeason);
        Assert.True(won.Crowned);
        Assert.False(lost.Crowned);
        Assert.Equal(SeasonEndRules.AverageRate(won.TotalPoints, won.SeasonStartedAt, won.SeasonEndedAt), won.AverageRate, 1);
        Assert.Equal(LeaderboardRules.MonthOf(won.SeasonEndedAt), won.EndedMonth);
        Assert.True(won.HoursInSeason > 200);
        Assert.True(won.Qualifies);
    }

    /// <summary>The factions race too (season-end.md §2), so a player alone in a realm who out-holds them is crowned, as before.</summary>
    [Fact]
    public async Task AFirstAgainstOnlyTheFactions_IsStillACrown()
    {
        var instance = await _db.AddInstanceAsync(TestIds.Player);
        instance.SeasonStartedAt = DateTime.UtcNow.AddDays(-10);
        await _db.SaveChangesAsync();
        await UserAsync(TestIds.Player);
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, "{}");
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(TestWorld.OwnedBy(TestIds.Player, "p")));
        await Seasons.SettleAllAsync(instance.Id, at: instance.SeasonStartedAt);

        await Seasons.DebugRingBellAsync(instance.Id);

        Assert.True((await _db.SeasonResults.AsNoTracking().SingleAsync()).Crowned);
    }

    // -----------------------------------------------------------------
    // Rate boards
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheMonth_RanksEachPlayersBestQualifiedSeason_TiesSharing()
    {
        await ResultAsync("a", 50);
        await ResultAsync("a", 90, season: 2);
        await ResultAsync("b", 90);
        await ResultAsync("c", 70);
        await ResultAsync("d", 999, qualifies: false);
        await ResultAsync("e", 500, month: 202609);

        var page = await Boards.MonthAsync(202610, "c");

        Assert.Equal(3, page.Rows.Count);
        Assert.Equal(90, page.Rows.Single(r => r.UserId == "a").Value);
        Assert.Equal(1, page.Rows.Single(r => r.UserId == "a").Rank);
        Assert.Equal(1, page.Rows.Single(r => r.UserId == "b").Rank);
        Assert.Equal(3, page.Me!.Rank);
        Assert.Equal(new[] { 202610, 202609 }, page.Months);
    }

    [Fact]
    public async Task TheAsker_OutsideTheTop_IsStillTold_WhereTheyStand()
    {
        for (int i = 0; i < 55; i++) await ResultAsync($"p{i:00}", 200 - i);

        var page = await Boards.MonthAsync(202610, "p54");

        Assert.Equal(LeaderboardRules.Top, page.Rows.Count);
        Assert.Equal(55, page.Me!.Rank);
        Assert.DoesNotContain(page.Rows, r => r.UserId == "p54");
    }

    [Fact]
    public async Task AllTime_PutsCrownsFirst_ThenTheBestRate()
    {
        await ResultAsync("fast", 300);
        await ResultAsync("crowned", 40, crowned: true);
        await ResultAsync("steady", 120);

        var page = await Boards.AllTimeAsync("steady");

        Assert.Equal(new[] { "crowned", "fast", "steady" }, page.Rows.Select(r => r.UserId));
        Assert.Equal(1, page.Rows[0].Crowns);
    }

    [Fact]
    public async Task TheNowBoard_ProjectsRunningSeasons_OnceTheyAreThreeDaysOld()
    {
        var old = await _db.AddInstanceAsync();
        old.SeasonStartedAt = _now.AddDays(-4);
        old.SeasonLengthDays = 30;
        var young = await _db.AddInstanceAsync("owner-2");
        young.SeasonStartedAt = _now.AddDays(-1);
        young.SeasonLengthDays = 30;
        await UserAsync("veteran");
        await UserAsync("fresh");
        _db.SeasonScores.Add(new SeasonScore
            { GameInstanceId = old.Id, UserId = "veteran", SeasonNumber = old.SeasonNumber, SettledPoints = 960, LastSettledAt = _now });
        _db.SeasonScores.Add(new SeasonScore
            { GameInstanceId = young.Id, UserId = "fresh", SeasonNumber = young.SeasonNumber, SettledPoints = 9000, LastSettledAt = _now });
        await _db.SaveChangesAsync();

        var page = await Boards.NowAsync("veteran");

        var row = Assert.Single(page.Rows);
        Assert.Equal("veteran", row.UserId);
        Assert.Equal(10, row.Value, 1);
    }

    // -----------------------------------------------------------------
    // Living boards
    // -----------------------------------------------------------------

    [Fact]
    public async Task Kills_AreCountedByTheDay_AndTheWeekForgetsTheOldOnes()
    {
        var realm = (await _db.AddInstanceAsync()).Id;
        await UserAsync("slayer");
        await UserAsync("old");
        var kills = new Dictionary<string, int> { ["Goblin"] = 7, ["Wolf"] = 3 };
        await LeaderboardService.TallyKillsAsync(_db, NullLogger.Instance, realm, "slayer", kills, _now);
        await LeaderboardService.TallyKillsAsync(_db, NullLogger.Instance, realm, "slayer", kills, _now.AddHours(1));
        await LeaderboardService.TallyKillsAsync(_db, NullLogger.Instance, realm, "old", kills, _now.AddDays(-8));

        Assert.Equal(20, (await _db.SlayerTallies.AsNoTracking().SingleAsync(t => t.UserId == "slayer")).Kills);

        var week = await Boards.LivingAsync(LeaderboardBoard.Slayers, "Week", "slayer");
        var ever = await Boards.LivingAsync(LeaderboardBoard.Slayers, "AllTime", "slayer");

        Assert.Equal("slayer", Assert.Single(week.Rows).UserId);
        Assert.Equal(2, ever.Rows.Count);
    }

    [Fact]
    public async Task Kills_AlsoCountOnTheSeasonsScore()
    {
        var instance = await _db.AddInstanceAsync();
        await UserAsync("slayer");
        _db.SeasonScores.Add(new SeasonScore { GameInstanceId = instance.Id, UserId = "slayer", SeasonNumber = instance.SeasonNumber });
        await _db.SaveChangesAsync();

        await LeaderboardService.TallyKillsAsync(_db, NullLogger.Instance, instance.Id, "slayer",
            new Dictionary<string, int> { ["Goblin"] = 4 }, _now);

        Assert.Equal(4, (await _db.SeasonScores.AsNoTracking().SingleAsync()).Kills);
    }

    [Fact]
    public async Task ALivingBoard_IsKeptForAnHour_ThenRebuilt()
    {
        var realm = (await _db.AddInstanceAsync()).Id;
        await UserAsync("slayer");
        await LeaderboardService.TallyKillsAsync(_db, NullLogger.Instance, realm, "slayer", new Dictionary<string, int> { ["Goblin"] = 5 }, _now);
        Assert.Equal(5, (await Boards.LivingAsync(LeaderboardBoard.Slayers, "Week", "slayer")).Rows[0].Value);

        await LeaderboardService.TallyKillsAsync(_db, NullLogger.Instance, realm, "slayer", new Dictionary<string, int> { ["Goblin"] = 5 }, _now);
        _now = _now.AddMinutes(30);
        Assert.Equal(5, (await Boards.LivingAsync(LeaderboardBoard.Slayers, "Week", "slayer")).Rows[0].Value);

        _now = _now.AddMinutes(31);
        Assert.Equal(10, (await Boards.LivingAsync(LeaderboardBoard.Slayers, "Week", "slayer")).Rows[0].Value);
    }

    [Fact]
    public async Task Conquerors_CountRegionsHeld_InTheirBracketOnly()
    {
        var standard = await _db.AddInstanceAsync();
        standard.SeasonLengthDays = 30;
        var shortRealm = await _db.AddInstanceAsync("owner-2");
        shortRealm.SeasonLengthDays = 7;
        await _db.SaveChangesAsync();
        await UserAsync(TestIds.Player);
        await UserAsync(TestIds.Rival);
        await _db.AddWorldAsync(standard.Id, TestWorld.Blob(
            TestWorld.OwnedBy(TestIds.Player, "a"), TestWorld.OwnedBy(TestIds.Player, "b"), TestWorld.OwnedBy(TestIds.Rival, "c")));
        await _db.AddWorldAsync(shortRealm.Id, TestWorld.Blob(
            TestWorld.OwnedBy(TestIds.Rival, "x"), TestWorld.OwnedBy(TestIds.Rival, "y"), TestWorld.OwnedBy(TestIds.Rival, "z")));

        var page = await Boards.LivingAsync(LeaderboardBoard.Conquerors, "Standard", TestIds.Player);

        Assert.Equal(TestIds.Player, page.Rows[0].UserId);
        Assert.Equal(2, page.Rows[0].Value);
        Assert.Equal(1, page.Rows.Single(r => r.UserId == TestIds.Rival).Value);
    }

    [Fact]
    public async Task Heroes_AreRankedOneByOne_ByTheServersPower()
    {
        var realm = await _db.AddInstanceAsync();
        realm.SeasonLengthDays = 30;
        await _db.SaveChangesAsync();
        await UserAsync(TestIds.Player);
        await _db.AddPlayerSaveAsync(realm.Id, TestIds.Player,
            "{\"Characters\":[{\"Id\":\"h1\",\"TypeName\":\"Warrior\",\"Level\":9},{\"Id\":\"h2\",\"TypeName\":\"Mage\",\"Level\":3}]}");

        var page = await Boards.LivingAsync(LeaderboardBoard.Heroes, "Standard", TestIds.Player);

        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(9, page.Rows[0].Level);
        Assert.Equal("Warrior", page.Rows[0].Detail);
        Assert.False(string.IsNullOrEmpty(page.Rows[0].Subject));
        Assert.True(page.Rows[0].Value > page.Rows[1].Value);
    }

    // -----------------------------------------------------------------
    // History
    // -----------------------------------------------------------------

    [Fact]
    public async Task AResult_OutlivesItsRealm()
    {
        var realm = await _db.AddInstanceAsync();
        await UserAsync(TestIds.Player);
        _db.SeasonResults.Add(new SeasonResult
            { GameInstanceId = realm.Id, UserId = TestIds.Player, SeasonNumber = 1, Rank = 1, Crowned = true, RealmName = "Gone" });
        await _db.SaveChangesAsync();

        _db.GameInstances.Remove(realm);
        await _db.SaveChangesAsync();

        var result = await _db.SeasonResults.AsNoTracking().SingleAsync();
        Assert.Null(result.GameInstanceId);
        Assert.Equal("Gone", result.RealmName);
        Assert.Equal(1, (await Boards.AllTimeAsync(TestIds.Player)).Me!.Crowns);
    }
}
