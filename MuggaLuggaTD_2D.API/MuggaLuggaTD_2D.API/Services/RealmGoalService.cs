using System.Text.Json.Nodes;
using Enums;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

public record RealmGoalContributor(string Name, int Count, bool Me);

public record RealmGoalResponse(
    int Day, RealmGoalKind Kind, string? Subject, string Text, int Target, int Count,
    DateTime EndsAt, DateTime? ReachedAt, int MyCount, ItemRarityTypes? MyChest,
    List<RealmGoalContributor> Top);

/// <summary>
/// The realm's goal of the day (Active Content B; Mike 2026-10-10: daily). Every deed that advances
/// a quest advances it too (<see cref="QuestService.RecordAsync"/> calls <see cref="RecordAsync"/>),
/// so auto mode and hand fights both count. When the count reaches the target, everyone with at least
/// 2% of it is paid a chest at once, by the request whose write reached it.
///
/// <para>The counter and the shares are revisioned rows (Hardening 4): two players finishing fights at
/// the same moment both count, and only one of them can be the write that reaches the goal, so the
/// chests are paid once.</para>
/// </summary>
public class RealmGoalService
{
    private const int TopShown = 5;

    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly ItemLedgerService _items;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<RealmGoalService> _logger;
    private readonly WarLogService? _warLog;
    private readonly LetterService? _letters;

    private static readonly Random Dice = new();

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public RealmGoalService(ApplicationDbContext context, IGameContentProvider content, ItemLedgerService items,
        ISessionLog sessionLog, ILogger<RealmGoalService> logger, WarLogService? warLog = null, LetterService? letters = null)
    {
        _context = context;
        _content = content;
        _items = items;
        _sessionLog = sessionLog;
        _logger = logger;
        _warLog = warLog;
        _letters = letters;
    }

    /// <summary>Today's goal, made if this is the first time anyone asked. Null for a realm with no world.</summary>
    public async Task<RealmGoal?> TodayAsync(Guid gameInstanceId)
    {
        var now = Clock();
        int day = RealmGoalRules.DayOf(now);

        var goal = await _context.RealmGoals.FirstOrDefaultAsync(g => g.GameInstanceId == gameInstanceId && g.Day == day);
        if (goal != null) return goal;

        var world = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (world == null) return null;

        var regions = WorldRegionBlob.ReadAllRegions(JsonNode.Parse(world.GameData));
        var peoples = regions
            .SelectMany(r => QuestRules.PeoplesOf(r.Biome, _content.EnemyPeoples))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var since = now.AddDays(-7);
        int active = await _context.PlayerGameData.CountAsync(p => p.GameInstanceId == gameInstanceId && p.UpdatedAt >= since);

        var offer = RealmGoalRules.For(gameInstanceId.ToString(), day, peoples, active);
        goal = new RealmGoal
        {
            GameInstanceId = gameInstanceId,
            Day = day,
            Kind = offer.Kind,
            Subject = offer.Subject,
            Target = offer.Target,
        };
        _context.RealmGoals.Add(goal);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Another request made today's goal first (the unique realm-and-day index). Theirs is the same goal.
            _context.Entry(goal).State = EntityState.Detached;
            goal = await _context.RealmGoals.FirstAsync(g => g.GameInstanceId == gameInstanceId && g.Day == day);
        }
        return goal;
    }

    /// <summary>Counts a deed toward today's goal. Never throws: a goal must not break the fight that fed it.</summary>
    public async Task RecordAsync(Guid gameInstanceId, string userId, QuestDeed deed)
    {
        if (string.IsNullOrEmpty(userId) || deed == null) return;
        try
        {
            var goal = await TodayAsync(gameInstanceId);
            if (goal == null) return;

            int amount = RealmGoalRules.CountOf(goal.Kind, goal.Subject, deed);
            if (amount <= 0) return;

            bool reachedNow = false;
            int quarterReached = 0;
            await Concurrency.RetryAsync(_context, async () =>
            {
                await Concurrency.RefreshAsync<RealmGoal>(_context, g => g.Id == goal.Id);
                var share = _context.RealmGoalShares.Local.FirstOrDefault(s => s.GoalId == goal.Id && s.UserId == userId)
                    ?? await _context.RealmGoalShares.FirstOrDefaultAsync(s => s.GoalId == goal.Id && s.UserId == userId);
                if (share == null)
                {
                    share = new RealmGoalShare { GoalId = goal.Id, UserId = userId };
                    _context.RealmGoalShares.Add(share);
                }

                // Past the target, deeds still count toward each player's share of the day, not the bar.
                share.Count += amount;
                goal.Count += amount;

                reachedNow = goal.ReachedAt == null && goal.Count >= goal.Target;
                if (reachedNow) goal.ReachedAt = Clock();

                quarterReached = RealmGoalRules.QuarterOf(goal.Count, goal.Target);
                bool announce = quarterReached > goal.QuartersAnnounced;
                if (announce) goal.QuartersAnnounced = quarterReached;
                else quarterReached = 0;

                await _context.SaveChangesAsync();
            });

            if (quarterReached > 0 && quarterReached < RealmGoalRules.Quarters && _warLog != null)
                await _warLog.RecordAsync(gameInstanceId, WarLogKind.RealmGoalProgress, null, null, null,
                    $"{quarterReached * 25}:{Parts(goal)}");

            if (reachedNow) await PayAsync(gameInstanceId, goal, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not count a deed toward the realm goal in {Instance}.", gameInstanceId);
        }
    }

    /// <summary>Pays every share of at least 2% a chest, writes the war log and sends each a letter.</summary>
    private async Task PayAsync(Guid gameInstanceId, RealmGoal goal, string finisherId)
    {
        var shares = await _context.RealmGoalShares.Where(s => s.GoalId == goal.Id).ToListAsync();
        string parts = Parts(goal);
        int paid = 0;

        foreach (var share in shares)
        {
            var chest = RealmGoalRules.ChestFor(share.Count, goal.Target);
            if (chest == null) continue;

            int level = await RosterLevelAsync(gameInstanceId, share.UserId);
            var pieces = QuestRules.RollChest(_content.DroppableItems.ToList(), Dice, level, chest.Value);
            await _items.GrantAsync(gameInstanceId, share.UserId, pieces, $"realm-goal {goal.Day}");
            share.ChestRarity = chest;
            paid++;

            if (_letters != null)
                await _letters.SendAsync(gameInstanceId, share.UserId, LetterKind.RealmGoalReached, goal.ReachedAt ?? Clock(),
                    $"goal:{goal.Day}", detail: $"{chest.Value}:{share.Count}:{parts}");
        }
        await Concurrency.RetryAsync(_context, () => _context.SaveChangesAsync());

        if (_warLog != null)
            await _warLog.RecordAsync(gameInstanceId, WarLogKind.RealmGoalReached, finisherId, null, null, $"100:{parts}");

        _sessionLog.Log("REALM-GOAL", $"realm={gameInstanceId} day={goal.Day} reached by={finisherId} paid={paid}/{shares.Count}");
    }

    /// <summary>
    /// The goal as "{kind}:{subject}:{target}" for war log and letter details. The client words it (with its
    /// plurals: "Slay 300 Boarmen"), so the server sends the parts rather than a sentence.
    /// </summary>
    private static string Parts(RealmGoal goal) => $"{goal.Kind}:{goal.Subject}:{goal.Target}";

    /// <summary>Today's goal as the quest book shows it, with this player's share and the top contributors.</summary>
    public async Task<RealmGoalResponse?> ViewAsync(Guid gameInstanceId, string userId)
    {
        var goal = await TodayAsync(gameInstanceId);
        if (goal == null) return null;

        var shares = await _context.RealmGoalShares.AsNoTracking()
            .Where(s => s.GoalId == goal.Id)
            .OrderByDescending(s => s.Count)
            .ToListAsync();
        var mine = shares.FirstOrDefault(s => s.UserId == userId);

        var topIds = shares.Take(TopShown).Select(s => s.UserId).ToList();
        var names = await _context.Users.AsNoTracking()
            .Where(u => topIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.UserName ?? "A herald");

        var top = shares.Take(TopShown)
            .Select(s => new RealmGoalContributor(names.TryGetValue(s.UserId, out var n) ? n : "A herald", s.Count, s.UserId == userId))
            .ToList();

        var chest = goal.ReachedAt != null
            ? mine?.ChestRarity
            : RealmGoalRules.ChestFor(mine?.Count ?? 0, goal.Target);

        return new RealmGoalResponse(goal.Day, goal.Kind, goal.Subject,
            RealmGoalRules.Describe(goal.Kind, goal.Subject, goal.Target), goal.Target, goal.Count,
            DateTime.SpecifyKind(RealmGoalRules.EndOf(Clock()), DateTimeKind.Utc),
            goal.ReachedAt == null ? null : DateTime.SpecifyKind(goal.ReachedAt.Value, DateTimeKind.Utc),
            mine?.Count ?? 0, chest, top);
    }

    /// <summary>Development only: moves today's goal to <paramref name="fraction"/> of its target, credited to this player.</summary>
    public async Task FillAsync(Guid gameInstanceId, string userId, double fraction)
    {
        var goal = await TodayAsync(gameInstanceId);
        if (goal == null) return;
        int wanted = (int)Math.Floor(goal.Target * Math.Clamp(fraction, 0, 1));
        int add = wanted - goal.Count;
        if (add <= 0) return;

        var deed = goal.Kind switch
        {
            RealmGoalKind.Slay => new QuestDeed { Kills = new Dictionary<string, int> { [goal.Subject ?? ""] = add } },
            _ => null,
        };
        if (deed != null)
        {
            await RecordAsync(gameInstanceId, userId, deed);
            return;
        }
        // Clear and ambush deeds count one each.
        for (int i = 0; i < add; i++)
            await RecordAsync(gameInstanceId, userId, goal.Kind == RealmGoalKind.Clear
                ? new QuestDeed { ClearedSiteId = "debug" }
                : new QuestDeed { AmbushWon = true });
    }

    /// <summary>The player's average hero level, the level their chest is rolled at (as the Hall board's).</summary>
    private async Task<int> RosterLevelAsync(Guid gameInstanceId, string userId)
    {
        var save = await MarchingArmy.LoadPlayerSaveAsync(_context, _logger, gameInstanceId, userId);
        var levels = save?.Characters?.Where(c => c != null).Select(c => (double)Math.Max(1, c.Level)).ToList();
        return levels == null || levels.Count == 0 ? 1 : Math.Max(1, (int)Math.Round(levels.Average()));
    }
}
