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
/// Fortifying (Hiring Hall phase 3). What is pinned: the bill grows with level and tier and wants
/// ore from IV; starting the works pays it and marks the region for everyone; nothing is spent when
/// it is refused (short of goods, not yours, besieged, at V, already building); finished works raise
/// entrenchment, clear the scaffolding and re-rate the realm; and works on land that changed hands
/// are cancelled rather than raising someone else's walls.
/// </summary>
public class FortifyTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeHubContext _hub = new();
    private readonly FakeSessionLog _log = new();
    private DateTime _now = DateTime.UtcNow;
    private Guid _realm;
    private const string Player = TestIds.Player;

    public void Dispose() => _db.Dispose();

    private MaterialWalletService Wallet => new(_db, _log, NullLogger<MaterialWalletService>.Instance);

    private SeasonScoreService Seasons => new(_db, new GoldService(_db, _log, NullLogger<GoldService>.Instance),
        new WorldProvisioningService(_db, NullLogger<WorldProvisioningService>.Instance), _hub, _log,
        NullLogger<SeasonScoreService>.Instance);

    private FortifyService Fortify => new(_db, Wallet, Seasons,
        new WarLogService(_db, _hub, NullLogger<WarLogService>.Instance, new FakeClock()), _hub, _log) { Clock = () => _now };

    private static string Stone => ResourceNodeRules.GoodOf(ResourceTrade.Quarrier);
    private static string Timber => ResourceNodeRules.GoodOf(ResourceTrade.Forester);
    private static string Ore => ResourceNodeRules.GoodOf(ResourceTrade.Miner);

    private async Task<WorldRegionData> WorldAsync(int entrenchment = 1, int tier = 1, string owner = Player)
    {
        var instance = await _db.AddInstanceAsync();
        instance.SeasonStartedAt = _now.AddDays(-1);
        instance.SeasonLengthDays = 30;
        instance.SeasonNumber = 1;
        _realm = instance.Id;
        var region = TestWorld.OwnedBy(owner, "r1", tier: tier);
        region.Entrenchment = entrenchment;
        await _db.AddWorldAsync(_realm, TestWorld.Blob(region));
        return region;
    }

    private async Task GiveAsync(string good, int quantity)
        => await Wallet.GrantAsync(_realm, Player, new List<MaterialGrant> { new() { MaterialName = good, Quantity = quantity } }, "test");

    private async Task<int> HeldAsync(string good)
        => (await Wallet.ReadAsync(_realm, Player)).Where(m => m.MaterialName == good).Sum(m => m.Quantity);

    private async Task<WorldRegionData> RegionAsync()
        => WorldRegionBlob.ReadRegion(WorldRegionBlob.FindRegion(await _db.ReadWorldAsync(_realm), "r1")!);

    // -----------------------------------------------------------------
    // The bill
    // -----------------------------------------------------------------

    [Fact]
    public void TheBillGrowsWithLevelAndTier_AndWantsOreFromFour()
    {
        Assert.Equal(new[] { (Stone, 150), (Timber, 150) }, FortifyRules.CostFor(1, 1));
        Assert.Equal(new[] { (Stone, 600), (Timber, 600) }, FortifyRules.CostFor(2, 3));
        Assert.Equal(new[] { (Stone, 600), (Timber, 600), (Ore, 100) }, FortifyRules.CostFor(4, 1));
        Assert.Empty(FortifyRules.CostFor(6, 1));
    }

    // -----------------------------------------------------------------
    // Starting the works
    // -----------------------------------------------------------------

    [Fact]
    public async Task StartingPaysTheBill_AndMarksTheRegion()
    {
        await WorldAsync(entrenchment: 1);
        await GiveAsync(Stone, 400);
        await GiveAsync(Timber, 300);

        var outcome = await Fortify.BeginAsync(_realm, Player, "r1");

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(100, await HeldAsync(Stone));
        Assert.Equal(0, await HeldAsync(Timber));
        var region = await RegionAsync();
        Assert.Equal(1, region.Entrenchment);
        Assert.Equal(2, region.FortifyingTo);
        Assert.Equal((_now + FortifyRules.Duration).Ticks, region.FortifyEndsAtUtcTicks);
        Assert.Contains("WorldViewGameDataUpdated", _hub.MethodsSentTo(_realm));
    }

    [Fact]
    public async Task ShortOfGoods_NothingIsSpent()
    {
        await WorldAsync(entrenchment: 0);
        await GiveAsync(Stone, 150);
        await GiveAsync(Timber, 149);

        var outcome = await Fortify.BeginAsync(_realm, Player, "r1");

        Assert.Equal(FortifyError.CannotAfford, outcome.Error);
        Assert.Equal(150, await HeldAsync(Stone));
        Assert.Equal(0, (await RegionAsync()).FortifyingTo);
    }

    [Fact]
    public async Task ARivalsRegionCannotBeFortified()
    {
        await WorldAsync(owner: TestIds.Rival);
        await GiveAsync(Stone, 1000);
        await GiveAsync(Timber, 1000);

        var outcome = await Fortify.BeginAsync(_realm, Player, "r1");

        Assert.Equal(FortifyError.Refused, outcome.Error);
        Assert.Equal(1000, await HeldAsync(Stone));
    }

    [Fact]
    public async Task NobodyBuildsWithAnArmyAtTheGate()
    {
        await WorldAsync();
        await GiveAsync(Stone, 1000);
        await GiveAsync(Timber, 1000);
        _db.Sieges.Add(new Siege
        {
            GameInstanceId = _realm, AttackerUserId = TestIds.Rival, DefenderUserId = Player, RegionId = "r1",
            State = SiegeState.Mustering, DeclaredAt = _now, MusterEndsAt = _now.AddHours(8), AssaultEndsAt = _now.AddHours(12)
        });
        await _db.SaveChangesAsync();

        var outcome = await Fortify.BeginAsync(_realm, Player, "r1");

        Assert.Equal(FortifyError.Refused, outcome.Error);
        Assert.Contains("siege", outcome.Message);
        Assert.Equal(1000, await HeldAsync(Stone));
    }

    [Fact]
    public async Task OneWorksAtATime()
    {
        await WorldAsync(entrenchment: 1);
        await GiveAsync(Stone, 5000);
        await GiveAsync(Timber, 5000);
        await GiveAsync(Ore, 5000);

        Assert.True((await Fortify.BeginAsync(_realm, Player, "r1")).Succeeded);
        Assert.Equal(FortifyError.Refused, (await Fortify.BeginAsync(_realm, Player, "r1")).Error);
    }

    [Fact]
    public async Task NothingPastFive()
    {
        await WorldAsync(entrenchment: FortifyRules.MaxLevel);
        await GiveAsync(Stone, 5000);
        await GiveAsync(Timber, 5000);
        await GiveAsync(Ore, 5000);
        Assert.Equal(FortifyError.Refused, (await Fortify.BeginAsync(_realm, Player, "r1")).Error);
    }

    // -----------------------------------------------------------------
    // Finishing
    // -----------------------------------------------------------------

    [Fact]
    public async Task FinishedWorksRaiseTheWalls_AndReRateTheRealm()
    {
        await WorldAsync(entrenchment: 1);
        await GiveAsync(Stone, 300);
        await GiveAsync(Timber, 300);
        await Seasons.SettleAllAsync(_realm, at: _now);
        double before = (await _db.SeasonScores.FirstAsync(s => s.UserId == Player)).PointsPerHour;

        await Fortify.BeginAsync(_realm, Player, "r1");
        _now += FortifyRules.Duration - TimeSpan.FromMinutes(1);
        Assert.Equal(0, await Fortify.CompleteDueAsync(_realm));

        _now += TimeSpan.FromMinutes(2);
        Assert.Equal(1, await Fortify.CompleteDueAsync(_realm));

        var region = await RegionAsync();
        Assert.Equal(2, region.Entrenchment);
        Assert.Equal(0, region.FortifyingTo);
        Assert.True((await _db.SeasonScores.FirstAsync(s => s.UserId == Player)).PointsPerHour > before);
        Assert.True(await _db.WarLog.AnyAsync(e => e.Kind == WarLogKind.Fortified.ToString() && e.RegionId == "r1"));
    }

    [Fact]
    public async Task WorksOnLandThatChangedHandsAreCancelled()
    {
        await WorldAsync(entrenchment: 1);
        await GiveAsync(Stone, 300);
        await GiveAsync(Timber, 300);
        await Fortify.BeginAsync(_realm, Player, "r1");

        // Taken by a rival mid-works.
        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == _realm);
        var world = System.Text.Json.Nodes.JsonNode.Parse(row.GameData)!;
        WorldRegionBlob.CaptureRegion(WorldRegionBlob.FindRegion(world, "r1")!, TestIds.Rival, "Rival", _now);
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();

        _now += FortifyRules.Duration + TimeSpan.FromMinutes(1);
        await Fortify.CompleteDueAsync(_realm);

        var region = await RegionAsync();
        Assert.Equal(1, region.Entrenchment);
        Assert.Equal(0, region.FortifyingTo);
        Assert.Equal(FortificationState.Cancelled, (await _db.RegionFortifications.SingleAsync()).State);
    }
}
