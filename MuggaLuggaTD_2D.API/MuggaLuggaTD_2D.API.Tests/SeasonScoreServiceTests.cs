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
/// The win condition: how a season is scored, and what happens when its time is up.
///
/// <para>This is the only code in the project that decides who <i>won</i>, so the properties worth
/// pinning are the ones the design argues for rather than the arithmetic. A player who loses their
/// territory keeps what it already earned and can still climb; nothing is earned after the bell;
/// the standings are the same whoever looks and whenever they look; and a realm that ends begins
/// again with the result written down.</para>
///
/// <para>Time is passed in rather than waited for. Accrual is deliberately a function of two
/// timestamps and a rate precisely so it can be asked about any moment, which is what makes a
/// month-long season testable in milliseconds.</para>
/// </summary>
public class SeasonScoreServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeHubContext _hub = new();
    private readonly FakeSessionLog _log = new();

    private SeasonScoreService Service => new(
        _db,
        new WorldProvisioningService(_db, NullLogger<WorldProvisioningService>.Instance),
        _hub,
        _log,
        NullLogger<SeasonScoreService>.Instance);

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// When the realm under test opened. Relative to now rather than a fixed calendar date, so
    /// the season is genuinely running - a hard-coded date would quietly become a season that had
    /// already expired, and every test here would then be testing the closing path.
    /// </summary>
    private static readonly DateTime SeasonStart = DateTime.UtcNow.AddDays(-2);

    // -----------------------------------------------------------------
    // Holding ground
    // -----------------------------------------------------------------

    [Fact]
    public async Task HoldingARegionEarnsPointsForEveryHourItIsHeld()
    {
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));

        await Service.SettleAllAsync(instance.Id, at: SeasonStart);
        await Service.SettleAllAsync(instance.Id, at: SeasonStart.AddHours(10));

        var score = await ScoreOf(TestIds.Player);
        Assert.True(score.SettledPoints > 0);
        Assert.Equal(score.PointsPerHour * 10, score.SettledPoints, 3);
    }

    [Fact]
    public async Task SettlingTwiceForTheSameMomentPaysOnce()
    {
        // Settle-up is called from every path that can change a holding, several of which can fire
        // in quick succession. It has to be idempotent or the scoreboard drifts upward with traffic.
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));

        await Service.SettleAllAsync(instance.Id, at: SeasonStart);
        await Service.SettleAllAsync(instance.Id, at: SeasonStart.AddHours(4));
        double once = (await ScoreOf(TestIds.Player)).SettledPoints;

        await Service.SettleAllAsync(instance.Id, at: SeasonStart.AddHours(4));

        Assert.Equal(once, (await ScoreOf(TestIds.Player)).SettledPoints, 3);
    }

    [Fact]
    public async Task LosingARegionStopsItsIncomeButKeepsWhatItAlreadyPaid()
    {
        // The design's central promise: a bad week must not be unrecoverable. Punishing a loss twice
        // - by stopping the income and clawing back the points - is exactly what it rules out.
        var region = TestWorld.OwnedBy(TestIds.Player, "r1");
        var instance = await SeedAsync(region);

        await Service.SettleAllAsync(instance.Id, at: SeasonStart);
        await Service.SettleAllAsync(instance.Id, at: SeasonStart.AddHours(10));
        double earned = (await ScoreOf(TestIds.Player)).SettledPoints;

        await ReplaceWorldAsync(instance.Id, TestWorld.OwnedBy(TestIds.Rival, "r1"));
        await Service.SettleAllAsync(instance.Id, at: SeasonStart.AddHours(10));

        var after = await ScoreOf(TestIds.Player);
        Assert.Equal(earned, after.SettledPoints, 3);
        Assert.Equal(0, after.PointsPerHour);

        // And from there they earn nothing from ground, however long they wait.
        await Service.SettleAllAsync(instance.Id, at: SeasonStart.AddHours(100));
        Assert.Equal(earned, (await ScoreOf(TestIds.Player)).SettledPoints, 3);
    }

    [Fact]
    public async Task TakingARegionFromSomeoneMovesTheIncomeToItsNewHolder()
    {
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Rival, "r1"));
        await Service.SettleAllAsync(instance.Id, at: SeasonStart);

        await ReplaceWorldAsync(instance.Id, TestWorld.OwnedBy(TestIds.Player, "r1"));
        await Service.SettleAllAsync(instance.Id, at: SeasonStart.AddHours(1));

        Assert.True((await ScoreOf(TestIds.Player)).PointsPerHour > 0);
        Assert.Equal(0, (await ScoreOf(TestIds.Rival)).PointsPerHour);
    }

    [Fact]
    public async Task APlayerWhoArrivesMidSeasonEarnsFromWhenTheyArrived()
    {
        // Backdating a joiner to the season's opening would pay them for ground they did not hold.
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Owner, "r1"));
        await Service.SettleAllAsync(instance.Id, at: SeasonStart.AddDays(5));

        await JoinAsync(instance.Id, TestIds.Player);
        await ReplaceWorldAsync(instance.Id,
            TestWorld.OwnedBy(TestIds.Owner, "r1"), TestWorld.OwnedBy(TestIds.Player, "r2"));

        await Service.SettleAllAsync(instance.Id, at: SeasonStart.AddDays(5));
        await Service.SettleAllAsync(instance.Id, at: SeasonStart.AddDays(6));

        var joiner = await ScoreOf(TestIds.Player);
        Assert.Equal(joiner.PointsPerHour * 24, joiner.SettledPoints, 1);
    }

    // -----------------------------------------------------------------
    // Deeds
    // -----------------------------------------------------------------

    [Fact]
    public async Task ClearingASitePaysAPlayerWhoHoldsNothingAtAll()
    {
        // This is the way back. A player pushed off the map entirely can still fight, and fighting
        // still scores - otherwise being behind would be the same as being finished.
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Rival, "r1"));

        await Service.AwardAsync(instance.Id, TestIds.Player, SeasonDeed.SiteCleared);

        var score = await ScoreOf(TestIds.Player);
        Assert.Equal(SeasonScoreRules.PointsFor(SeasonDeed.SiteCleared), score.ClearingPoints, 3);
        Assert.Equal(score.ClearingPoints, score.SettledPoints, 3);
        Assert.Equal(0, score.PointsPerHour);
    }

    [Fact]
    public async Task RaidingAndRepellingBothPayAndAreBookedSeparately()
    {
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Rival, "r1"));

        await Service.AwardAsync(instance.Id, TestIds.Player, SeasonDeed.RaidLanded);
        await Service.AwardAsync(instance.Id, TestIds.Rival, SeasonDeed.RaidRepelled);

        Assert.True((await ScoreOf(TestIds.Player)).RaidingPoints > 0);
        Assert.True((await ScoreOf(TestIds.Rival)).RaidingPoints > 0);
        Assert.Equal(0, (await ScoreOf(TestIds.Player)).ClearingPoints);
    }

    [Fact]
    public async Task ADeedDoneAfterTheBellChangesNothing()
    {
        // A client that had not yet been told the season ended must not be able to alter a result
        // that has already been settled.
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));
        await Service.SettleAllAsync(instance.Id, at: SeasonStart);

        await ExpireSeasonAsync(instance.Id);
        double before = (await ScoreOf(TestIds.Player)).SettledPoints;

        await Service.AwardAsync(instance.Id, TestIds.Player, SeasonDeed.SiteCleared);

        var after = await ScoreOf(TestIds.Player);
        Assert.Equal(0, after.ClearingPoints);
        Assert.True(after.SettledPoints >= before);
    }

    // -----------------------------------------------------------------
    // The bell
    // -----------------------------------------------------------------

    [Fact]
    public async Task NothingIsEarnedFromGroundAfterTheSeasonCloses()
    {
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));
        await Service.SettleAllAsync(instance.Id, at: SeasonStart);

        // Ask for a settle long past the closing time; accrual is clamped to the bell.
        await Service.SettleAllAsync(instance.Id, at: instance.SeasonEndsAt.AddDays(10));

        var score = await ScoreOf(TestIds.Player);
        double wholeSeason = score.PointsPerHour * (instance.SeasonEndsAt - SeasonStart).TotalHours;
        Assert.Equal(wholeSeason, score.SettledPoints, 1);
    }

    [Fact]
    public async Task AnExpiredSeasonIsClosedRecordedAndReplacedByTheNextOne()
    {
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));
        await Service.SettleAllAsync(instance.Id, at: SeasonStart);
        await ExpireSeasonAsync(instance.Id);

        bool closed = await Service.EnsureSeasonCurrentAsync(instance.Id);

        Assert.True(closed);

        var results = await _db.SeasonResults.Where(r => r.GameInstanceId == instance.Id).ToListAsync();
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(1, r.SeasonNumber));

        var reloaded = await _db.GameInstances.FirstAsync(g => g.Id == instance.Id);
        Assert.Equal(2, reloaded.SeasonNumber);
        Assert.False(reloaded.SeasonHasExpired(DateTime.UtcNow));
    }

    [Fact]
    public async Task TheWinnerIsWhoeverHasTheMostPointsAndIsRankedFirst()
    {
        var instance = await SeedAsync(
            TestWorld.OwnedBy(TestIds.Player, "r1"), TestWorld.OwnedBy(TestIds.Rival, "r2"));
        await JoinAsync(instance.Id, TestIds.Player);
        await JoinAsync(instance.Id, TestIds.Rival);

        await Service.SettleAllAsync(instance.Id, at: SeasonStart);

        // Same ground, so the rival wins it on deeds - which is the point: territory is not the
        // only way to score, and a player can be caught by someone who fought more.
        await Service.AwardAsync(instance.Id, TestIds.Rival, SeasonDeed.SiteCleared);
        await Service.AwardAsync(instance.Id, TestIds.Rival, SeasonDeed.RaidLanded);

        await ExpireSeasonAsync(instance.Id);
        await Service.EnsureSeasonCurrentAsync(instance.Id);

        var results = await _db.SeasonResults
            .Where(r => r.GameInstanceId == instance.Id && r.SeasonNumber == 1)
            .OrderBy(r => r.Rank)
            .ToListAsync();

        Assert.Equal(TestIds.Rival, results[0].UserId);
        Assert.Equal(1, results[0].Rank);
        Assert.True(results[0].TotalPoints > results[1].TotalPoints);
    }

    [Fact]
    public async Task AClosedSeasonRecordsWhereEachPlayersPointsCameFrom()
    {
        // The breakdown is the carry-over's substance: what a standing was made of is the only thing
        // a future reward could be built on, and it cannot be reconstructed afterwards.
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));
        await Service.SettleAllAsync(instance.Id, at: SeasonStart);
        await Service.AwardAsync(instance.Id, TestIds.Player, SeasonDeed.SiteCleared);

        await ExpireSeasonAsync(instance.Id);
        await Service.EnsureSeasonCurrentAsync(instance.Id);

        var result = await _db.SeasonResults.FirstAsync(r => r.UserId == TestIds.Player);
        Assert.True(result.HoldingPoints > 0);
        Assert.True(result.ClearingPoints > 0);
        Assert.Equal(1, result.RegionsHeld);
        Assert.Equal(SeasonStart, result.SeasonStartedAt);
    }

    [Fact]
    public async Task TheNextSeasonStartsEveryoneAtZeroOnAWorldThatIsNotTheOldOne()
    {
        // Resetting rather than freezing is what keeps a group playing together. It only works if
        // the map is genuinely new - the same map again would make a reset a memory wipe.
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));
        await JoinAsync(instance.Id, TestIds.Player);
        await Service.SettleAllAsync(instance.Id, at: SeasonStart);
        await Service.AwardAsync(instance.Id, TestIds.Player, SeasonDeed.SiteCleared);

        string oldWorld = (await _db.WorldViewGameData.AsNoTracking()
            .FirstAsync(w => w.GameInstanceId == instance.Id)).GameData;

        await ExpireSeasonAsync(instance.Id);
        await Service.EnsureSeasonCurrentAsync(instance.Id);

        var second = await _db.SeasonScores
            .FirstAsync(s => s.GameInstanceId == instance.Id && s.SeasonNumber == 2 && s.UserId == TestIds.Player);

        Assert.Equal(0, second.ClearingPoints);
        Assert.Equal(0, second.RaidingPoints);
        Assert.Equal(0, second.SettledPoints, 3);

        string newWorld = (await _db.WorldViewGameData.AsNoTracking()
            .FirstAsync(w => w.GameInstanceId == instance.Id)).GameData;
        Assert.NotEqual(oldWorld, newWorld);
    }

    [Fact]
    public async Task AResetClearsTheRunsAndCooldownsThatBelongedToTheOldWorld()
    {
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));
        _db.PveRuns.Add(new PveRun { GameInstanceId = instance.Id, UserId = TestIds.Player, LocationId = "r1:0" });
        _db.RegionRaids.Add(new RegionRaid
        {
            GameInstanceId = instance.Id, UserId = TestIds.Player, RegionId = "r1", RaidedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        await ExpireSeasonAsync(instance.Id);
        await Service.EnsureSeasonCurrentAsync(instance.Id);

        Assert.Empty(await _db.PveRuns.Where(r => r.GameInstanceId == instance.Id).ToListAsync());
        Assert.Empty(await _db.RegionRaids.Where(r => r.GameInstanceId == instance.Id).ToListAsync());
    }

    [Fact]
    public async Task ClosingIsDoneOnceHoweverManyRequestsNoticeIt()
    {
        // Without a scheduler, arriving traffic is what notices the bell - and several requests can
        // notice it at the same moment. A realm must not reset twice.
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));
        await Service.SettleAllAsync(instance.Id, at: SeasonStart);
        await ExpireSeasonAsync(instance.Id);

        Assert.True(await Service.EnsureSeasonCurrentAsync(instance.Id));
        Assert.False(await Service.EnsureSeasonCurrentAsync(instance.Id));

        var reloaded = await _db.GameInstances.FirstAsync(g => g.Id == instance.Id);
        Assert.Equal(2, reloaded.SeasonNumber);
        Assert.Single(await _db.SeasonResults
            .Where(r => r.GameInstanceId == instance.Id && r.UserId == TestIds.Player)
            .ToListAsync());
    }

    [Fact]
    public async Task ASeasonThatStillHasTimeLeftIsNotTouched()
    {
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));

        Assert.False(await Service.EnsureSeasonCurrentAsync(instance.Id));
        Assert.Empty(await _db.SeasonResults.ToListAsync());
    }

    [Fact]
    public async Task ClosingTellsTheRealmWhatHappenedAndThatTheMapChanged()
    {
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));
        await Service.SettleAllAsync(instance.Id, at: SeasonStart);
        await ExpireSeasonAsync(instance.Id);

        await Service.EnsureSeasonCurrentAsync(instance.Id);

        var sent = _hub.MethodsSentTo(instance.Id).ToList();
        Assert.Contains("SeasonEnded", sent);
        Assert.Contains("WorldViewGameDataUpdated", sent);
        Assert.NotEmpty(_log.Of("SEASON-END"));
    }

    // -----------------------------------------------------------------
    // The table
    // -----------------------------------------------------------------

    [Fact]
    public async Task ReadingTheStandingsDoesNotChangeThem()
    {
        // The board is projected, never settled on read. A scoreboard that only becomes correct when
        // somebody looks at it would score a watched realm differently from an unwatched one.
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));
        await Service.SettleAllAsync(instance.Id, at: SeasonStart);

        var before = (await ScoreOf(TestIds.Player)).LastSettledAt;
        await Service.StandingsAsync(instance.Id);

        Assert.Equal(before, (await ScoreOf(TestIds.Player)).LastSettledAt);
    }

    [Fact]
    public async Task TheStandingsShowWhatEachPlayerIsEarningAndHowMuchTheyHold()
    {
        var instance = await SeedAsync(
            TestWorld.OwnedBy(TestIds.Player, "r1"), TestWorld.OwnedBy(TestIds.Player, "r2"));
        await JoinAsync(instance.Id, TestIds.Player);
        await Service.SettleAllAsync(instance.Id, at: SeasonStart);

        var standings = await Service.StandingsAsync(instance.Id);
        var player = standings.Standings.First(s => s.UserId == TestIds.Player);

        Assert.Equal(2, player.RegionsHeld);
        Assert.True(player.PointsPerHour > 0);
        Assert.Equal(1, instance.SeasonNumber);
        Assert.Equal(instance.SeasonEndsAt, standings.SeasonEndsAt);
    }

    [Fact]
    public async Task TheStandingsProjectForwardFromTheLastSettleWithoutWritingAnything()
    {
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));
        await Service.SettleAllAsync(instance.Id, at: DateTime.UtcNow.AddHours(-3));

        var standings = await Service.StandingsAsync(instance.Id);
        var player = standings.Standings.First(s => s.UserId == TestIds.Player);
        var stored = await ScoreOf(TestIds.Player);

        Assert.True(player.TotalPoints > stored.SettledPoints);
    }

    [Fact]
    public async Task PlayersWithTheSameScoreShareARank()
    {
        var instance = await SeedAsync(
            TestWorld.OwnedBy(TestIds.Player, "r1"), TestWorld.OwnedBy(TestIds.Rival, "r1b"));
        await JoinAsync(instance.Id, TestIds.Player);
        await JoinAsync(instance.Id, TestIds.Rival);

        var standings = await Service.StandingsAsync(instance.Id);
        var tied = standings.Standings.Where(s => s.UserId != TestIds.Owner).ToList();

        Assert.Equal(2, tied.Count);
        Assert.Equal(tied[0].Rank, tied[1].Rank);
    }

    [Fact]
    public async Task APlayerCanReadBackWhereTheyFinished()
    {
        var instance = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "r1"));
        await Service.SettleAllAsync(instance.Id, at: SeasonStart);
        await ExpireSeasonAsync(instance.Id);
        await Service.EnsureSeasonCurrentAsync(instance.Id);

        var history = await Service.HistoryForUserAsync(TestIds.Player);

        Assert.Single(history);
        Assert.Equal(1, history[0].SeasonNumber);
        Assert.Equal("Test Realm", history[0].RealmName);
    }

    // -----------------------------------------------------------------
    // Seeding
    // -----------------------------------------------------------------

    /// <summary>
    /// A realm whose season started at a known moment, holding exactly these regions. The owner is
    /// always a member; other holders are added to the world but only become members when they join.
    /// </summary>
    private async Task<GameInstance> SeedAsync(params WorldRegionData[] regions)
    {
        var instance = await _db.AddInstanceAsync();
        instance.SeasonStartedAt = SeasonStart;
        instance.SeasonLengthDays = 30;
        instance.SeasonNumber = 1;
        await _db.SaveChangesAsync();

        foreach (var userId in regions.Select(r => r.OwnerUserId).Where(id => !string.IsNullOrEmpty(id)).Distinct())
            await EnsureUserAsync(userId!);

        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(regions));
        return instance;
    }

    private async Task ReplaceWorldAsync(Guid gameInstanceId, params WorldRegionData[] regions)
    {
        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == gameInstanceId);
        row.GameData = TestWorld.Blob(regions).ToJsonString();
        await _db.SaveChangesAsync();
    }

    /// <summary>Gives a user a foothold in the realm, which is what makes them one of its players.</summary>
    private async Task JoinAsync(Guid gameInstanceId, string userId)
    {
        await EnsureUserAsync(userId);

        if (await _db.PlayerGameData.AnyAsync(p => p.GameInstanceId == gameInstanceId && p.UserId == userId))
            return;

        await _db.AddPlayerSaveAsync(gameInstanceId, userId, "{}");
    }

    private async Task EnsureUserAsync(string userId)
    {
        if (await _db.Users.AnyAsync(u => u.Id == userId)) return;

        _db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, DisplayName = userId });
        await _db.SaveChangesAsync();
    }

    /// <summary>Moves the realm's start back so its season has already run out.</summary>
    private async Task ExpireSeasonAsync(Guid gameInstanceId)
    {
        var instance = await _db.GameInstances.FirstAsync(g => g.Id == gameInstanceId);
        instance.SeasonStartedAt = SeasonStart;

        // Shortening the season is how the bell is rung: the realm opened two days ago, so a
        // one-day season is already over. Moving the clock is not an option in a test, and
        // waiting for one is not either.
        instance.SeasonLengthDays = 1;
        await _db.SaveChangesAsync();
    }

    private async Task<SeasonScore> ScoreOf(string userId)
        => await _db.SeasonScores.AsNoTracking().FirstAsync(s => s.UserId == userId);
}
