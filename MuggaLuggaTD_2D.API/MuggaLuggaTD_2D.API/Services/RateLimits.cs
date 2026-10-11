using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Rate limits (Hardening 8; Mike 2026-10-10: yes, with the measured numbers). Each player has two token
/// buckets:
/// <list type="bullet">
/// <item><b>general:</b> <see cref="GeneralTokens"/> requests per <see cref="Period"/>. Entering a realm is
/// the busiest moment, at about 24 requests in a few seconds, so this leaves about 5× headroom.</item>
/// <item><b>trade:</b> <see cref="TradeTokens"/> per period, for writes to the Bazaar, quests and hiring. That's
/// far beyond a person clicking.</item>
/// </list>
/// Saves, content, login, the hub and the version check are never limited: a save refused for speed
/// could lose progress, and those routes are cheap. A player is keyed by their user id, or by address
/// before they log in.
///
/// <para>Every refusal is tallied (<see cref="RateLimitTally"/>), so the limits can be reviewed after a
/// few weeks of play (Mike: "ensure we aren't over-throwing"). Read the tally with
/// <c>dotnet run -- rate-limits</c>.</para>
/// </summary>
public static class RateLimits
{
    public const string Command = "rate-limits";

    public const int GeneralTokens = 120;
    public const int TradeTokens = 20;
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(10);

    public const string Refused = "Slow down a moment.";

    private static readonly string[] TradeAreas = { "/bazaar", "/quests", "/hiring" };

    /// <summary>Routes never limited: content, login and register, saves, the hub, the version check, health.</summary>
    public static bool IsExempt(HttpRequest request)
    {
        string path = request.Path.Value?.ToLowerInvariant() ?? "";
        if (path.StartsWith("/api/content") || path.StartsWith("/api/auth/login") || path.StartsWith("/api/auth/register") ||
            path.StartsWith("/hubs/") || path.StartsWith("/api/client") || path.StartsWith("/healthz"))
            return true;
        return HttpMethods.IsPost(request.Method) && path.EndsWith("/playerdata/me");
    }

    /// <summary>A write to the Bazaar, quests or hiring: it also spends from the trade bucket.</summary>
    public static bool IsTrade(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) return false;
        string path = request.Path.Value?.ToLowerInvariant() ?? "";
        return TradeAreas.Any(a => path.Contains(a));
    }

    /// <summary>Whose buckets a request spends from: the player, or the address before login.</summary>
    public static string KeyOf(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    private static readonly Regex Ids = new(
        @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|(?<=/)\d+(?=/|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A path with its ids taken out, so refusals of one route add up: "/api/gameinstance/{id}/bazaar/buy".</summary>
    public static string RouteOf(string? path) => Ids.Replace((path ?? "").ToLowerInvariant(), "{id}");

    /// <summary>The limiter: every request spends from its player's general bucket, and a trade from the trade bucket too.</summary>
    public static PartitionedRateLimiter<HttpContext> Create(int generalTokens = GeneralTokens, int tradeTokens = TradeTokens)
    {
        var general = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            IsExempt(context.Request)
                ? RateLimitPartition.GetNoLimiter("exempt")
                : RateLimitPartition.GetTokenBucketLimiter("g:" + KeyOf(context), _ => Bucket(generalTokens)));

        var trade = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            IsExempt(context.Request) || !IsTrade(context.Request)
                ? RateLimitPartition.GetNoLimiter("not-trade")
                : RateLimitPartition.GetTokenBucketLimiter("t:" + KeyOf(context), _ => Bucket(tradeTokens)));

        return PartitionedRateLimiter.CreateChained(general, trade);
    }

    private static TokenBucketRateLimiterOptions Bucket(int tokens) => new()
    {
        TokenLimit = tokens,
        TokensPerPeriod = tokens,
        ReplenishmentPeriod = Period,
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    /// <summary>Prints the tally: refusals by day and route, and how many players met each.</summary>
    public static async Task<int> RunFromCommandLineAsync(IServiceProvider services, string[] args)
    {
        int days = 14;
        int i = Array.IndexOf(args, "--days");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int d)) days = Math.Max(1, d);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        int since = RateLimitTally.DayOf(DateTime.UtcNow.AddDays(-days));
        var rows = await context.RateLimitRefusals.AsNoTracking().Where(r => r.Day >= since).ToListAsync();

        Console.WriteLine($"Rate-limit refusals, last {days} day(s) (limits: general {GeneralTokens}, trade {TradeTokens} per {Period.TotalSeconds:0} s):");
        foreach (var group in rows.GroupBy(r => (r.Day, r.Policy, r.Route)).OrderBy(g => g.Key.Day).ThenByDescending(g => g.Sum(r => r.Count)))
            Console.WriteLine($"  {group.Key.Day}  {group.Key.Policy,-7}  {group.Sum(r => r.Count),6} refused  {group.Count(),3} player(s)  {group.Key.Route}");
        Console.WriteLine(rows.Count == 0 ? "  None." : $"  {rows.Sum(r => r.Count)} in all.");
        return 0;
    }
}

/// <summary>
/// Counts refusals in memory and writes them to <see cref="RateLimitRefusal"/> rows once a minute (on the
/// scheduler's sweep), so a flood of refusals is not also a flood of database writes.
/// </summary>
public class RateLimitTally
{
    private readonly ConcurrentDictionary<(int Day, string Policy, string Route, string Who), int> _pending = new();

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public static int DayOf(DateTime at) => at.Year * 10000 + at.Month * 100 + at.Day;

    public void Add(HttpContext context)
    {
        string policy = RateLimits.IsTrade(context.Request) ? "trade" : "general";
        var key = (DayOf(Clock()), policy, RateLimits.RouteOf(context.Request.Path.Value), RateLimits.KeyOf(context));
        _pending.AddOrUpdate(key, 1, (_, n) => n + 1);
    }

    public int Pending => _pending.Values.Sum();

    /// <summary>Adds what was counted since the last flush to the day's rows. Returns how many refusals it wrote.</summary>
    public async Task<int> FlushAsync(ApplicationDbContext context)
    {
        var taken = new List<((int Day, string Policy, string Route, string Who) Key, int Count)>();
        foreach (var key in _pending.Keys.ToList())
            if (_pending.TryRemove(key, out int n) && n > 0) taken.Add((key, n));
        if (taken.Count == 0) return 0;

        var now = Clock();
        foreach (var (key, count) in taken)
        {
            string route = key.Route.Length > 200 ? key.Route[..200] : key.Route;
            var row = context.RateLimitRefusals.Local.FirstOrDefault(r => r.Day == key.Day && r.Policy == key.Policy && r.Route == route && r.Who == key.Who)
                ?? await context.RateLimitRefusals.FirstOrDefaultAsync(r => r.Day == key.Day && r.Policy == key.Policy && r.Route == route && r.Who == key.Who);
            if (row == null)
            {
                row = new RateLimitRefusal { Day = key.Day, Policy = key.Policy, Route = route, Who = key.Who };
                context.RateLimitRefusals.Add(row);
            }
            row.Count += count;
            row.LastAt = now;
        }
        await context.SaveChangesAsync();
        return taken.Sum(t => t.Count);
    }
}
