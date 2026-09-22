using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Hubs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// The scoreboard, and the end of a season (see <c>docs/design/seasons-and-scoring.md</c>).
///
/// <para>A realm runs for a season its creator sized, and the player with the most points when it
/// closes wins. This service owns every part of that: what a player is earning, what they have
/// banked, where they stand, and what happens when the time is up.</para>
///
/// <para><b>Nothing ticks.</b> A score is settled points plus a rate plus when that rate started, so
/// the points a player has now are arithmetic rather than the sum of ticks that may or may not have
/// run. Every path that can change what somebody holds settles everybody up at the old rate first
/// and then stores the new one. There is no scheduler to fail silently and take the scoreboard with
/// it, and a player offline for a week earns exactly what they should.</para>
///
/// <para>Season end is detected the same lazy way: the first request to arrive after the closing
/// time closes the season. Because accrual is clamped to that moment, it does not matter whether
/// that request comes a second or a day later - the standings are the same either way.</para>
/// </summary>
public class SeasonScoreService
{
    /// <summary>
    /// One closing at a time per realm. Season end is reached by whichever request happens to arrive
    /// first, and two arriving together would otherwise both close it and reset the world twice.
    ///
    /// <para>This is per-process, which is the honest scope of it: a second API instance would need a
    /// database-level claim instead. The game does not run that way yet, and pretending otherwise
    /// with a more elaborate mechanism would buy nothing real.</para>
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ClosingLocks = new();

    private readonly ApplicationDbContext _context;
    private readonly WorldProvisioningService _worlds;
    private readonly IHubContext<GameHub> _hubContext;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<SeasonScoreService> _logger;

    public SeasonScoreService(
        ApplicationDbContext context,
        WorldProvisioningService worlds,
        IHubContext<GameHub> hubContext,
        ISessionLog sessionLog,
        ILogger<SeasonScoreService> logger)
    {
        _context = context;
        _worlds = worlds;
        _hubContext = hubContext;
        _sessionLog = sessionLog;
        _logger = logger;
    }

    // -----------------------------------------------------------------
    // Settling up
    // -----------------------------------------------------------------

    /// <summary>
    /// Brings every player in the realm up to date and re-rates them against the world as it stands.
    ///
    /// <para>Called after anything that could change who holds what - a capture, a seat, a reset. It
    /// is cheap enough to call on the whole realm rather than reasoning about who was affected,
    /// which is deliberate: a capture changes the loser's rate as much as the winner's, and working
    /// out the difference correctly every time is a bug waiting to happen.</para>
    /// </summary>
    public async Task SettleAllAsync(Guid gameInstanceId, JsonNode? world = null, DateTime? at = null)
    {
        var instance = await _context.GameInstances
            .Include(g => g.PlayerGameData)
            .FirstOrDefaultAsync(g => g.Id == gameInstanceId);

        if (instance == null) return;

        world ??= await LoadWorldAsync(gameInstanceId);
        var regions = WorldRegionBlob.ReadAllRegions(world);

        var until = SeasonScoreRules.AccrueUntil(at ?? DateTime.UtcNow, instance.SeasonEndsAt);
        var scores = await ScoresForAsync(instance, MembersOf(instance, regions), until);

        foreach (var score in scores.Values)
        {
            Settle(score, until);
            score.PointsPerHour = SeasonScoreRules.RateForHoldings(score.UserId, regions);
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Pays a player for something they did, having first brought the whole realm up to date so the
    /// lump lands on top of a settled score rather than being swept up by the next settle.
    /// </summary>
    public async Task AwardAsync(Guid gameInstanceId, string userId, SeasonDeed deed, JsonNode? world = null)
    {
        double points = SeasonScoreRules.PointsFor(deed);
        if (points <= 0 || string.IsNullOrEmpty(userId)) return;

        await SettleAllAsync(gameInstanceId, world);

        var instance = await _context.GameInstances.FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        if (instance == null) return;

        // Past the closing time nothing more is earned. Without this a deed done after the bell -
        // by a client that had not yet been told - would change a settled result.
        if (instance.SeasonHasExpired(DateTime.UtcNow)) return;

        var score = await FindOrCreateAsync(instance, userId, DateTime.UtcNow);

        score.SettledPoints += points;
        if (deed == SeasonDeed.SiteCleared) score.ClearingPoints += points;
        else score.RaidingPoints += points;

        await _context.SaveChangesAsync();

        _sessionLog.Log("SEASON-DEED",
            $"instance={gameInstanceId} user={userId} deed={deed} points={points:F0} total={score.SettledPoints:F0}");
    }

    // -----------------------------------------------------------------
    // Standings
    // -----------------------------------------------------------------

    /// <summary>
    /// The table as it stands right now, projected forward from each player's settled score without
    /// writing anything. Reading the scoreboard must never be what makes it correct.
    /// </summary>
    public async Task<SeasonStandingsResponse> StandingsAsync(Guid gameInstanceId)
    {
        var instance = await _context.GameInstances
            .Include(g => g.PlayerGameData)
            .FirstOrDefaultAsync(g => g.Id == gameInstanceId);

        if (instance == null)
        {
            return new SeasonStandingsResponse(
                gameInstanceId, 0, DateTime.UtcNow, DateTime.UtcNow, 0, new List<SeasonStandingEntry>());
        }

        var world = await LoadWorldAsync(gameInstanceId);
        var regions = WorldRegionBlob.ReadAllRegions(world);
        var until = SeasonScoreRules.AccrueUntil(DateTime.UtcNow, instance.SeasonEndsAt);

        var scores = await _context.SeasonScores
            .Where(s => s.GameInstanceId == gameInstanceId && s.SeasonNumber == instance.SeasonNumber)
            .ToListAsync();

        var members = MembersOf(instance, regions);
        var names = await DisplayNamesAsync(members);

        var rows = new List<StandingRow>();

        foreach (var userId in members)
        {
            var score = scores.FirstOrDefault(s => s.UserId == userId)
                        ?? new SeasonScore { UserId = userId, LastSettledAt = until };

            // Projected, not stored: what they would have if they were settled this instant.
            double pending = SeasonScoreRules.Accrued(score.PointsPerHour, score.LastSettledAt, until);

            rows.Add(new StandingRow(
                userId,
                score.SettledPoints + pending,
                score.HoldingPoints + pending,
                score.ClearingPoints,
                score.RaidingPoints,
                SeasonScoreRules.RateForHoldings(userId, regions),
                regions.Count(r => r.IsOwnedByPlayer(userId))));
        }

        var ordered = rows.OrderByDescending(r => r.Total).ToList();
        var entries = new List<SeasonStandingEntry>();

        for (int i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];

            // A tie shares a rank, as it does in any table.
            int rank = i > 0 && Math.Abs(ordered[i - 1].Total - row.Total) < 0.0001
                ? entries[i - 1].Rank
                : i + 1;

            entries.Add(new SeasonStandingEntry(
                rank,
                row.UserId,
                names.TryGetValue(row.UserId, out var name) ? name : "Unknown",
                Math.Round(row.Total, 1),
                Math.Round(row.Holding, 1),
                Math.Round(row.Clearing, 1),
                Math.Round(row.Raiding, 1),
                Math.Round(row.Rate, 1),
                row.RegionsHeld));
        }

        return new SeasonStandingsResponse(
            gameInstanceId,
            instance.SeasonNumber,
            instance.SeasonStartedAt,
            instance.SeasonEndsAt,
            instance.SeasonLengthDays,
            entries);
    }

    private record StandingRow(
        string UserId, double Total, double Holding, double Clearing, double Raiding, double Rate, int RegionsHeld);

    /// <summary>What a player has already won, anywhere. The carry-over's data, waiting for a use.</summary>
    public async Task<IReadOnlyList<SeasonResultEntry>> HistoryForUserAsync(string userId, int limit = 20)
    {
        var results = await _context.SeasonResults
            .Include(r => r.User)
            .Include(r => r.GameInstance)
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.SeasonEndedAt)
            .Take(limit)
            .ToListAsync();

        return results.Select(ToEntry).ToList();
    }

    /// <summary>The final table of a season that has already closed.</summary>
    public async Task<IReadOnlyList<SeasonResultEntry>> ResultsAsync(Guid gameInstanceId, int seasonNumber)
    {
        var results = await _context.SeasonResults
            .Include(r => r.User)
            .Where(r => r.GameInstanceId == gameInstanceId && r.SeasonNumber == seasonNumber)
            .OrderBy(r => r.Rank)
            .ToListAsync();

        return results.Select(ToEntry).ToList();
    }

    // -----------------------------------------------------------------
    // Ending, and beginning again
    // -----------------------------------------------------------------

    /// <summary>
    /// Closes the season if its time is up, and starts the next one. Returns true if it did.
    ///
    /// <para>Cheap on the common path - one instance read and a date comparison - because every
    /// world request calls it. That is the point: without a scheduler, arriving traffic is what
    /// notices the bell, and the arithmetic is clamped to the closing time so a late arrival gets
    /// the same answer an on-time one would have.</para>
    /// </summary>
    public async Task<bool> EnsureSeasonCurrentAsync(Guid gameInstanceId)
    {
        var instance = await _context.GameInstances.FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        if (instance == null || !instance.SeasonHasExpired(DateTime.UtcNow)) return false;

        var gate = ClosingLocks.GetOrAdd(gameInstanceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try
        {
            // Re-read inside the gate: another request may have closed it while we waited, in which
            // case the instance now points at a fresh season and there is nothing to do.
            await _context.Entry(instance).ReloadAsync();
            if (!instance.SeasonHasExpired(DateTime.UtcNow)) return false;

            await CloseSeasonAsync(instance);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CloseSeasonAsync(GameInstance instance)
    {
        int closing = instance.SeasonNumber;
        var startedAt = instance.SeasonStartedAt;
        var endedAt = instance.SeasonEndsAt;

        // Settle everyone to the exact closing moment, so the final table is the one anybody would
        // have computed at the bell rather than the one whoever happened to arrive first sees.
        var world = await LoadWorldAsync(instance.Id);
        await SettleAllAsync(instance.Id, world, endedAt);

        var regions = WorldRegionBlob.ReadAllRegions(world);

        var scores = await _context.SeasonScores
            .Where(s => s.GameInstanceId == instance.Id && s.SeasonNumber == closing)
            .ToListAsync();

        bool alreadyRecorded = await _context.SeasonResults
            .AnyAsync(r => r.GameInstanceId == instance.Id && r.SeasonNumber == closing);

        if (!alreadyRecorded)
        {
            var ordered = scores.OrderByDescending(s => s.SettledPoints).ToList();
            var written = new List<SeasonResult>();

            for (int i = 0; i < ordered.Count; i++)
            {
                var score = ordered[i];
                int rank = i > 0 && Math.Abs(ordered[i - 1].SettledPoints - score.SettledPoints) < 0.0001
                    ? written[i - 1].Rank
                    : i + 1;

                var result = new SeasonResult
                {
                    GameInstanceId = instance.Id,
                    UserId = score.UserId,
                    SeasonNumber = closing,
                    Rank = rank,
                    TotalPoints = Math.Round(score.SettledPoints, 1),
                    HoldingPoints = Math.Round(score.HoldingPoints, 1),
                    ClearingPoints = Math.Round(score.ClearingPoints, 1),
                    RaidingPoints = Math.Round(score.RaidingPoints, 1),
                    RegionsHeld = regions.Count(r => r.IsOwnedByPlayer(score.UserId)),
                    SeasonStartedAt = startedAt,
                    SeasonEndedAt = endedAt
                };

                written.Add(result);
                _context.SeasonResults.Add(result);

                _sessionLog.Log("SEASON-END",
                    $"instance={instance.Id} season={closing} rank={rank} user={score.UserId} " +
                    $"points={result.TotalPoints:F0} (hold={result.HoldingPoints:F0} " +
                    $"clear={result.ClearingPoints:F0} raid={result.RaidingPoints:F0}) " +
                    $"regions={result.RegionsHeld}");
            }

            _logger.LogInformation("Season {Season} of instance {Instance} closed with {Count} standing(s).",
                closing, instance.Id, written.Count);
        }

        // The realm begins again rather than stopping: same players, same rosters, new map. Runs, raid
        // cooldowns and sieges referred to the world that just ended, so they go with it.
        instance.SeasonNumber = closing + 1;
        instance.SeasonStartedAt = DateTime.UtcNow;
        instance.UpdatedAt = DateTime.UtcNow;

        _context.PveRuns.RemoveRange(_context.PveRuns.Where(r => r.GameInstanceId == instance.Id));
        _context.RegionRaids.RemoveRange(_context.RegionRaids.Where(r => r.GameInstanceId == instance.Id));
        _context.Sieges.RemoveRange(_context.Sieges.Where(s => s.GameInstanceId == instance.Id));

        await _context.SaveChangesAsync();

        var fresh = await _worlds.RegenerateWorldAsync(instance.Id);

        // Everyone starts the new season at zero, rated against the map they were just seated in.
        await SettleAllAsync(instance.Id, JsonNode.Parse(fresh.GameData), DateTime.UtcNow);

        await BroadcastSeasonEndedAsync(instance, closing, fresh);
    }

    private async Task BroadcastSeasonEndedAsync(GameInstance instance, int closedSeason, WorldViewGameData world)
    {
        var finalTable = await ResultsAsync(instance.Id, closedSeason);

        await _hubContext.Clients.Group(instance.Id.ToString())
            .SendAsync("SeasonEnded", new SeasonEndedNotification(
                instance.Id, closedSeason, instance.SeasonNumber, instance.SeasonEndsAt, finalTable));

        // And the map itself changed under everyone, so the ordinary world broadcast follows.
        var payload = JsonSerializer.Deserialize<object>(world.GameData) ?? new { };
        await _hubContext.Clients.Group(instance.Id.ToString())
            .SendAsync("WorldViewGameDataUpdated", new WorldViewGameDataUpdated(instance.Id, payload, world.UpdatedAt));
    }

    // -----------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------

    private static void Settle(SeasonScore score, DateTime until)
    {
        if (until <= score.LastSettledAt) return;

        double accrued = SeasonScoreRules.Accrued(score.PointsPerHour, score.LastSettledAt, until);
        score.SettledPoints += accrued;
        score.HoldingPoints += accrued;
        score.LastSettledAt = until;
    }

    /// <summary>
    /// Everyone the scoreboard should have a row for: the realm's members, plus anybody holding
    /// ground in it. The second half matters because a world is generated with its members seated,
    /// which can happen before their player data row exists.
    /// </summary>
    private static List<string> MembersOf(GameInstance instance, IEnumerable<WorldRegionData> regions)
    {
        var members = new List<string>();

        void Add(string? userId)
        {
            if (!string.IsNullOrEmpty(userId) && !members.Contains(userId, StringComparer.Ordinal))
                members.Add(userId);
        }

        Add(instance.OwnerId);

        foreach (var player in instance.PlayerGameData ?? Enumerable.Empty<PlayerGameData>())
            Add(player.UserId);

        foreach (var region in regions)
        {
            if (region.Ownership == LocationOwnership.Player)
                Add(region.OwnerUserId);
        }

        return members;
    }

    private async Task<Dictionary<string, SeasonScore>> ScoresForAsync(
        GameInstance instance, IReadOnlyList<string> userIds, DateTime createdAt)
    {
        var existing = await _context.SeasonScores
            .Where(s => s.GameInstanceId == instance.Id && s.SeasonNumber == instance.SeasonNumber)
            .ToListAsync();

        var byUser = existing.ToDictionary(s => s.UserId, StringComparer.Ordinal);

        foreach (var userId in userIds)
        {
            if (byUser.ContainsKey(userId)) continue;

            var score = NewScore(instance, userId, createdAt);
            _context.SeasonScores.Add(score);
            byUser[userId] = score;
        }

        return byUser;
    }

    private async Task<SeasonScore> FindOrCreateAsync(GameInstance instance, string userId, DateTime createdAt)
    {
        var score = await _context.SeasonScores.FirstOrDefaultAsync(s =>
            s.GameInstanceId == instance.Id && s.UserId == userId && s.SeasonNumber == instance.SeasonNumber);

        if (score != null) return score;

        score = NewScore(instance, userId, createdAt);
        _context.SeasonScores.Add(score);
        return score;
    }

    private static SeasonScore NewScore(GameInstance instance, string userId, DateTime createdAt)
    {
        // A player joining mid-season starts earning from when they arrived, not from the season's
        // opening - they did not hold anything before that.
        var from = createdAt < instance.SeasonStartedAt ? instance.SeasonStartedAt : createdAt;

        return new SeasonScore
        {
            GameInstanceId = instance.Id,
            UserId = userId,
            SeasonNumber = instance.SeasonNumber,
            LastSettledAt = from
        };
    }

    private async Task<JsonNode?> LoadWorldAsync(Guid gameInstanceId)
    {
        var row = await _context.WorldViewGameData
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);

        return row == null ? null : JsonNode.Parse(row.GameData);
    }

    private async Task<Dictionary<string, string>> DisplayNamesAsync(IReadOnlyList<string> userIds)
    {
        var users = await _context.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.UserName })
            .ToListAsync();

        return users.ToDictionary(u => u.Id, u => u.UserName ?? "Unknown", StringComparer.Ordinal);
    }

    private static SeasonResultEntry ToEntry(SeasonResult result) => new(
        result.SeasonNumber,
        result.Rank,
        result.UserId,
        result.User?.UserName ?? "Unknown",
        result.TotalPoints,
        result.HoldingPoints,
        result.ClearingPoints,
        result.RaidingPoints,
        result.RegionsHeld,
        result.SeasonStartedAt,
        result.SeasonEndedAt,
        result.GameInstance?.Name);
}
