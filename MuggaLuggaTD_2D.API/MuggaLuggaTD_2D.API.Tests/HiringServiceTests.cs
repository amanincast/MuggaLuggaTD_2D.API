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
/// The Hiring Hall (design 12e). What is pinned: the board fills on a first visit and then one local
/// at a time; a hire needs a bed (land) and the gold; a worker goes only to a site of their trade, in
/// land their employer holds, with room; gathering pays whole goods into the wallet and keeps the
/// fraction; a Foreman lifts the rest of the crew; losing the land sends workers home; and a season
/// reset takes the workers, the board and the goods.
/// </summary>
public class HiringServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private Guid _realm;
    private DateTime _now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Player = TestIds.Player;

    public void Dispose() => _db.Dispose();

    private GoldService Gold => new(_db, _log, NullLogger<GoldService>.Instance);
    private MaterialWalletService Wallet => new(_db, _log, NullLogger<MaterialWalletService>.Instance);

    private HiringService Hiring => new(_db, Wallet, Gold, _log) { Dice = _dice, Clock = () => _now };
    private readonly Random _dice = new(7);

    /// <summary>
    /// A world of <paramref name="regions"/> held by the player, the first of which has a resource
    /// site. Region ids are searched rather than assumed: whether a region has a node is the
    /// generator's call.
    /// </summary>
    private async Task<(WorldRegionData Region, SiteSpec Site, ResourceTrade Trade)> WorldAsync(int regions = 1, string owner = Player)
    {
        _realm = (await _db.AddInstanceAsync()).Id;
        for (int i = 0; i < 200; i++)
        {
            var region = TestWorld.OwnedBy(owner, $"r{i}", tier: 2);
            var site = RegionGenerator.Generate(region).Sites.FirstOrDefault(s => s.Type == LocationType.ResourceNode);
            if (site == null) continue;

            var all = new List<WorldRegionData> { region };
            for (int n = 1; n < regions; n++)
                all.Add(TestWorld.Region($"x{n}", q: n, r: 0, ownership: LocationOwnership.Player, ownerUserId: owner));
            await _db.AddWorldAsync(_realm, TestWorld.Blob(all.ToArray()));
            return (region, site, ResourceNodeRules.TradeOf(site.SiteId, region.Biome));
        }
        throw new InvalidOperationException("no region with a resource site");
    }

    private async Task<HiredWorker> WorkerAsync(ResourceTrade trade, params WorkerTrait[] traits)
    {
        var worker = new HiredWorker
        {
            GameInstanceId = _realm, UserId = Player, Name = "Tam", Trade = trade, Tier = WorkerTier.Local,
            Traits = HiringTraits.Write(traits), HomeBiome = BiomeType.Volcanic, HiredAt = _now, LastSettledAt = _now
        };
        _db.HiredWorkers.Add(worker);
        await _db.SaveChangesAsync();
        return worker;
    }

    private async Task<int> GoodsAsync(ResourceTrade trade)
        => (await Wallet.ReadAsync(_realm, Player)).Where(m => m.MaterialName == ResourceNodeRules.GoodOf(trade)).Sum(m => m.Quantity);

    // -----------------------------------------------------------------
    // The board
    // -----------------------------------------------------------------

    [Fact]
    public async Task AFirstVisitFillsTheBoard()
    {
        await WorldAsync();

        var board = await Hiring.ReadBoardAsync(_realm, Player);

        Assert.Equal(HiringRules.BoardSize, board.Count);
        Assert.Equal(Enumerable.Range(0, HiringRules.BoardSize), board.Select(c => c.Slot));
    }

    [Fact]
    public async Task AnEmptySeatFillsOneLocalPerInterval()
    {
        await WorldAsync(regions: 3);
        await Gold.GrantAsync(_realm, Player, 10_000, "test");
        await Hiring.ReadBoardAsync(_realm, Player);
        await Hiring.HireAsync(_realm, Player, 0);
        await Hiring.HireAsync(_realm, Player, 1);

        _now += HiringRules.ArrivalInterval - TimeSpan.FromMinutes(1);
        Assert.Equal(4, (await Hiring.ReadBoardAsync(_realm, Player)).Count);

        _now += TimeSpan.FromMinutes(1);
        Assert.Equal(5, (await Hiring.ReadBoardAsync(_realm, Player)).Count);
    }

    [Fact]
    public async Task AFullBoardBanksNoArrivals()
    {
        await WorldAsync(regions: 3);
        await Gold.GrantAsync(_realm, Player, 10_000, "test");
        await Hiring.ReadBoardAsync(_realm, Player);

        _now += TimeSpan.FromHours(20);
        await Hiring.ReadBoardAsync(_realm, Player);
        await Hiring.HireAsync(_realm, Player, 0);
        await Hiring.HireAsync(_realm, Player, 1);

        Assert.Equal(4, (await Hiring.ReadBoardAsync(_realm, Player)).Count);
    }

    [Fact]
    public async Task ARefreshDoublesUntilADungeonIsCleared()
    {
        await WorldAsync();
        await Gold.GrantAsync(_realm, Player, 10_000, "test");
        var before = (await Hiring.ReadBoardAsync(_realm, Player)).Select(c => c.Name).ToList();

        Assert.True((await Hiring.RefreshAsync(_realm, Player)).Succeeded);
        Assert.Equal(HiringRules.RefreshBaseCost * 2, await Hiring.RefreshCostAsync(_realm, Player));
        Assert.Equal(10_000 - HiringRules.RefreshBaseCost, await Gold.BalanceAsync(_realm, Player));
        Assert.NotEqual(before, (await Hiring.ReadBoardAsync(_realm, Player)).Select(c => c.Name).ToList());

        await Hiring.ResetRefreshAsync(_realm, Player);
        Assert.Equal(HiringRules.RefreshBaseCost, await Hiring.RefreshCostAsync(_realm, Player));
    }

    [Fact]
    public async Task ARefreshThePlayerCannotAffordChangesNothing()
    {
        await WorldAsync();
        var before = (await Hiring.ReadBoardAsync(_realm, Player)).Select(c => c.Name).ToList();

        var outcome = await Hiring.RefreshAsync(_realm, Player);

        Assert.Equal(HiringError.CannotAfford, outcome.Error);
        Assert.Equal(before, (await Hiring.ReadBoardAsync(_realm, Player)).Select(c => c.Name).ToList());
    }

    // -----------------------------------------------------------------
    // Hiring
    // -----------------------------------------------------------------

    [Fact]
    public async Task AHireCostsGold_AndFreesTheSeat()
    {
        await WorldAsync();
        await Gold.GrantAsync(_realm, Player, 5_000, "test");
        var seat = (await Hiring.ReadBoardAsync(_realm, Player)).First();

        var (outcome, worker) = await Hiring.HireAsync(_realm, Player, seat.Slot);

        Assert.True(outcome.Succeeded);
        Assert.Equal(seat.Name, worker!.Name);
        Assert.Equal(5_000 - HiringRules.CostOf(seat.Tier), await Gold.BalanceAsync(_realm, Player));
        Assert.DoesNotContain(await Hiring.ReadBoardAsync(_realm, Player), c => c.Slot == seat.Slot);
    }

    [Fact]
    public async Task BedsAreTiedToLand()
    {
        await WorldAsync(regions: 1);
        await Gold.GrantAsync(_realm, Player, 50_000, "test");
        await Hiring.ReadBoardAsync(_realm, Player);

        for (int slot = 0; slot < HiringRules.BedsPerRegion; slot++)
            Assert.True((await Hiring.HireAsync(_realm, Player, slot)).Outcome.Succeeded);

        var (refused, _) = await Hiring.HireAsync(_realm, Player, HiringRules.BedsPerRegion);
        Assert.Equal(HiringError.NoBeds, refused.Error);
    }

    [Fact]
    public async Task AHireThePlayerCannotAffordLeavesTheSeatTaken()
    {
        await WorldAsync();
        var seat = (await Hiring.ReadBoardAsync(_realm, Player)).First();

        var (outcome, _) = await Hiring.HireAsync(_realm, Player, seat.Slot);

        Assert.Equal(HiringError.CannotAfford, outcome.Error);
        Assert.Contains(await Hiring.ReadBoardAsync(_realm, Player), c => c.Slot == seat.Slot);
        Assert.Empty(await Hiring.WorkersAsync(_realm, Player));
    }

    // -----------------------------------------------------------------
    // Assigning
    // -----------------------------------------------------------------

    [Fact]
    public async Task AWorkerGoesOnlyToASiteOfTheirTrade()
    {
        var (_, site, trade) = await WorldAsync();
        var other = ResourceNodeRules.Trades.First(t => t != trade);
        var worker = await WorkerAsync(other);

        var outcome = await Hiring.AssignAsync(_realm, Player, worker.Id, site.SiteId);

        Assert.Equal(HiringError.WrongTrade, outcome.Error);
    }

    [Fact]
    public async Task AVersatileWorkerWorksTheirSecondTrade()
    {
        var (_, site, trade) = await WorldAsync();
        var worker = await WorkerAsync(ResourceNodeRules.Trades.First(t => t != trade), WorkerTrait.Versatile);
        worker.SecondTrade = trade;
        await _db.SaveChangesAsync();

        Assert.True((await Hiring.AssignAsync(_realm, Player, worker.Id, site.SiteId)).Succeeded);
        Assert.True(worker.RatePerHour > 0);
    }

    [Fact]
    public async Task ASiteHasRoomForItsSlotsOnly()
    {
        var (region, site, trade) = await WorldAsync();
        for (int i = 0; i < ResourceNodeRules.SlotsFor(region.Tier); i++)
            Assert.True((await Hiring.AssignAsync(_realm, Player, (await WorkerAsync(trade)).Id, site.SiteId)).Succeeded);

        var outcome = await Hiring.AssignAsync(_realm, Player, (await WorkerAsync(trade)).Id, site.SiteId);

        Assert.Equal(HiringError.SiteFull, outcome.Error);
    }

    [Fact]
    public async Task NobodyIsSentToARivalsLand()
    {
        var (_, site, trade) = await WorldAsync(owner: TestIds.Rival);
        var worker = await WorkerAsync(trade);

        var outcome = await Hiring.AssignAsync(_realm, Player, worker.Id, site.SiteId);

        Assert.Equal(HiringError.NotYourLand, outcome.Error);
    }

    // -----------------------------------------------------------------
    // Gathering
    // -----------------------------------------------------------------

    [Fact]
    public async Task GatheringPaysWholeGoods_AndKeepsTheFraction()
    {
        var (_, site, trade) = await WorldAsync();
        var worker = await WorkerAsync(trade);
        await Hiring.AssignAsync(_realm, Player, worker.Id, site.SiteId);
        double rate = worker.RatePerHour;

        _now += TimeSpan.FromHours(2.5);
        await Hiring.SettlePlayerAsync(_realm, Player);

        double expected = rate * 2.5;
        Assert.Equal((int)Math.Floor(expected), await GoodsAsync(trade));
        Assert.Equal(expected - Math.Floor(expected), worker.Carry, 6);
    }

    [Fact]
    public async Task AWorkerAtTheHallGathersNothing()
    {
        var (_, _, trade) = await WorldAsync();
        await WorkerAsync(trade);

        _now += TimeSpan.FromHours(10);
        await Hiring.SettlePlayerAsync(_realm, Player);

        Assert.Equal(0, await GoodsAsync(trade));
    }

    [Fact]
    public async Task AForemanLiftsTheRestOfTheCrew()
    {
        var (_, site, trade) = await WorldAsync();
        var worker = await WorkerAsync(trade);
        await Hiring.AssignAsync(_realm, Player, worker.Id, site.SiteId);
        double alone = worker.RatePerHour;

        var foreman = await WorkerAsync(trade, WorkerTrait.Foreman);
        await Hiring.AssignAsync(_realm, Player, foreman.Id, site.SiteId);
        Assert.Equal(alone * (1 + HiringRules.ForemanBonus), worker.RatePerHour, 6);

        await Hiring.AssignAsync(_realm, Player, foreman.Id, null);
        Assert.Equal(alone, worker.RatePerHour, 6);
    }

    [Fact]
    public async Task LosingTheLandSendsWorkersHome_AfterPayingThem()
    {
        var (region, site, trade) = await WorldAsync();
        var worker = await WorkerAsync(trade);
        await Hiring.AssignAsync(_realm, Player, worker.Id, site.SiteId);
        double rate = worker.RatePerHour;

        _now += TimeSpan.FromHours(4);
        region.Ownership = LocationOwnership.Player;
        region.OwnerUserId = TestIds.Rival;
        await Hiring.SettleAllAsync(_realm, new[] { region }, _now);

        Assert.Null(worker.SiteId);
        Assert.Equal(0, worker.RatePerHour);
        Assert.Equal((int)Math.Floor(rate * 4), await GoodsAsync(trade));
    }

    // -----------------------------------------------------------------
    // Season reset
    // -----------------------------------------------------------------

    [Fact]
    public async Task AResetTakesTheWorkersTheBoardAndTheGoods()
    {
        var (_, site, trade) = await WorldAsync();
        await Hiring.ReadBoardAsync(_realm, Player);
        var worker = await WorkerAsync(trade);
        await Hiring.AssignAsync(_realm, Player, worker.Id, site.SiteId);
        _now += TimeSpan.FromHours(10);
        await Hiring.SettlePlayerAsync(_realm, Player);
        await Wallet.GrantAsync(_realm, Player, new List<MaterialGrant> { new() { MaterialName = "Minor Fire Essence", Quantity = 3 } }, "test");
        Assert.True(await GoodsAsync(trade) > 0);

        await HiringService.ResetRealmAsync(_db, _realm);
        await _db.SaveChangesAsync();

        Assert.Empty(await _db.HiredWorkers.Where(w => w.GameInstanceId == _realm).ToListAsync());
        Assert.Empty(await _db.HiringCandidates.Where(c => c.GameInstanceId == _realm).ToListAsync());
        Assert.Equal(0, await GoodsAsync(trade));
        Assert.Contains(await Wallet.ReadAsync(_realm, Player), m => m.MaterialName == "Minor Fire Essence");
    }
}
