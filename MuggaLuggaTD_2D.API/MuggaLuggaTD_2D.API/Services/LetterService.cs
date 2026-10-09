using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// Writes and reads players' letters (the Inbox spec). A letter is personal: what happened to this
/// player in this realm, with an action. The realm's news stays in the war log.
///
/// <para><b>Sending never fails the action it reports</b>, as with the war log: a raid that landed has
/// landed whether or not its letter could be written.</para>
///
/// <para>Most war letters come from the war log itself (<see cref="FromWarLogAsync"/>): a war log line
/// with a player on either side is that player's letter, so every raid and siege path writes its
/// letters with no extra line, and a new path that logs gets them too.</para>
/// </summary>
public class LetterService
{
    public const int MaximumPage = 100;
    private const string FactionPrefix = "faction:";

    private readonly ApplicationDbContext _context;
    private readonly IHubContext<GameHub>? _hub;
    private readonly ILogger<LetterService> _logger;
    private readonly TimeProvider _clock;

    public LetterService(ApplicationDbContext context, IHubContext<GameHub>? hub, ILogger<LetterService> logger, TimeProvider clock)
    {
        _context = context;
        _hub = hub;
        _logger = logger;
        _clock = clock;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // -----------------------------------------------------------------
    // Writing
    // -----------------------------------------------------------------

    /// <summary>
    /// Sends one letter, once: a second send with the same <paramref name="dedupKey"/> does nothing. A raid
    /// on a region already raided within the hour folds into that letter instead ("raided twice").
    /// </summary>
    public async Task SendAsync(Guid gameInstanceId, string userId, LetterKind kind, DateTime occurredAt, string dedupKey,
        string? regionId = null, string? subjectId = null, string? actorName = null, string? detail = null)
    {
        if (string.IsNullOrEmpty(userId) || userId.StartsWith(FactionPrefix)) return;
        Letter? letter = null;
        try
        {
            if (await _context.Letters.AnyAsync(l => l.UserId == userId && l.GameInstanceId == gameInstanceId && l.DedupKey == dedupKey))
                return;

            if (kind == LetterKind.RaidOnYou && regionId != null)
            {
                var since = occurredAt - LetterRules.GroupWindow;
                var earlier = await _context.Letters
                    .Where(l => l.UserId == userId && l.GameInstanceId == gameInstanceId && l.Kind == nameof(LetterKind.RaidOnYou)
                                && l.RegionId == regionId && l.OccurredAt > since && l.OccurredAt <= occurredAt)
                    .OrderByDescending(l => l.OccurredAt)
                    .FirstOrDefaultAsync();
                if (earlier != null && LetterRules.FoldsInto(earlier.OccurredAt, occurredAt))
                {
                    earlier.Count++;
                    earlier.ReadAt = null;
                    earlier.ActorName = actorName ?? earlier.ActorName;
                    earlier.Detail = detail ?? earlier.Detail;
                    await _context.SaveChangesAsync();
                    await PushAsync(earlier);
                    return;
                }
            }

            letter = new Letter
            {
                GameInstanceId = gameInstanceId,
                UserId = userId,
                Kind = kind.ToString(),
                OccurredAt = occurredAt,
                DedupKey = Clip(dedupKey, 120)!,
                RegionId = Clip(regionId, 32),
                SubjectId = Clip(subjectId, 64),
                ActorName = Clip(actorName, 64),
                Detail = Clip(detail, 200),
            };
            _context.Letters.Add(letter);
            await _context.SaveChangesAsync();

            await TrimAsync(gameInstanceId, userId);
            await PushAsync(letter);
        }
        catch (Exception ex)
        {
            // Do not leave a failed letter tracked, or the next unrelated save would retry - and fail - it.
            if (letter != null) _context.Entry(letter).State = EntityState.Detached;
            _logger.LogWarning(ex, "Could not send letter {Kind} to {User} in instance {Instance}.", kind, userId, gameInstanceId);
        }
    }

    /// <summary>
    /// The letters a war log line means for the players in it: the defender hears of a raid or a siege on
    /// them, the besieger of how their own siege ended, a captor of a ransom paid to them.
    /// </summary>
    public async Task FromWarLogAsync(WarLogEntry entry)
    {
        string? subject = IsPlayer(entry.SubjectUserId) ? entry.SubjectUserId : null;
        string? actor = IsPlayer(entry.ActorUserId) ? entry.ActorUserId : null;
        string key(string who) => $"warlog:{entry.Id}:{who}";

        async Task Send(string? to, LetterKind kind, string? detail, string role) =>
            await SendAsync(entry.GameInstanceId, to!, kind, entry.OccurredAt, key(role), entry.RegionId,
                actorName: role == "subject" ? entry.ActorName : entry.SubjectName, detail: detail);

        if (!Enum.TryParse<WarLogKind>(entry.Kind, out var warKind)) return;
        switch (warKind)
        {
            case WarLogKind.RaidLanded:
                if (subject != null) await Send(subject, LetterKind.RaidOnYou, entry.Detail, "subject");
                break;
            case WarLogKind.RaidRepelled:
                if (subject != null) await Send(subject, LetterKind.RaidRepelled, entry.Detail, "subject");
                break;
            case WarLogKind.SiegeDeclared:
                if (subject != null) await Send(subject, LetterKind.SiegeDeclaredOnYou, entry.Detail, "subject");
                break;
            case WarLogKind.SiegeWon:
                if (subject != null) await Send(subject, LetterKind.SiegeResultOnYou, Result("lost", entry.Detail), "subject");
                if (actor != null) await Send(actor, LetterKind.YourSiegeResult, Result("won", entry.Detail), "actor");
                // The fallen region's garrison is taken with it, by a player or a faction alike.
                if (subject != null && CapturedIn(entry.Detail) is int taken and > 0)
                    await Send(subject, LetterKind.HeroesCaptured, taken.ToString(), "captured");
                break;
            case WarLogKind.SiegeRepelled:
                if (subject != null) await Send(subject, LetterKind.SiegeResultOnYou, Result("held", entry.Detail), "subject");
                if (actor != null) await Send(actor, LetterKind.YourSiegeResult, Result("failed", entry.Detail), "actor");
                break;
            case WarLogKind.SiegeLapsed:
                if (subject != null) await Send(subject, LetterKind.SiegeResultOnYou, "lapsed", "subject");
                if (actor != null) await Send(actor, LetterKind.YourSiegeResult, "lapsed", "actor");
                break;
            case WarLogKind.RansomPaid:
                // The captor is the subject; the payer bought their own heroes back and needs no letter.
                if (subject != null) await Send(subject, LetterKind.PrisonersRansomed, entry.Detail, "subject");
                break;
        }
    }

    /// <summary>
    /// How many champions a won siege took, from its war log detail ("2 champions taken prisoner", worded the
    /// same by the player and the faction siege). Null when it took none.
    /// </summary>
    public static int? CapturedIn(string? detail)
    {
        var match = detail == null ? null : Regex.Match(detail, @"^(\d+) champions? taken prisoner");
        return match is { Success: true } ? int.Parse(match.Groups[1].Value) : null;
    }

    /// <summary>
    /// Catches up what only the clock decides, before a read: heroes taken in a siege walk home after
    /// <see cref="CaptivityRules.PrisonerReturnHours"/> unless bought back first, and that return is a
    /// letter dated when it happened. Bought-back heroes need none: the player did it themselves.
    /// </summary>
    public async Task CatchUpAsync(Guid gameInstanceId, string userId)
    {
        var now = Now;
        var returnAfter = TimeSpan.FromHours(CaptivityRules.PrisonerReturnHours);
        var taken = await _context.Letters.AsNoTracking()
            .Where(l => l.UserId == userId && l.GameInstanceId == gameInstanceId && l.Kind == nameof(LetterKind.HeroesCaptured))
            .ToListAsync();
        foreach (var letter in taken.Where(l => l.OccurredAt + returnAfter <= now))
        {
            var home = letter.OccurredAt + returnAfter;
            bool ransomed = await _context.WarLog.AsNoTracking().AnyAsync(e =>
                e.GameInstanceId == gameInstanceId && e.Kind == nameof(WarLogKind.RansomPaid) && e.ActorUserId == userId &&
                e.RegionId == letter.RegionId && e.OccurredAt >= letter.OccurredAt && e.OccurredAt <= home);
            if (ransomed) continue;
            await SendAsync(gameInstanceId, userId, LetterKind.HeroesReturned, home, $"returned:{letter.Id}", letter.RegionId,
                detail: letter.Detail);
        }
    }

    /// <summary>"won" or "won|2 champions taken prisoner": the outcome first, so the client can word it.</summary>
    private static string Result(string outcome, string? detail) => string.IsNullOrEmpty(detail) ? outcome : $"{outcome}|{detail}";

    private static bool IsPlayer(string? userId) => !string.IsNullOrEmpty(userId) && !userId.StartsWith(FactionPrefix);

    private async Task TrimAsync(Guid gameInstanceId, string userId)
    {
        var mine = await _context.Letters
            .Where(l => l.UserId == userId && l.GameInstanceId == gameInstanceId)
            .Select(l => new { l.Id, l.OccurredAt, l.ReadAt })
            .ToListAsync();
        var drop = LetterRules.ToDrop(mine, l => l.OccurredAt, l => l.ReadAt != null, Now).Select(l => l.Id).ToList();
        if (drop.Count == 0) return;

        _context.Letters.RemoveRange(await _context.Letters.Where(l => drop.Contains(l.Id)).ToListAsync());
        await _context.SaveChangesAsync();
    }

    private async Task PushAsync(Letter letter)
    {
        if (_hub == null) return;
        try
        {
            var flags = await FlagsAsync(letter.GameInstanceId, letter.UserId, new[] { letter });
            await _hub.Clients.User(letter.UserId).SendAsync("LetterAdded", ToResponse(letter, flags.Contains(letter.Id)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not push letter {Id}.", letter.Id);
        }
    }

    // -----------------------------------------------------------------
    // Reading
    // -----------------------------------------------------------------

    /// <summary>A page of the player's letters in this realm, newest first, each with its live ⚑.</summary>
    public async Task<LettersResponse> PageAsync(Guid gameInstanceId, string userId, DateTime? before = null, int take = 50)
    {
        var query = _context.Letters.AsNoTracking().Where(l => l.UserId == userId && l.GameInstanceId == gameInstanceId);
        if (before != null) query = query.Where(l => l.OccurredAt < before.Value);
        var page = await query
            .OrderByDescending(l => l.OccurredAt)
            .Take(Math.Clamp(take, 1, MaximumPage))
            .ToListAsync();

        var flags = await FlagsAsync(gameInstanceId, userId, page);
        var summary = await SummaryAsync(gameInstanceId, userId);
        return new LettersResponse(page.Select(l => ToResponse(l, flags.Contains(l.Id))).ToList(), summary.Unread, summary.Flagged);
    }

    /// <summary>The unread count, and whether any unread letter still needs the player.</summary>
    public async Task<LetterSummaryResponse> SummaryAsync(Guid gameInstanceId, string userId)
    {
        var unread = await _context.Letters.AsNoTracking()
            .Where(l => l.UserId == userId && l.GameInstanceId == gameInstanceId && l.ReadAt == null)
            .ToListAsync();
        var flags = await FlagsAsync(gameInstanceId, userId, unread);
        return new LetterSummaryResponse(unread.Count, flags.Count > 0);
    }

    /// <summary>Marks the given letters read, or all of them. Returns how many changed.</summary>
    public async Task<int> MarkReadAsync(Guid gameInstanceId, string userId, IReadOnlyCollection<Guid>? ids, bool all)
    {
        var query = _context.Letters.Where(l => l.UserId == userId && l.GameInstanceId == gameInstanceId && l.ReadAt == null);
        if (!all)
        {
            if (ids == null || ids.Count == 0) return 0;
            query = query.Where(l => ids.Contains(l.Id));
        }
        var letters = await query.ToListAsync();
        var now = Now;
        foreach (var letter in letters) letter.ReadAt = now;
        await _context.SaveChangesAsync();
        return letters.Count;
    }

    /// <summary>
    /// Which of <paramref name="letters"/> still need the player (⚑). Worked out from state already stored,
    /// never kept on the letter: a siege on them while it is live, heroes while they are held, an ambush
    /// while the company stands halted, a quest until it is handed in, a season until its chest is opened.
    /// </summary>
    public async Task<HashSet<Guid>> FlagsAsync(Guid gameInstanceId, string userId, IEnumerable<Letter> letters)
    {
        var flagged = new HashSet<Guid>();
        var candidates = letters.Where(l => Enum.TryParse<LetterKind>(l.Kind, out var k) && LetterRules.CanFlag(k)).ToList();
        if (candidates.Count == 0) return flagged;

        var sieged = candidates.Where(l => l.Kind == nameof(LetterKind.SiegeDeclaredOnYou)).ToList();
        if (sieged.Count > 0)
        {
            var regions = await LiveSiegeRegionsAsync(gameInstanceId, userId);
            foreach (var letter in sieged)
                if (letter.RegionId != null && regions.Contains(letter.RegionId)) flagged.Add(letter.Id);
        }
        var held = candidates.Where(l => l.Kind == nameof(LetterKind.HeroesCaptured)).ToList();
        if (held.Count > 0)
        {
            var regions = await HoldingRegionsAsync(gameInstanceId);
            foreach (var letter in held)
                if (letter.RegionId != null && regions.Contains(letter.RegionId)) flagged.Add(letter.Id);
        }

        var ambushed = candidates.Where(l => l.Kind == nameof(LetterKind.CompanyAmbushed)).ToList();
        if (ambushed.Count > 0)
        {
            var halted = (await _context.PlayerParties.AsNoTracking()
                .Where(p => p.GameInstanceId == gameInstanceId && p.UserId == userId && p.State == CompanyState.Ambushed)
                .Select(p => p.Id).ToListAsync()).Select(id => id.ToString()).ToHashSet();
            foreach (var letter in ambushed)
                if (letter.SubjectId != null && halted.Contains(letter.SubjectId)) flagged.Add(letter.Id);
        }

        var quests = candidates.Where(l => l.Kind == nameof(LetterKind.QuestReady)).ToList();
        if (quests.Count > 0)
        {
            var waiting = (await _context.PlayerQuests.AsNoTracking()
                .Where(q => q.GameInstanceId == gameInstanceId && q.UserId == userId && q.DoneAt != null && q.HandedInAt == null)
                .Select(q => q.Id).ToListAsync()).Select(id => id.ToString()).ToHashSet();
            foreach (var letter in quests)
                if (letter.SubjectId != null && waiting.Contains(letter.SubjectId)) flagged.Add(letter.Id);
        }

        var seasons = candidates.Where(l => l.Kind == nameof(LetterKind.SeasonEnded)).ToList();
        if (seasons.Count > 0)
        {
            var unopened = (await _context.SeasonResults.AsNoTracking()
                .Where(r => r.GameInstanceId == gameInstanceId && r.UserId == userId && r.ChestRarity != null && r.ChestOpenedAt == null)
                .Select(r => r.SeasonNumber).ToListAsync()).Select(n => n.ToString()).ToHashSet();
            foreach (var letter in seasons)
                if (letter.SubjectId != null && unopened.Contains(letter.SubjectId)) flagged.Add(letter.Id);
        }
        return flagged;
    }

    /// <summary>The regions where somebody is still held prisoner (a fallen garrison stays where it was taken).</summary>
    private async Task<HashSet<string>> HoldingRegionsAsync(Guid gameInstanceId)
    {
        var row = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        var regions = new HashSet<string>();
        if (row == null) return regions;
        var now = Now;
        foreach (var region in WorldRegionBlob.ReadAllRegions(JsonNode.Parse(row.GameData)))
            if (region.SiteOverrides.Values.Any(o => o != null && CaptivityRules.IsHolding(o, now)))
                regions.Add(region.RegionId);
        return regions;
    }

    private async Task<HashSet<string>> LiveSiegeRegionsAsync(Guid gameInstanceId, string userId)
    {
        var players = await _context.Sieges.AsNoTracking()
            .Where(s => s.GameInstanceId == gameInstanceId && s.DefenderUserId == userId &&
                        (s.State == SiegeState.Mustering || s.State == SiegeState.Assault))
            .Select(s => s.RegionId).ToListAsync();
        var factions = await _context.FactionSieges.AsNoTracking()
            .Where(s => s.GameInstanceId == gameInstanceId && s.DefenderUserId == userId && s.State == SiegeState.Mustering)
            .Select(s => s.RegionId).ToListAsync();
        return new HashSet<string>(players.Concat(factions));
    }

    // -----------------------------------------------------------------

    public static LetterResponse ToResponse(Letter l, bool flagged) => new(
        l.Id, l.GameInstanceId, l.Kind, l.OccurredAt, l.RegionId, l.SubjectId, l.ActorName, l.Detail, l.Count, l.ReadAt != null, flagged);

    private static string? Clip(string? s, int max) => s == null ? null : s.Length > max ? s[..max] : s;
}
