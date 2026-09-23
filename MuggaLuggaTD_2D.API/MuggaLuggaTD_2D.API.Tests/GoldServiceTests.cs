using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using Enums;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The purse. Gold is the first currency here that accrues with <i>time</i> rather than only with
/// events, so what is pinned is the settlement: a balance is settled + rate x elapsed, banked
/// whenever anything touches it, and never advanced twice for the same stretch of time.
/// </summary>
public class GoldServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private static readonly Guid Realm = Guid.NewGuid();
    private const string Player = "player-1";

    private GoldService Service =>
        new(_db, new FakeSessionLog(), NullLogger<GoldService>.Instance);

    public void Dispose() => _db.Dispose();

    private static WorldRegionData Region(string owner, int tier = 1)
        => new()
        {
            RegionId = "r1",
            Tier = tier,
            Hex = new HexCoord(9, 9),
            Ownership = LocationOwnership.Player,
            OwnerUserId = owner
        };

    // -----------------------------------------------------------------
    // Grants
    // -----------------------------------------------------------------

    [Fact]
    public async Task AnEmptyPurseReadsAsZeroRatherThanFailing()
    {
        Assert.Equal(0, await Service.BalanceAsync(Realm, Player));
    }

    [Fact]
    public async Task GrantingAddsToWhatIsAlreadyHeld()
    {
        await Service.GrantAsync(Realm, Player, 120, "run-1");
        await Service.GrantAsync(Realm, Player, 80, "run-2");

        Assert.Equal(200, await Service.BalanceAsync(Realm, Player));
    }

    [Fact]
    public async Task GrantingNothingChangesNothing()
    {
        await Service.GrantAsync(Realm, Player, 50, "run-1");
        await Service.GrantAsync(Realm, Player, 0, "run-2");
        await Service.GrantAsync(Realm, Player, -999, "run-3");

        Assert.Equal(50, await Service.BalanceAsync(Realm, Player));
    }

    [Fact]
    public async Task OnePlayersGoldIsNotAnothers()
    {
        await Service.GrantAsync(Realm, Player, 500, "run-1");
        await Service.GrantAsync(Realm, "player-2", 10, "run-2");

        Assert.Equal(500, await Service.BalanceAsync(Realm, Player));
        Assert.Equal(10, await Service.BalanceAsync(Realm, "player-2"));
    }

    [Fact]
    public async Task GoldIsHeldPerRealm_SoASecondWorldStartsEmpty()
    {
        await Service.GrantAsync(Realm, Player, 900, "run-1");

        Assert.Equal(0, await Service.BalanceAsync(Guid.NewGuid(), Player));
    }

    // -----------------------------------------------------------------
    // Spending
    // -----------------------------------------------------------------

    [Fact]
    public async Task SpendingDeductsAndReportsTheRemainder()
    {
        await Service.GrantAsync(Realm, Player, 300, "run-1");

        var outcome = await Service.SpendAsync(Realm, Player, 120, "tavern-refresh");

        Assert.True(outcome.Succeeded);
        Assert.Equal(180, outcome.Balance);
        Assert.Equal(180, await Service.BalanceAsync(Realm, Player));
    }

    [Fact]
    public async Task SpendingMoreThanIsHeldTakesNothing()
    {
        await Service.GrantAsync(Realm, Player, 100, "run-1");

        var outcome = await Service.SpendAsync(Realm, Player, 101, "tavern-refresh");

        Assert.False(outcome.Succeeded);
        Assert.Equal(GoldError.InsufficientGold, outcome.Error);
        Assert.Equal(100, await Service.BalanceAsync(Realm, Player));
    }

    [Fact]
    public async Task SpendingFromAnEmptyPurseIsRefusedRatherThanGoingNegative()
    {
        var outcome = await Service.SpendAsync(Realm, Player, 1, "tavern-refresh");

        Assert.False(outcome.Succeeded);
        Assert.Equal(0, await Service.BalanceAsync(Realm, Player));
    }

    [Fact]
    public async Task SpendingNothingIsRefused()
    {
        Assert.Equal(GoldError.NothingToSpend, (await Service.SpendAsync(Realm, Player, 0, "x")).Error);
        Assert.Equal(GoldError.NothingToSpend, (await Service.SpendAsync(Realm, Player, -5, "x")).Error);
    }

    // -----------------------------------------------------------------
    // Accrual from land
    // -----------------------------------------------------------------

    [Fact]
    public async Task SettlingRatesAPlayerAgainstWhatTheyHold()
    {
        var regions = new[] { Region(Player) };
        var at = DateTime.UtcNow;

        await Service.SettleAllAsync(Realm, regions, new[] { Player }, at);

        var purse = await Service.ReadAsync(Realm, Player);

        Assert.NotNull(purse);
        Assert.Equal(GoldRules.RateFor(regions[0]), purse!.GoldPerHour, 6);
    }

    [Fact]
    public async Task LandPaysOverTimeWithoutAnythingTicking()
    {
        var regions = new[] { Region(Player) };
        var start = DateTime.UtcNow.AddHours(-10);

        // Rated ten hours ago, settled now: the whole ten hours is paid by arithmetic alone.
        await Service.SettleAllAsync(Realm, regions, new[] { Player }, start);
        await Service.SettleAllAsync(Realm, regions, new[] { Player }, start.AddHours(10));

        long expected = (long)Math.Floor(GoldRules.RateFor(regions[0]) * 10);

        Assert.Equal(expected, await Service.BalanceAsync(Realm, Player, start.AddHours(10)));
    }

    [Fact]
    public async Task SettlingTwiceAtTheSameInstantPaysOnce()
    {
        var regions = new[] { Region(Player) };
        var start = DateTime.UtcNow.AddHours(-4);
        var now = start.AddHours(4);

        await Service.SettleAllAsync(Realm, regions, new[] { Player }, start);
        await Service.SettleAllAsync(Realm, regions, new[] { Player }, now);
        await Service.SettleAllAsync(Realm, regions, new[] { Player }, now);

        long expected = (long)Math.Floor(GoldRules.RateFor(regions[0]) * 4);

        Assert.Equal(expected, await Service.BalanceAsync(Realm, Player, now));
    }

    [Fact]
    public async Task LosingLandStopsTheIncomeButKeepsWhatItPaid()
    {
        var mine = Region(Player);
        var start = DateTime.UtcNow.AddHours(-5);
        var lost = start.AddHours(5);

        await Service.SettleAllAsync(Realm, new[] { mine }, new[] { Player }, start);

        // The region changes hands, and the realm is settled at the old rate before being re-rated.
        var taken = Region("player-2");
        await Service.SettleAllAsync(Realm, new[] { taken }, new[] { Player }, lost);

        long earned = (long)Math.Floor(GoldRules.RateFor(mine) * 5);

        Assert.Equal(earned, await Service.BalanceAsync(Realm, Player, lost));

        // And nothing more accrues afterwards, because the rate is now zero.
        Assert.Equal(earned, await Service.BalanceAsync(Realm, Player, lost.AddHours(20)));
    }

    [Fact]
    public async Task ReadingABalanceDoesNotBankIt()
    {
        // Reading the scoreboard must never be what makes it correct - the same rule the season
        // standings follow. A projected read leaves the stored settlement alone.
        var regions = new[] { Region(Player) };
        var start = DateTime.UtcNow.AddHours(-3);

        await Service.SettleAllAsync(Realm, regions, new[] { Player }, start);

        long projected = await Service.BalanceAsync(Realm, Player, start.AddHours(3));
        var purse = await Service.ReadAsync(Realm, Player);

        Assert.True(projected > 0);
        Assert.Equal(0, purse!.SettledGold, 6);
        Assert.Equal(start, purse.LastSettledAt);
    }

    [Fact]
    public async Task AGrantBanksWhatTheLandOwedFirst()
    {
        var regions = new[] { Region(Player) };

        // Rated two hours ago; the grant should land on top of those two hours, not replace them.
        await Service.SettleAllAsync(Realm, regions, new[] { Player }, DateTime.UtcNow.AddHours(-2));

        long balance = await Service.GrantAsync(Realm, Player, 100, "pve-claim");

        long fromLand = (long)Math.Floor(GoldRules.RateFor(regions[0]) * 2);

        Assert.True(balance >= 100 + fromLand - 1, $"balance {balance} should include ~{fromLand} from land");
    }
}
