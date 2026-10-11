using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Rate limits (Hardening 8). What a player should be able to rely on: playing normally is never refused, a
/// save never is, one player's flood does not touch another, and every refusal is counted for review.
/// </summary>
public class RateLimitTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();

    public void Dispose() => _db.Dispose();

    private static HttpContext Request(string method, string path, string? user = "user-a")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (user != null)
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user) }, "test"));
        return context;
    }

    private static int Admitted(PartitionedRateLimiter<HttpContext> limiter, Func<HttpContext> request, int tries)
    {
        int admitted = 0;
        for (int i = 0; i < tries; i++)
        {
            using var lease = limiter.AttemptAcquire(request());
            if (lease.IsAcquired) admitted++;
        }
        return admitted;
    }

    private const string Realm = "/api/gameinstance/5761cee3-569f-46bb-bf1a-1c48e85622e6";

    [Theory]
    [InlineData("GET", "/api/content")]
    [InlineData("POST", "/api/auth/login")]
    [InlineData("POST", Realm + "/playerdata/me")]
    [InlineData("POST", "/hubs/game/negotiate")]
    [InlineData("GET", "/api/client/version")]
    public void SavesContentLoginAndTheHub_AreNeverLimited(string method, string path) =>
        Assert.True(RateLimits.IsExempt(Request(method, path).Request));

    [Theory]
    [InlineData("POST", Realm + "/bazaar/buy", true)]
    [InlineData("POST", Realm + "/quests/accept", true)]
    [InlineData("POST", Realm + "/hiring/hire", true)]
    [InlineData("GET", Realm + "/bazaar", false)]
    [InlineData("POST", Realm + "/parties/travel", false)]
    public void TradesAreWritesToTheBazaarQuestsAndHiring(string method, string path, bool trade) =>
        Assert.Equal(trade, RateLimits.IsTrade(Request(method, path).Request));

    [Fact]
    public void ARoute_IsCountedWithoutItsIds() =>
        Assert.Equal("/api/gameinstance/{id}/rifts/{id}/chest",
            RateLimits.RouteOf(Realm + "/rifts/0b7259b9-5dfc-439c-a046-1c7fc0d45285/chest"));

    [Fact]
    public void APlayer_HasTheGeneralBucket_AndNoMore()
    {
        using var limiter = RateLimits.Create();
        Assert.Equal(RateLimits.GeneralTokens, Admitted(limiter, () => Request("GET", Realm + "/quests"), RateLimits.GeneralTokens + 30));
    }

    [Fact]
    public void OnePlayersFlood_DoesNotTouchAnother()
    {
        using var limiter = RateLimits.Create();
        Admitted(limiter, () => Request("GET", Realm + "/quests", "flooder"), 500);
        Assert.Equal(10, Admitted(limiter, () => Request("GET", Realm + "/quests", "neighbour"), 10));
    }

    [Fact]
    public void Trades_HaveTheirOwnSmallerBucket_AndOtherRequestsStillGoThrough()
    {
        using var limiter = RateLimits.Create();
        Assert.Equal(RateLimits.TradeTokens, Admitted(limiter, () => Request("POST", Realm + "/bazaar/buy"), 50));
        Assert.Equal(10, Admitted(limiter, () => Request("GET", Realm + "/bazaar"), 10));
    }

    [Fact]
    public void AnExemptRoute_GoesThrough_HoweverBusy()
    {
        using var limiter = RateLimits.Create();
        Admitted(limiter, () => Request("GET", Realm + "/quests"), 500);
        Assert.Equal(300, Admitted(limiter, () => Request("POST", Realm + "/playerdata/me"), 300));
    }

    [Fact]
    public void BeforeLogin_APlayerIsKeyedByAddress()
    {
        var context = Request("POST", "/api/auth/change-password", user: null);
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.1.2.3");
        Assert.Equal("ip:10.1.2.3", RateLimits.KeyOf(context));
    }

    [Fact]
    public async Task EveryRefusal_IsCounted_ByDayRouteAndPlayer()
    {
        var tally = new RateLimitTally { Clock = () => new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc) };
        for (int i = 0; i < 3; i++) tally.Add(Request("POST", Realm + "/bazaar/buy", "a"));
        tally.Add(Request("GET", Realm + "/quests", "b"));

        Assert.Equal(4, await tally.FlushAsync(_db));
        Assert.Equal(0, await tally.FlushAsync(_db));

        tally.Add(Request("POST", Realm + "/bazaar/buy", "a"));
        await tally.FlushAsync(_db);

        var rows = await _db.RateLimitRefusals.AsNoTracking().ToListAsync();
        var trade = Assert.Single(rows, r => r.Policy == "trade");
        Assert.Equal(4, trade.Count);
        Assert.Equal(20261010, trade.Day);
        Assert.Equal("/api/gameinstance/{id}/bazaar/buy", trade.Route);
        Assert.Single(rows, r => r.Policy == "general" && r.Who == "b");
    }
}
