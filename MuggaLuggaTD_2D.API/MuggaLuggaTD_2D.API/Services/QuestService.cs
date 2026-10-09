using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Services;

public enum QuestError
{
    None,
    WorldNotFound,
    OfferNotFound,
    TooManyActive,
    QuestNotFound,
    NotDone,
    NotEnoughToBring
}

public record QuestOutcome(QuestError Error, string? Message = null)
{
    public bool Succeeded => Error == QuestError.None;
}

/// <summary>
/// Quests (<c>docs/design/quests.md</c>): the board, taking a quest, counting deeds toward it and handing it
/// in for its chest. The board is worked out by <see cref="QuestRules.Board"/>; only what the player has taken
/// and seen is stored (<see cref="QuestBoardState"/>, <see cref="PlayerQuest"/>).
///
/// <para>Deeds are recorded by the service that did the thing (<see cref="WorldPveService"/>,
/// <see cref="PartyService"/>, <see cref="AutoFightService"/>), after it has paid. <b>Recording never fails
/// the action it records</b>, as with First Steps.</para>
/// </summary>
public class QuestService
{
    private static readonly Random Dice = new();

    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly ItemLedgerService _items;
    private readonly GoldService _gold;
    private readonly MaterialWalletService _wallet;
    private readonly ISessionLog _sessionLog;
    private readonly HiringService? _hiring;
    private readonly LetterService? _letters;

    /// <summary>The clock, for tests.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public QuestService(ApplicationDbContext context, IGameContentProvider content, ItemLedgerService items,
        GoldService gold, MaterialWalletService wallet, ISessionLog sessionLog, HiringService? hiring = null,
        LetterService? letters = null)
    {
        _letters = letters;
        _context = context;
        _content = content;
        _items = items;
        _gold = gold;
        _wallet = wallet;
        _sessionLog = sessionLog;
        _hiring = hiring;
    }

    public async Task<(QuestOutcome Outcome, QuestBoardResponse? Board)> BoardAsync(Guid gameInstanceId, string userId)
    {
        var regions = await RegionsAsync(gameInstanceId);
        if (regions == null) return (new QuestOutcome(QuestError.WorldNotFound, "This realm has no world yet."), null);

        var state = await StateAsync(gameInstanceId, userId);
        await _context.SaveChangesAsync();
        return (new QuestOutcome(QuestError.None), await ResponseAsync(gameInstanceId, userId, state, regions));
    }

    /// <summary>Takes an offer from the board. It must be standing now, and there must be room in the quest log.</summary>
    public async Task<(QuestOutcome Outcome, QuestBoardResponse? Board)> AcceptAsync(Guid gameInstanceId, string userId, string offerId)
    {
        var regions = await RegionsAsync(gameInstanceId);
        if (regions == null) return (new QuestOutcome(QuestError.WorldNotFound, "This realm has no world yet."), null);

        var state = await StateAsync(gameInstanceId, userId);
        var offer = OffersOf(gameInstanceId, userId, state, regions).FirstOrDefault(o => o.Id == offerId);
        if (offer == null)
            return (new QuestOutcome(QuestError.OfferNotFound, "That quest is no longer offered."), null);

        int active = await _context.PlayerQuests.CountAsync(q =>
            q.GameInstanceId == gameInstanceId && q.UserId == userId && q.HandedInAt == null);
        if (active >= QuestRules.MaxActive)
            return (new QuestOutcome(QuestError.TooManyActive,
                $"You already have {QuestRules.MaxActive} quests. Finish or abandon one first."), null);

        var now = Clock();
        _context.PlayerQuests.Add(new PlayerQuest
        {
            GameInstanceId = gameInstanceId, UserId = userId, OfferId = offer.Id, Offer = offer, AcceptedAt = now
        });
        state.Taken = Join(state.TakenIds.Append(offer.Id));
        state.Seen = Join(state.SeenIds.Append(offer.GiverId));
        state.UpdatedAt = now;
        await _context.SaveChangesAsync();

        _sessionLog.Log("QUEST-ACCEPT", $"user={userId} realm={gameInstanceId} offer={offer.Id} kind={offer.Kind} target={offer.Target} count={offer.Count}");
        return (new QuestOutcome(QuestError.None), await ResponseAsync(gameInstanceId, userId, state, regions));
    }

    /// <summary>Gives a quest up. It does not come back to the board: the offer stays taken for its hour.</summary>
    public async Task<(QuestOutcome Outcome, QuestBoardResponse? Board)> AbandonAsync(Guid gameInstanceId, string userId, Guid questId)
    {
        var quest = await _context.PlayerQuests.FirstOrDefaultAsync(q =>
            q.Id == questId && q.GameInstanceId == gameInstanceId && q.UserId == userId && q.HandedInAt == null);
        if (quest == null) return (new QuestOutcome(QuestError.QuestNotFound, "No such quest."), null);

        _context.PlayerQuests.Remove(quest);
        await _context.SaveChangesAsync();
        _sessionLog.Log("QUEST-ABANDON", $"user={userId} realm={gameInstanceId} offer={quest.OfferId}");
        return await BoardAsync(gameInstanceId, userId);
    }

    /// <summary>
    /// Hands a quest in: a finished one, or a Gather quest whose goods the wallet holds (they are spent).
    /// Pays the chest, the gold and the materials. Once every offer on the board has been handed in, a fresh
    /// set is rolled at once.
    /// </summary>
    public async Task<(QuestOutcome Outcome, QuestHandInResponse? Response)> HandInAsync(Guid gameInstanceId, string userId, Guid questId)
    {
        var quest = await _context.PlayerQuests.FirstOrDefaultAsync(q =>
            q.Id == questId && q.GameInstanceId == gameInstanceId && q.UserId == userId && q.HandedInAt == null);
        if (quest == null) return (new QuestOutcome(QuestError.QuestNotFound, "No such quest."), null);

        var offer = quest.Offer;
        var now = Clock();
        if (offer.Kind == QuestKind.Gather)
        {
            // Goods are gathered lazily: bring in what the workers have made since the last read first.
            if (_hiring != null) await _hiring.SettlePlayerAsync(gameInstanceId, userId);
            var spent = await _wallet.SpendAsync(gameInstanceId, userId,
                new[] { new MaterialGrant { MaterialName = offer.Target, Quantity = offer.Count } }, $"quest {quest.Id}");
            if (!spent.Succeeded) return (new QuestOutcome(QuestError.NotEnoughToBring, spent.Message), null);
            quest.Progress = offer.Count;
        }
        else if (!QuestRules.IsDone(offer, quest.Progress))
        {
            return (new QuestOutcome(QuestError.NotDone, "That quest is not done yet."), null);
        }

        // Marked handed in before the chest is granted, so a second request cannot open it again meanwhile.
        quest.DoneAt ??= now;
        quest.HandedInAt = now;
        await _context.SaveChangesAsync();

        string source = $"quest {quest.Id}";
        var pieces = QuestRules.RollChest(_content.DroppableItems.ToList(), Dice, offer.Level, offer.ChestTier);
        await _items.GrantAsync(gameInstanceId, userId, pieces, source);
        await _wallet.GrantAsync(gameInstanceId, userId, offer.Materials, source);
        long balance = await _gold.GrantAsync(gameInstanceId, userId, offer.Gold, source);

        _sessionLog.Log("QUEST-HANDIN",
            $"user={userId} realm={gameInstanceId} offer={offer.Id} chest={offer.ChestTier} pieces={string.Join(",", pieces.Select(p => p.Rarity))} gold={offer.Gold}");

        var regions = await RegionsAsync(gameInstanceId) ?? new List<WorldRegionData>();
        var state = await StateAsync(gameInstanceId, userId);
        await FreshSetIfAllDoneAsync(gameInstanceId, userId, state, regions);
        await _context.SaveChangesAsync();

        return (new QuestOutcome(QuestError.None), new QuestHandInResponse(
            pieces, offer.Gold, balance, offer.Materials, await ResponseAsync(gameInstanceId, userId, state, regions)));
    }

    /// <summary>Marks givers as looked at, which takes their gold "?" off the map.</summary>
    public async Task<(QuestOutcome Outcome, QuestBoardResponse? Board)> SeenAsync(Guid gameInstanceId, string userId, IEnumerable<string> giverIds)
    {
        var regions = await RegionsAsync(gameInstanceId);
        if (regions == null) return (new QuestOutcome(QuestError.WorldNotFound, "This realm has no world yet."), null);

        var state = await StateAsync(gameInstanceId, userId);
        state.Seen = Join(state.SeenIds.Concat((giverIds ?? Enumerable.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Take(50)));
        state.UpdatedAt = Clock();
        await _context.SaveChangesAsync();
        return (new QuestOutcome(QuestError.None), await ResponseAsync(gameInstanceId, userId, state, regions));
    }

    /// <summary>Counts a deed toward every quest it serves. Idempotent per deed, and never throws.</summary>
    public async Task RecordAsync(Guid gameInstanceId, string userId, QuestDeed deed)
    {
        if (string.IsNullOrEmpty(userId) || deed == null) return;
        try
        {
            var quests = await _context.PlayerQuests
                .Where(q => q.GameInstanceId == gameInstanceId && q.UserId == userId && q.HandedInAt == null && q.DoneAt == null)
                .ToListAsync();
            if (quests.Count == 0) return;

            var now = Clock();
            bool changed = false;
            var ready = new List<PlayerQuest>();
            foreach (var quest in quests)
            {
                var offer = quest.Offer;
                int after = QuestRules.Advance(offer, quest.Progress, deed);
                if (after == quest.Progress) continue;
                quest.Progress = after;
                if (QuestRules.IsDone(offer, after))
                {
                    quest.DoneAt = now;
                    ready.Add(quest);
                }
                changed = true;
                _sessionLog.Log("QUEST-PROGRESS", $"user={userId} realm={gameInstanceId} offer={offer.Id} {after}/{offer.Count}");
            }
            if (changed) await _context.SaveChangesAsync();

            // Ready to hand in (the inbox). A Gather quest is done only at the hand-in, so it never gets here.
            if (_letters != null)
            {
                foreach (var quest in ready)
                {
                    var offer = quest.Offer;
                    await _letters.SendAsync(gameInstanceId, userId, LetterKind.QuestReady, now, $"quest:{quest.Id}:ready",
                        offer.RegionId, quest.Id.ToString(), detail: $"{offer.Kind}|{offer.Target}|{offer.Count}");
                }
            }
        }
        catch (Exception ex)
        {
            // A deed lost to a race is a deed done again; the action itself stands.
            foreach (var entry in _context.ChangeTracker.Entries<PlayerQuest>().ToList())
                entry.State = EntityState.Detached;
            _sessionLog.Log("QUEST-PROGRESS", $"user={userId} realm={gameInstanceId} not recorded: {ex.Message}");
        }
    }

    /// <summary>The kills a claim reports, held to the fight's biome and plan (<see cref="QuestRules.ClampKills"/>).</summary>
    public IReadOnlyDictionary<string, int> ClampKills(IDictionary<string, int>? reported, BiomeType biome, int tier, LocationType? site)
    {
        if (reported == null || reported.Count == 0) return new Dictionary<string, int>();
        var peoples = QuestRules.PeoplesOf(biome, _content.EnemyPeoples);
        return QuestRules.ClampKills(reported, peoples, RunRewardCalculator.PlannedEnemies(Math.Max(1, tier), _content.RunTuning, site));
    }

    /// <summary>A fight auto mode fought: its kills estimated from the plan at auto mode's share.</summary>
    public IReadOnlyDictionary<string, int> EstimatedKills(BiomeType biome, int tier, LocationType? site, double share) =>
        QuestRules.EstimatedKills(QuestRules.PeoplesOf(biome, _content.EnemyPeoples),
            RunRewardCalculator.PlannedEnemies(Math.Max(1, tier), _content.RunTuning, site), share);

    /// <summary>Debug, Development only: a fresh set now, as if every quest on the board had been handed in.</summary>
    public async Task DebugFreshSetAsync(Guid gameInstanceId, string userId)
    {
        var state = await StateAsync(gameInstanceId, userId);
        NextSet(state);
        await _context.SaveChangesAsync();
    }

    /// <summary>Debug, Development only: every quest taken is done (a Gather quest still needs its goods).</summary>
    public async Task<int> DebugFinishAsync(Guid gameInstanceId, string userId)
    {
        var quests = await _context.PlayerQuests
            .Where(q => q.GameInstanceId == gameInstanceId && q.UserId == userId && q.HandedInAt == null && q.DoneAt == null)
            .ToListAsync();
        int finished = 0;
        foreach (var quest in quests)
        {
            var offer = quest.Offer;
            if (offer.Kind == QuestKind.Gather) continue;
            quest.Progress = offer.Count;
            quest.DoneAt = Clock();
            finished++;
        }
        await _context.SaveChangesAsync();
        return finished;
    }

    /// <summary>A season reset starts the world over, so the boards and the quests go with it.</summary>
    public static async Task ResetRealmAsync(ApplicationDbContext context, Guid realmId)
    {
        context.QuestBoardStates.RemoveRange(await context.QuestBoardStates.Where(b => b.GameInstanceId == realmId).ToListAsync());
        context.PlayerQuests.RemoveRange(await context.PlayerQuests.Where(q => q.GameInstanceId == realmId).ToListAsync());
    }

    // -----------------------------------------------------------------

    /// <summary>The player's board row, created on first ask and moved on to this hour (the lists start empty in a new hour).</summary>
    private async Task<QuestBoardState> StateAsync(Guid gameInstanceId, string userId)
    {
        var state = await _context.QuestBoardStates.FirstOrDefaultAsync(b => b.GameInstanceId == gameInstanceId && b.UserId == userId);
        long hour = QuestRules.HourOf(Clock());
        if (state == null)
        {
            state = new QuestBoardState { GameInstanceId = gameInstanceId, UserId = userId, Hour = hour };
            _context.QuestBoardStates.Add(state);
        }
        else if (state.Hour != hour)
        {
            state.Hour = hour;
            state.Taken = string.Empty;
            state.Seen = string.Empty;
            state.UpdatedAt = Clock();
        }
        return state;
    }

    private List<QuestOffer> BoardOf(Guid gameInstanceId, string userId, QuestBoardState state, List<WorldRegionData> regions) =>
        QuestRules.Board(gameInstanceId.ToString(), userId, state.Hour, state.Set, regions, _content.EnemyPeoples, _content.RunTuning);

    /// <summary>The offers still standing: the board less what has been taken from it.</summary>
    private List<QuestOffer> OffersOf(Guid gameInstanceId, string userId, QuestBoardState state, List<WorldRegionData> regions)
    {
        var taken = state.TakenIds.ToHashSet(StringComparer.Ordinal);
        return BoardOf(gameInstanceId, userId, state, regions).Where(o => !taken.Contains(o.Id)).ToList();
    }

    /// <summary>Every offer on the board handed in: a fresh set at once, not at the hour (Mike).</summary>
    private async Task FreshSetIfAllDoneAsync(Guid gameInstanceId, string userId, QuestBoardState state, List<WorldRegionData> regions)
    {
        var board = BoardOf(gameInstanceId, userId, state, regions).Select(o => o.Id).ToList();
        if (board.Count == 0) return;
        var handedIn = await _context.PlayerQuests
            .Where(q => q.GameInstanceId == gameInstanceId && q.UserId == userId && q.HandedInAt != null && board.Contains(q.OfferId))
            .CountAsync();
        if (handedIn < board.Count) return;
        NextSet(state);
        _sessionLog.Log("QUEST-FRESH-SET", $"user={userId} realm={gameInstanceId} set={state.Set}");
    }

    private void NextSet(QuestBoardState state)
    {
        state.Set++;
        state.Taken = string.Empty;
        state.Seen = string.Empty;
        state.UpdatedAt = Clock();
    }

    private async Task<QuestBoardResponse> ResponseAsync(Guid gameInstanceId, string userId, QuestBoardState state, List<WorldRegionData> regions)
    {
        var active = await _context.PlayerQuests.AsNoTracking()
            .Where(q => q.GameInstanceId == gameInstanceId && q.UserId == userId && q.HandedInAt == null)
            .OrderBy(q => q.AcceptedAt)
            .ToListAsync();

        return new QuestBoardResponse(
            state.Hour,
            QuestRules.TurnsOverAt(state.Hour),
            state.Set,
            OffersOf(gameInstanceId, userId, state, regions),
            active.Select(q => new QuestEntry(q.Id, q.Offer, q.Progress, q.DoneAt != null, DateTime.SpecifyKind(q.AcceptedAt, DateTimeKind.Utc))).ToList(),
            state.SeenIds.ToList(),
            QuestRules.MaxActive);
    }

    private async Task<List<WorldRegionData>?> RegionsAsync(Guid gameInstanceId)
    {
        var row = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (row == null) return null;
        return WorldRegionBlob.ReadAllRegions(JsonNode.Parse(row.GameData));
    }

    private static string Join(IEnumerable<string> ids) => string.Join(',', ids.Distinct(StringComparer.Ordinal));
}
