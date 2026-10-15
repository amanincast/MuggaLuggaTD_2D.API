using Enums;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>A player's best try in a week.</summary>
public record TrialBest(long Score, int Kills, int Waves, int Seconds, int Hero, int Rank, int Total);

/// <summary>A finished week, as the player placed in it.</summary>
public record TrialFinish(int Week, DateTime StartsAt, long Score, int Rank, int Total, ItemRarityTypes? Chest, Guid? PaidInstanceId);

/// <summary>One row of the week's top, for the trial book's glance.</summary>
public record TrialTopRow(int Rank, string Name, long Score, int Hero, bool Mine);

/// <summary>
/// The week as the trial book shows it. The heroes are not here: the client derives them from the week and its own
/// signature content (<see cref="TrialRules.Heroes"/>), as it derives a region's sites from its seed.
/// </summary>
public record TrialResponse(
    int Week, DateTime StartsAt, DateTime EndsAt, BiomeType Biome, int ArenaSeed, List<RiftMutator> Mutators,
    int TriesLeft, DateTime NextTryAt, Guid? OpenTry, TrialBest? Best, List<TrialTopRow> Top, int Total,
    TrialFinish? LastWeek, List<TrialFinish> Paid, int WeeksEntered, TrialFinish? BestFinish, DateTime ServerNow);

public record TrialBeginRequest(int Hero);

public record TrialBeginResponse(Guid AttemptId, int Week, int TriesLeft);

public record TrialFinishRequest(Guid AttemptId, int Kills, int Waves, int Seconds);

public record TrialFinishResponse(long Score, int Kills, int Waves, int Seconds, bool Trimmed, bool IsBest, TrialBest? Best);

/// <summary>Why a try could not begin or finish.</summary>
public class TrialRefusal : Exception
{
    public TrialRefusal(string message) : base(message) { }
}

/// <summary>
/// The Weekly Trial (Active Content E): the week, the day's tries, scoring a try, and paying each finished week's chest
/// into the realm the player next opens the Hall in. Cross-realm: nothing here belongs to a realm but the chest.
/// </summary>
public class TrialService
{
    private static readonly Random Dice = new();

    /// <summary>The trial book's glance at the top.</summary>
    public const int TopShown = 5;

    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly ItemLedgerService _items;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<TrialService> _logger;
    private readonly LetterService? _letters;

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public TrialService(ApplicationDbContext context, IGameContentProvider content, ItemLedgerService items,
        ISessionLog sessionLog, ILogger<TrialService> logger, LetterService? letters = null)
    {
        _context = context;
        _content = content;
        _items = items;
        _sessionLog = sessionLog;
        _logger = logger;
        _letters = letters;
    }

    // -----------------------------------------------------------------
    // The week
    // -----------------------------------------------------------------

    /// <summary>
    /// This week as the player sees it. With <paramref name="realm"/> (a realm they are in), first pays any finished
    /// week's chest owed to them into it: the realm they came back to is the one they last played.
    /// </summary>
    public async Task<TrialResponse> ViewAsync(string userId, Guid? realm)
    {
        var paid = realm == null ? new List<TrialFinish>() : await SettleAsync(userId, realm.Value);

        var now = Clock();
        int week = TrialRules.WeekOf(now);
        int day = TrialRules.DayOf(now);

        var today = await _context.TrialAttempts.AsNoTracking()
            .Where(a => a.UserId == userId && a.Day == day)
            .ToListAsync();
        var open = today.Where(a => a.FinishedAt == null).OrderByDescending(a => a.BegunAt).FirstOrDefault();

        var best = await BestAsync(week, userId);
        var (top, total) = await TopAsync(week, userId);

        var mine = await _context.TrialEntries.AsNoTracking()
            .Where(e => e.UserId == userId && e.BestScore > 0)
            .ToListAsync();
        var finished = mine.Where(e => e.Week < week && e.FinalRank != null).ToList();
        var last = finished.FirstOrDefault(e => e.Week == week - 1);
        var bestFinish = finished
            .OrderBy(e => e.FinalRank!.Value / (double)Math.Max(1, e.FinalTotal ?? 1))
            .ThenByDescending(e => e.Week)
            .FirstOrDefault();

        return new TrialResponse(
            week, Utc(TrialRules.StartOf(week)), Utc(TrialRules.EndOf(week)),
            TrialRules.Biome(week), TrialRules.ArenaSeed(week), TrialRules.Mutators(week),
            Math.Max(0, TrialRules.AttemptsPerDay - today.Count),
            Utc(TrialRules.Epoch.AddDays(day + 1)),
            open?.Id, best, top, total,
            last == null ? null : FinishOf(last),
            paid,
            mine.Count,
            bestFinish == null ? null : FinishOf(bestFinish),
            Utc(now));
    }

    /// <summary>Begins a try with hero <paramref name="hero"/> (0-3): spends one of the day's tries.</summary>
    public async Task<TrialBeginResponse> BeginAsync(string userId, int hero, Guid? realm)
    {
        if (hero < 0 || hero >= TrialRules.Classes.Length) throw new TrialRefusal("No such hero.");

        var now = Clock();
        int week = TrialRules.WeekOf(now);
        int day = TrialRules.DayOf(now);
        int used = await _context.TrialAttempts.CountAsync(a => a.UserId == userId && a.Day == day);
        if (used >= TrialRules.AttemptsPerDay)
            throw new TrialRefusal($"You have had your {TrialRules.AttemptsPerDay} tries today. The next comes at midnight (UTC).");

        var attempt = new TrialAttempt
        {
            UserId = userId,
            Week = week,
            Day = day,
            Hero = hero,
            GameInstanceId = realm,
            BegunAt = now,
        };
        _context.TrialAttempts.Add(attempt);
        await _context.SaveChangesAsync();

        _sessionLog.Log("TRIAL-BEGIN", $"user={userId} week={week} hero={hero} try={used + 1}/{TrialRules.AttemptsPerDay}");
        return new TrialBeginResponse(attempt.Id, week, TrialRules.AttemptsPerDay - used - 1);
    }

    /// <summary>
    /// Finishes a try with what the client saw, held to what could have happened (<see cref="TrialRules.Check"/>), and
    /// keeps it as the week's best if it beats it. A try counts for the week it was begun in.
    /// </summary>
    public async Task<TrialFinishResponse> FinishAsync(string userId, TrialFinishRequest request)
    {
        var attempt = await _context.TrialAttempts.FirstOrDefaultAsync(a => a.Id == request.AttemptId && a.UserId == userId);
        if (attempt == null) throw new TrialRefusal("No such try.");
        if (attempt.FinishedAt != null) throw new TrialRefusal("That try is already scored.");

        var now = Clock();
        var check = TrialRules.Check(_content.RunTuning, request.Kills, request.Waves, request.Seconds, (now - attempt.BegunAt).TotalSeconds);
        attempt.FinishedAt = now;
        attempt.Kills = check.Kills;
        attempt.Waves = check.Waves;
        attempt.Seconds = check.Seconds;
        attempt.Score = check.Score;
        attempt.Trimmed = check.Trimmed;
        if (check.Trimmed)
        {
            attempt.Reported = $"{request.Kills}k {request.Waves}w {request.Seconds}s";
            _logger.LogWarning("Trial try {Attempt} by {User} trimmed: reported {Reported}, kept {Kills}k {Waves}w {Seconds}s.",
                attempt.Id, userId, attempt.Reported, check.Kills, check.Waves, check.Seconds);
        }

        bool isBest = false;
        await Concurrency.RetryAsync(_context, async () =>
        {
            await Concurrency.RefreshAsync<TrialEntry>(_context, e => e.Week == attempt.Week && e.UserId == userId);
            var entry = _context.TrialEntries.Local.FirstOrDefault(e => e.Week == attempt.Week && e.UserId == userId)
                        ?? await _context.TrialEntries.FirstOrDefaultAsync(e => e.Week == attempt.Week && e.UserId == userId);
            if (entry == null)
            {
                entry = new TrialEntry { Week = attempt.Week, UserId = userId };
                _context.TrialEntries.Add(entry);
            }

            isBest = check.Score > entry.BestScore;
            if (isBest)
            {
                entry.BestScore = check.Score;
                entry.BestKills = check.Kills;
                entry.BestWaves = check.Waves;
                entry.BestSeconds = check.Seconds;
                entry.BestHero = attempt.Hero;
                entry.BestAt = now;
            }
            await _context.SaveChangesAsync();
        });

        _sessionLog.Log("TRIAL-FINISH", $"user={userId} week={attempt.Week} hero={attempt.Hero} score={check.Score} " +
                                        $"kills={check.Kills} waves={check.Waves} seconds={check.Seconds} trimmed={check.Trimmed} best={isBest}");
        return new TrialFinishResponse(check.Score, check.Kills, check.Waves, check.Seconds, check.Trimmed, isBest,
            await BestAsync(attempt.Week, userId));
    }

    // -----------------------------------------------------------------
    // The week's end
    // -----------------------------------------------------------------

    /// <summary>
    /// Pays every finished week this player scored in and has not been paid for: a chest by finish
    /// (<see cref="TrialRules.ChestFor"/>) into <paramref name="realm"/>, with a letter. A week's ranks are final once
    /// it is over, so paying late changes nothing about who got what.
    /// </summary>
    public async Task<List<TrialFinish>> SettleAsync(string userId, Guid realm)
    {
        int week = TrialRules.WeekOf(Clock());
        var owed = await _context.TrialEntries
            .Where(e => e.UserId == userId && e.Week < week && e.PaidAt == null)
            .OrderBy(e => e.Week)
            .ToListAsync();
        var paid = new List<TrialFinish>();
        if (owed.Count == 0) return paid;

        bool inRealm = await _context.GameInstances.AsNoTracking().AnyAsync(g => g.Id == realm);
        if (!inRealm) return paid;

        foreach (var entry in owed)
        {
            var finish = await PayAsync(entry, realm);
            if (finish != null) paid.Add(finish);
        }
        return paid;
    }

    private async Task<TrialFinish?> PayAsync(TrialEntry entry, Guid realm)
    {
        int total = await _context.TrialEntries.CountAsync(e => e.Week == entry.Week && e.BestScore > 0);
        int rank = await _context.TrialEntries.CountAsync(e => e.Week == entry.Week && e.BestScore > entry.BestScore) + 1;
        ItemRarityTypes? chest = entry.BestScore > 0 ? TrialRules.ChestFor(rank, total) : null;

        // Claim the payment first, so two Hall visits at once pay one chest.
        bool claimed = await Concurrency.RetryAsync(_context, async () =>
        {
            await Concurrency.RefreshAsync<TrialEntry>(_context, e => e.Id == entry.Id);
            if (entry.PaidAt != null) return false;
            entry.PaidAt = Clock();
            entry.PaidInstanceId = chest == null ? null : realm;
            entry.ChestRarity = chest;
            entry.FinalRank = entry.BestScore > 0 ? rank : null;
            entry.FinalTotal = entry.BestScore > 0 ? total : null;
            await _context.SaveChangesAsync();
            return true;
        });
        if (!claimed || chest == null) return null;

        int level = await RealmGoalService.RosterLevelAsync(_context, _logger, realm, entry.UserId);
        var pieces = QuestRules.RollChest(_content.DroppableItems.ToList(), Dice, level, chest.Value);
        await _items.GrantAsync(realm, entry.UserId, pieces, $"trial {entry.Week}");

        if (_letters != null)
            await _letters.SendAsync(realm, entry.UserId, LetterKind.TrialChest, Clock(), $"trial:{entry.Week}",
                subjectId: entry.Week.ToString(), detail: $"{chest.Value}:{rank}:{total}:{entry.BestScore}");

        _sessionLog.Log("TRIAL-CHEST", $"user={entry.UserId} week={entry.Week} rank={rank}/{total} chest={chest} realm={realm}");
        return FinishOf(entry);
    }

    // -----------------------------------------------------------------
    // Reading
    // -----------------------------------------------------------------

    private async Task<TrialBest?> BestAsync(int week, string userId)
    {
        var entry = await _context.TrialEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Week == week && e.UserId == userId);
        if (entry == null || entry.BestScore <= 0) return null;
        int total = await _context.TrialEntries.CountAsync(e => e.Week == week && e.BestScore > 0);
        int rank = await _context.TrialEntries.CountAsync(e => e.Week == week && e.BestScore > entry.BestScore) + 1;
        return new TrialBest(entry.BestScore, entry.BestKills, entry.BestWaves, entry.BestSeconds, entry.BestHero, rank, total);
    }

    private async Task<(List<TrialTopRow> Rows, int Total)> TopAsync(int week, string userId)
    {
        var entries = await _context.TrialEntries.AsNoTracking()
            .Where(e => e.Week == week && e.BestScore > 0)
            .OrderByDescending(e => e.BestScore).ThenBy(e => e.BestAt)
            .Take(TopShown)
            .ToListAsync();
        int total = await _context.TrialEntries.CountAsync(e => e.Week == week && e.BestScore > 0);
        var ids = entries.Select(e => e.UserId).ToList();
        var names = await _context.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => PlayerNames.Of(u.DisplayName, u.UserName));

        var rows = new List<TrialTopRow>();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            int rank = i > 0 && entries[i - 1].BestScore == e.BestScore ? rows[i - 1].Rank : i + 1;
            rows.Add(new TrialTopRow(rank, names.TryGetValue(e.UserId, out var n) ? n : "Unknown", e.BestScore, e.BestHero, e.UserId == userId));
        }
        return (rows, total);
    }

    private static TrialFinish FinishOf(TrialEntry e) =>
        new(e.Week, Utc(TrialRules.StartOf(e.Week)), e.BestScore, e.FinalRank ?? 0, e.FinalTotal ?? 0, e.ChestRarity, e.PaidInstanceId);

    private static DateTime Utc(DateTime at) => DateTime.SpecifyKind(at, DateTimeKind.Utc);

    // -----------------------------------------------------------------
    // Debug (Development only; Combat Debug → Weekly Trial)
    // -----------------------------------------------------------------

    /// <summary>Gives this player back today's tries.</summary>
    public async Task DebugResetTriesAsync(string userId)
    {
        int day = TrialRules.DayOf(Clock());
        var today = await _context.TrialAttempts.Where(a => a.UserId == userId && a.Day == day).ToListAsync();
        _context.TrialAttempts.RemoveRange(today);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Moves this player's best of this week back to last week, unpaid, with made-up rivals around it, so the next Hall
    /// visit pays its chest. Rivals are the leaderboards' seeded "Chronicler" players when they exist.
    /// </summary>
    public async Task<string> DebugEndWeekAsync(string userId)
    {
        int week = TrialRules.WeekOf(Clock());
        int last = week - 1;
        var mine = await _context.TrialEntries.FirstOrDefaultAsync(e => e.Week == week && e.UserId == userId);
        long score = mine?.BestScore > 0 ? mine.BestScore : 50_000;

        var stale = await _context.TrialEntries.Where(e => e.Week == last).ToListAsync();
        _context.TrialEntries.RemoveRange(stale);

        _context.TrialEntries.Add(new TrialEntry
        {
            Week = last, UserId = userId, BestScore = score, BestKills = mine?.BestKills ?? 250, BestWaves = mine?.BestWaves ?? 8,
            BestSeconds = mine?.BestSeconds ?? 200, BestHero = mine?.BestHero ?? 0, BestAt = TrialRules.StartOf(last).AddDays(3),
        });

        var rivals = await _context.Users.AsNoTracking()
            .Where(u => u.Id != userId && u.UserName != null && u.UserName.StartsWith("chronicler"))
            .Select(u => u.Id).Take(9).ToListAsync();
        for (int i = 0; i < rivals.Count; i++)
        {
            long theirs = (long)(score * (1.6 - i * 0.15));
            _context.TrialEntries.Add(new TrialEntry
            {
                Week = last, UserId = rivals[i], BestScore = theirs, BestKills = 200 + i * 10, BestWaves = 7, BestSeconds = 180,
                BestHero = i % TrialRules.Classes.Length, BestAt = TrialRules.StartOf(last).AddDays(2),
                // Rivals are already settled: only the asker's chest is waiting.
                PaidAt = Clock(),
            });
        }
        await _context.SaveChangesAsync();
        return $"Week {last} now holds your {score:N0} and {rivals.Count} rivals; open the Hall to be paid.";
    }

    /// <summary>Forgets this player's trial: every try and every week.</summary>
    public async Task DebugForgetAsync(string userId)
    {
        _context.TrialAttempts.RemoveRange(await _context.TrialAttempts.Where(a => a.UserId == userId).ToListAsync());
        _context.TrialEntries.RemoveRange(await _context.TrialEntries.Where(e => e.UserId == userId).ToListAsync());
        await _context.SaveChangesAsync();
    }
}
