using Enums;
using Items.Models;
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
/// The season's end (<c>docs/design/season-end.md</c>). Pinned: the factions race the players on their
/// land; a faction that out-holds everyone takes the realm and nobody is crowned; a finish earns a chest
/// by its average rate, opened once and granted by the server; the end page shows once on return and
/// can be read again; and a faction taking land settles the scoreboard there and then.
/// </summary>
public class SeasonEndTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeHubContext _hub = new();
    private readonly FakeSessionLog _log = new();
    private readonly FakeGameContent _content = new()
    {
        DroppableItems = new[] { new ItemTemplate { ItemName = "Cinder Blade", ItemType = ItemTypes.Weapon } }
    };

    private static readonly DateTime SeasonStart = DateTime.UtcNow.AddDays(-2);

    public void Dispose() => _db.Dispose();

    private SeasonScoreService Seasons => new(
        _db,
        new GoldService(_db, _log, NullLogger<GoldService>.Instance),
        new WorldProvisioningService(_db, NullLogger<WorldProvisioningService>.Instance),
        _hub,
        _log,
        NullLogger<SeasonScoreService>.Instance,
        content: _content,
        items: new ItemLedgerService(_db, _log, NullLogger<ItemLedgerService>.Instance),
        letters: Letters);

    private LetterService Letters => new(_db, _hub, NullLogger<LetterService>.Instance, new FakeClock());

    private static WorldRegionData Held(FactionId faction, string id, int tier = 4)
    {
        var region = TestWorld.Region(id, ownership: LocationOwnership.Enemy, tier: tier);
        region.Faction = faction;
        return region;
    }

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void TheChestFollowsTheAverageRate_NotTheSeasonsLength()
    {
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        // 100 an hour held for a day and for a month earn the same chest.
        Assert.Equal(ItemRarityTypes.Rare, SeasonEndRules.ChestFor(100 * 24, start, start.AddDays(1)));
        Assert.Equal(ItemRarityTypes.Rare, SeasonEndRules.ChestFor(100 * 24 * 30, start, start.AddDays(30)));

        Assert.Equal(ItemRarityTypes.Uncommon, SeasonEndRules.ChestFor(10 * 24, start, start.AddDays(1)));
        Assert.Equal(ItemRarityTypes.Magic, SeasonEndRules.ChestFor(40 * 24, start, start.AddDays(1)));
        Assert.Equal(ItemRarityTypes.Legendary, SeasonEndRules.ChestFor(200 * 24, start, start.AddDays(1)));

        Assert.Null(SeasonEndRules.ChestFor(0, start, start.AddDays(1)));
    }

    [Fact]
    public void AFactionEarnsFromItsOwnLandAlone()
    {
        var mine = Held(FactionId.Grimjaw, "g", tier: 3);
        var theirs = Held(FactionId.Ashkin, "a", tier: 3);
        var player = TestWorld.OwnedBy(TestIds.Player, "p", tier: 3);

        double rate = SeasonEndRules.RateForFaction(FactionId.Grimjaw, new[] { mine, theirs, player });

        Assert.Equal(SeasonScoreRules.RateFor(mine), rate, 3);
        Assert.Equal(FactionId.Grimjaw, SeasonEndRules.FactionOf(SeasonEndRules.FactionScoreId(FactionId.Grimjaw)));
        Assert.Equal(FactionId.None, SeasonEndRules.FactionOf(TestIds.Player));
    }

    // -----------------------------------------------------------------
    // The race
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheFactionsScoreTheirLandBesideThePlayers()
    {
        var grimjaw = Held(FactionId.Grimjaw, "g");
        var realm = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "p"), grimjaw);

        await Seasons.SettleAllAsync(realm, at: SeasonStart);
        await Seasons.SettleAllAsync(realm, at: SeasonStart.AddHours(10));

        var score = await _db.FactionSeasonScores.AsNoTracking().SingleAsync(f => f.Faction == FactionId.Grimjaw);
        Assert.Equal(SeasonScoreRules.RateFor(grimjaw) * 10, score.SettledPoints, 3);

        var standings = await Seasons.StandingsAsync(realm);
        var row = standings.Standings.Single(s => s.UserId == "faction:Grimjaw");
        Assert.Equal("The Grimjaw", row.DisplayName);
        Assert.Equal(1, row.RegionsHeld);
        Assert.Equal(0, row.ClearingPoints);
        Assert.Contains(standings.Standings, s => s.UserId == "faction:Ashkin");
    }

    [Fact]
    public async Task AFactionThatOutHoldsEveryone_TakesTheRealm_AndNobodyIsCrowned()
    {
        var realm = await SeedAsync(
            TestWorld.OwnedBy(TestIds.Player, "p", tier: 1),
            Held(FactionId.Grimjaw, "g1"), Held(FactionId.Grimjaw, "g2"));
        await Seasons.SettleAllAsync(realm, at: SeasonStart);

        await CloseAsync(realm);

        var table = await Seasons.ResultsAsync(realm, 1);
        Assert.Equal("faction:Grimjaw", table[0].UserId);
        Assert.Equal(1, table[0].Rank);
        Assert.Equal("The Grimjaw", table[0].DisplayName);
        Assert.Equal(2, table[0].RegionsHeld);

        var me = table.Single(e => e.UserId == TestIds.Player);
        Assert.Equal(2, me.Rank);
        Assert.Equal(0, me.Crowns);
    }

    [Fact]
    public async Task APlayerWhoOutscoresTheFactions_IsCrowned_AndEarnsAChest()
    {
        var realm = await SeedAsync(
            TestWorld.OwnedBy(TestIds.Player, "p1", tier: 4), TestWorld.OwnedBy(TestIds.Player, "p2", tier: 4),
            Held(FactionId.Grimjaw, "g", tier: 3));
        await Seasons.SettleAllAsync(realm, at: SeasonStart);

        await CloseAsync(realm);

        var result = await _db.SeasonResults.AsNoTracking().SingleAsync(r => r.UserId == TestIds.Player);
        Assert.Equal(1, result.Rank);
        Assert.NotNull(result.ChestRarity);
        Assert.Null(result.SeenAt);

        // The crown follows the player into the next season's table.
        var next = await Seasons.StandingsAsync(realm);
        Assert.Equal(1, next.Standings.Single(s => s.UserId == TestIds.Player).Crowns);
        Assert.Equal(0, next.Standings.Single(s => s.UserId == "faction:Grimjaw").Crowns);
    }

    [Fact]
    public async Task ALonePlayersFirstPlace_InASeasonWithNobodyElseRanked_IsNoCrown()
    {
        // A realm closed before the factions were scored had one row: winning it beat nobody.
        var realm = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "p"));
        _db.SeasonResults.Add(new SeasonResult
        {
            GameInstanceId = realm, UserId = TestIds.Player, SeasonNumber = 0, Rank = 1, TotalPoints = 500,
            SeasonStartedAt = SeasonStart.AddDays(-30), SeasonEndedAt = SeasonStart
        });
        await _db.SaveChangesAsync();

        var standings = await Seasons.StandingsAsync(realm);

        Assert.Equal(0, standings.Standings.Single(s => s.UserId == TestIds.Player).Crowns);
    }

    [Fact]
    public async Task EveryRankedPlayerGetsTheSeasonsLetter_WhichOutlivesTheReset_AndNeedsThemUntilTheChestIsOpened()
    {
        var realm = await SeedAsync(
            TestWorld.OwnedBy(TestIds.Player, "p1", tier: 4), TestWorld.OwnedBy(TestIds.Player, "p2", tier: 4),
            Held(FactionId.Grimjaw, "g", tier: 3));
        await Seasons.SettleAllAsync(realm, at: SeasonStart);

        await CloseAsync(realm);

        var letter = Assert.Single((await Letters.PageAsync(realm, TestIds.Player)).Letters);
        Assert.Equal(nameof(LetterKind.SeasonEnded), letter.Kind);
        Assert.Equal("1", letter.SubjectId);
        Assert.StartsWith("1|1|", letter.Detail);
        Assert.True(letter.Flagged);
        // One per ranked player; a faction is ranked but writes no letter.
        Assert.Equal(await _db.SeasonResults.CountAsync(), await _db.Letters.CountAsync());
        Assert.DoesNotContain(await _db.Letters.ToListAsync(), l => l.UserId.StartsWith("faction"));
    }

    // -----------------------------------------------------------------
    // The page and the chest
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheEndPageIsShownOnceOnReturn_AndCanBeReadAgain()
    {
        var realm = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "p"), Held(FactionId.Ashkin, "a", tier: 1));
        await Seasons.SettleAllAsync(realm, at: SeasonStart);
        await CloseAsync(realm);

        var page = await Seasons.EndedForAsync(realm, TestIds.Player, unseenOnly: true);
        Assert.NotNull(page);
        Assert.Equal(1, page!.SeasonNumber);
        Assert.False(page.Seen);
        Assert.Equal(TestIds.Player, page.Mine!.UserId);
        Assert.Contains(page.FinalTable, e => e.UserId == "faction:Ashkin");
        Assert.NotNull(page.ChestRarity);

        Assert.True(await Seasons.MarkSeenAsync(realm, TestIds.Player, 1));

        Assert.Null(await Seasons.EndedForAsync(realm, TestIds.Player, unseenOnly: true));
        var again = await Seasons.EndedForAsync(realm, TestIds.Player, unseenOnly: false, seasonNumber: 1);
        Assert.True(again!.Seen);
    }

    [Fact]
    public async Task TheChestOpensOnce_AndItsPieceIsGrantedByTheServer()
    {
        var realm = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "p"));
        await Seasons.SettleAllAsync(realm, at: SeasonStart);
        await CloseAsync(realm);
        var rarity = (await _db.SeasonResults.AsNoTracking().SingleAsync(r => r.UserId == TestIds.Player)).ChestRarity;

        var (error, chest) = await Seasons.OpenChestAsync(realm, TestIds.Player, 1);

        Assert.Equal(SeasonScoreService.ChestError.None, error);
        Assert.Equal("Cinder Blade", chest!.Item.ItemName);
        Assert.Equal(rarity.ToString(), chest.Rarity);
        Assert.True(await _db.ItemGrants.AnyAsync(g => g.UserId == TestIds.Player && g.ItemId == chest.Item.Id));

        var (twice, _) = await Seasons.OpenChestAsync(realm, TestIds.Player, 1);
        Assert.Equal(SeasonScoreService.ChestError.AlreadyOpened, twice);
        Assert.Equal(1, await _db.ItemGrants.CountAsync(g => g.UserId == TestIds.Player));

        var page = await Seasons.EndedForAsync(realm, TestIds.Player, unseenOnly: false, seasonNumber: 1);
        Assert.True(page!.ChestOpened);
        Assert.Equal("Cinder Blade", page.ChestItemName);
    }

    [Fact]
    public async Task ThePlayerWhoScoredNothing_HasNoChestToOpen()
    {
        var realm = await SeedAsync(Held(FactionId.Grimjaw, "g"));
        await JoinAsync(realm, TestIds.Player);
        await Seasons.SettleAllAsync(realm, at: SeasonStart);
        await CloseAsync(realm);

        var (error, _) = await Seasons.OpenChestAsync(realm, TestIds.Player, 1);

        Assert.Equal(SeasonScoreService.ChestError.NoChest, error);
    }

    [Fact]
    public async Task TheDebugBell_ClosesTheSeasonNow_KeepingWhatWasEarned()
    {
        var realm = await SeedAsync(TestWorld.OwnedBy(TestIds.Player, "p"));
        await Seasons.SettleAllAsync(realm, at: SeasonStart);

        Assert.True(await Seasons.DebugRingBellAsync(realm));

        var result = await _db.SeasonResults.AsNoTracking().SingleAsync(r => r.UserId == TestIds.Player);
        Assert.True(result.TotalPoints > 0);
        Assert.Equal(2, (await _db.GameInstances.AsNoTracking().SingleAsync(g => g.Id == realm)).SeasonNumber);
    }

    // -----------------------------------------------------------------
    // A faction moving the land settles the board
    // -----------------------------------------------------------------

    [Fact]
    public async Task AFactionExpanding_SettlesTheScoreboardThereAndThen()
    {
        var realm = await SeedAsync(Held(FactionId.Grimjaw, "g"), Wild("wild", 1, 0));
        var seasons = Seasons;
        var noon = DateTime.UtcNow;
        await seasons.SettleAllAsync(realm, at: noon.AddHours(-1));
        double before = (await _db.FactionSeasonScores.AsNoTracking().SingleAsync(f => f.Faction == FactionId.Grimjaw)).PointsPerHour;

        var factions = new FactionService(_db, _log, _content,
            new WarLogService(_db, _hub, NullLogger<WarLogService>.Instance, new FakeClock()), seasons, _hub);
        await factions.ActAsync(realm, noon, force: FactionId.Grimjaw, forceAction: FactionAction.Expand);

        var after = await _db.FactionSeasonScores.AsNoTracking().SingleAsync(f => f.Faction == FactionId.Grimjaw);
        Assert.True(after.PointsPerHour > before);
        Assert.Equal(noon, after.LastSettledAt);
    }

    // -----------------------------------------------------------------
    // Seeding
    // -----------------------------------------------------------------

    private static WorldRegionData Wild(string id, int q, int r)
    {
        var region = TestWorld.Region(id, q, r, tier: 3);
        region.Entrenchment = 0;
        return region;
    }

    private async Task<Guid> SeedAsync(params WorldRegionData[] regions)
    {
        var instance = await _db.AddInstanceAsync();
        instance.SeasonStartedAt = SeasonStart;
        instance.SeasonLengthDays = 30;
        instance.SeasonNumber = 1;
        await _db.SaveChangesAsync();

        foreach (var userId in regions.Select(r => r.OwnerUserId).Where(id => !string.IsNullOrEmpty(id)).Distinct())
            await JoinAsync(instance.Id, userId!);

        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(regions));
        return instance.Id;
    }

    private async Task JoinAsync(Guid realm, string userId)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == userId))
        {
            _db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, DisplayName = userId });
            await _db.SaveChangesAsync();
        }
        if (!await _db.PlayerGameData.AnyAsync(p => p.GameInstanceId == realm && p.UserId == userId))
            await _db.AddPlayerSaveAsync(realm, userId, "{}");
    }

    /// <summary>Rings the bell: the realm opened two days ago, so a one-day season is over.</summary>
    private async Task CloseAsync(Guid realm)
    {
        var instance = await _db.GameInstances.FirstAsync(g => g.Id == realm);
        instance.SeasonLengthDays = 1;
        await _db.SaveChangesAsync();
        Assert.True(await Seasons.EnsureSeasonCurrentAsync(realm));
    }
}
