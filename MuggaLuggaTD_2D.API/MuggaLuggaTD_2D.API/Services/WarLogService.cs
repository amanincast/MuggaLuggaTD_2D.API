using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Hubs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Writes and reads a realm's war log.
///
/// <para>Recording never fails the action it records: a raid that landed has landed whether or not
/// its log line could be written, so a failure here is logged and swallowed.</para>
/// </summary>
public class WarLogService
{
    /// <summary>The most a single read returns. The log is for catching up, not for archaeology.</summary>
    public const int MaximumRead = 100;

    private readonly ApplicationDbContext _context;
    private readonly IHubContext<GameHub> _hubContext;
    private readonly ILogger<WarLogService> _logger;
    private readonly TimeProvider _clock;

    public WarLogService(
        ApplicationDbContext context, IHubContext<GameHub> hubContext, ILogger<WarLogService> logger, TimeProvider clock)
    {
        _context = context;
        _hubContext = hubContext;
        _logger = logger;
        _clock = clock;
    }

    public async Task RecordAsync(
        Guid gameInstanceId,
        WarLogKind kind,
        string? actorUserId,
        string? subjectUserId,
        string? regionId,
        string? detail = null,
        DateTime? at = null)
    {
        WarLogEntry? entry = null;
        try
        {
            var season = await _context.GameInstances.AsNoTracking()
                .Where(g => g.Id == gameInstanceId)
                .Select(g => (int?)g.SeasonNumber)
                .FirstOrDefaultAsync();
            if (season == null) return;

            entry = new WarLogEntry
            {
                GameInstanceId = gameInstanceId,
                SeasonNumber = season.Value,
                OccurredAt = at ?? _clock.GetUtcNow().UtcDateTime,
                RecordedTicks = NextRecordedTicks(),
                Kind = kind.ToString(),
                ActorUserId = actorUserId,
                ActorName = await NameOfAsync(actorUserId),
                SubjectUserId = subjectUserId,
                SubjectName = await NameOfAsync(subjectUserId),
                RegionId = regionId,
                Detail = detail
            };

            _context.WarLog.Add(entry);
            await _context.SaveChangesAsync();

            await _hubContext.Clients.Group(gameInstanceId.ToString())
                .SendAsync("WarLogEntryAdded", ToResponse(entry));
        }
        catch (Exception ex)
        {
            // Do not leave a failed line tracked, or the next unrelated save would retry - and fail - it.
            if (entry != null) _context.Entry(entry).State = EntityState.Detached;
            _logger.LogWarning(ex, "Could not record war log {Kind} in instance {Instance}.", kind, gameInstanceId);
        }
    }

    /// <summary>The current season's log, newest first.</summary>
    public async Task<List<WarLogEntryResponse>> ReadAsync(Guid gameInstanceId, int limit = 50)
    {
        var season = await _context.GameInstances.AsNoTracking()
            .Where(g => g.Id == gameInstanceId)
            .Select(g => g.SeasonNumber)
            .FirstOrDefaultAsync();

        var entries = await _context.WarLog.AsNoTracking()
            .Where(e => e.GameInstanceId == gameInstanceId && e.SeasonNumber == season)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.RecordedTicks)
            .Take(Math.Clamp(limit, 1, MaximumRead))
            .ToListAsync();

        return entries.Select(ToResponse).ToList();
    }

    private static long _lastRecordedTicks;

    /// <summary>
    /// The real clock's ticks, but strictly increasing across this process, so two lines written
    /// within the same tick still sort in the order they were written.
    /// </summary>
    private static long NextRecordedTicks()
    {
        while (true)
        {
            long last = Interlocked.Read(ref _lastRecordedTicks);
            long next = Math.Max(DateTime.UtcNow.Ticks, last + 1);
            if (Interlocked.CompareExchange(ref _lastRecordedTicks, next, last) == last)
                return next;
        }
    }

    private async Task<string?> NameOfAsync(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;

        var user = await _context.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.UserName })
            .FirstOrDefaultAsync();

        var name = user?.DisplayName ?? user?.UserName;
        return name != null && name.Length > 64 ? name[..64] : name;
    }

    private static WarLogEntryResponse ToResponse(WarLogEntry e) => new(
        e.Id, e.OccurredAt, e.Kind, e.ActorUserId, e.ActorName, e.SubjectUserId, e.SubjectName, e.RegionId, e.Detail);
}
