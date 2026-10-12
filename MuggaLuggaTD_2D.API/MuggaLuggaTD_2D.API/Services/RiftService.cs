using System.Text.Json.Nodes;
using Enums;
using StateManagement.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Hubs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

public record RiftContributor(string Name, int Breach, bool Me);

public record RiftView(
    Guid Id, RiftKind Kind, RiftStage Stage, string RegionId, string? TargetUserId, string? TargetName,
    DateTime OmenAt, DateTime OpensAt, DateTime ClosesAt, DateTime? InvadedAt,
    int BreachMax, int Breach, DateTime? SealedAt, string? SealedByName,
    int MyBreach, ItemRarityTypes? MyChest, bool MyChestOpened, List<RiftContributor> Top,
    RiftContractView? Contract = null, RiftEruption Eruption = RiftEruption.None);

/// <summary>The rifts a realm's map shows: every live one, and the recently ended ones.</summary>
public record RiftsResponse(List<RiftView> Live, List<RiftView> Recent);

/// <summary>What the hub sends when a rift changes stage; the client asks for the rest.</summary>
public record RiftUpdatedNotification(Guid RiftId, RiftKind Kind, RiftStage Stage, string RegionId);

public record RiftChestResponse(ItemRarityTypes Rarity, List<ItemSaveData> Items);

public enum RiftChestError { None, NoSuchRift, NoChest, AlreadyOpened, NothingToRoll }

/// <summary>
/// Rifts from other realms (Invasions spec, revision R1–R7; phase 1, "the rift's life"). Announces
/// each rift when <see cref="RiftRules"/> says it is due, opens it, and when its time runs out erupts a
/// Great Rift or invades a Lesser Rift's region. Wins wear a rift's Breach down
/// (<see cref="RecordWinAsync"/>), and the win that empties it seals it and pays.
///
/// <para>Advanced once a minute by the <see cref="SiegeScheduler"/>, and by every read of a realm's
/// rifts first, so a restart never skips one. An erupting Great Rift hands its region to the Riftborn faction
/// (<see cref="FactionService.EruptAsync"/>, phase 6): wild land seized, held land besieged, or past the cap a surge.</para>
/// </summary>
public partial class RiftService
{
    private const int TopShown = 10;
    private static readonly TimeSpan RecentFor = TimeSpan.FromHours(24);

    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly ItemLedgerService _items;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<RiftService> _logger;
    private readonly IHubContext<GameHub> _hub;
    private readonly RealmPresence _presence;
    private readonly WarLogService? _warLog;
    private readonly LetterService? _letters;
    private readonly SeasonScoreService? _scores;
    private readonly GoldService? _gold;
    private readonly FactionService? _factions;

    private static readonly Random Dice = new();

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public RiftService(ApplicationDbContext context, IGameContentProvider content, ItemLedgerService items,
        ISessionLog sessionLog, ILogger<RiftService> logger, IHubContext<GameHub> hub, RealmPresence presence,
        WarLogService? warLog = null, LetterService? letters = null, SeasonScoreService? scores = null, GoldService? gold = null,
        FactionService? factions = null)
    {
        _factions = factions;
        _context = context;
        _content = content;
        _items = items;
        _sessionLog = sessionLog;
        _logger = logger;
        _hub = hub;
        _presence = presence;
        _warLog = warLog;
        _letters = letters;
        _scores = scores;
        _gold = gold;
    }

    // -----------------------------------------------------------------
    // The rift's life
    // -----------------------------------------------------------------

    /// <summary>Advances every realm with a world. Returns how many rifts were announced or moved on.</summary>
    public async Task<int> AdvanceAllAsync()
    {
        var realms = await _context.WorldViewGameData.AsNoTracking().Select(w => w.GameInstanceId).ToListAsync();
        int changed = 0;
        foreach (var realm in realms)
        {
            try
            {
                changed += await AdvanceAsync(realm);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not advance the rifts of {Instance}.", realm);
            }
        }
        return changed;
    }

    /// <summary>
    /// Moves this realm's rifts on to where the clock says they are, then announces the next of each
    /// kind if it is due. Returns how many rifts changed.
    /// </summary>
    public async Task<int> AdvanceAsync(Guid gameInstanceId)
    {
        var now = Clock();
        var instance = await _context.GameInstances.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        if (instance == null || instance.SeasonHasExpired(now)) return 0;

        int changed = 0;
        var live = await _context.Rifts
            .Where(r => r.GameInstanceId == gameInstanceId && r.SeasonNumber == instance.SeasonNumber &&
                        (r.Stage == RiftStage.Omen || r.Stage == RiftStage.Open))
            .ToListAsync();
        foreach (var rift in live)
            if (await MoveOnAsync(rift, now)) changed++;
        changed += await LapseHoldsAsync(gameInstanceId, now);

        var world = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (world == null) return changed;
        var regions = WorldRegionBlob.ReadAllRegions(JsonNode.Parse(world.GameData));

        changed += await FollowTheLandAsync(gameInstanceId, instance.SeasonNumber, regions);

        int active = await ActivePlayersAsync(gameInstanceId, now);
        if (active == 0) return changed;

        foreach (var kind in new[] { RiftKind.Great, RiftKind.Lesser })
            if (await AnnounceIfDueAsync(instance, kind, regions, active, now) != null) changed++;
        return changed;
    }

    /// <summary>
    /// A Lesser Rift and its invasion go with the land (R4): when its region has a new player lord, the rift is
    /// theirs to close. Land no player holds keeps the rift's last lord.
    /// </summary>
    private async Task<int> FollowTheLandAsync(Guid gameInstanceId, int season, IReadOnlyList<WorldRegionData> regions)
    {
        var lesser = await _context.Rifts
            .Where(r => r.GameInstanceId == gameInstanceId && r.SeasonNumber == season && r.Kind == RiftKind.Lesser &&
                        (r.Stage == RiftStage.Omen || r.Stage == RiftStage.Open || r.Stage == RiftStage.Invaded))
            .ToListAsync();
        int moved = 0;
        foreach (var rift in lesser)
        {
            var region = regions.FirstOrDefault(r => r.RegionId == rift.RegionId);
            if (region == null || region.Ownership != LocationOwnership.Player || string.IsNullOrEmpty(region.OwnerUserId)) continue;
            if (string.Equals(region.OwnerUserId, rift.TargetUserId, StringComparison.Ordinal)) continue;

            string? was = rift.TargetUserId;
            rift.TargetUserId = region.OwnerUserId;
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await _context.Entry(rift).ReloadAsync();
                continue;
            }
            moved++;
            _sessionLog.Log("RIFT", $"realm={gameInstanceId} rift={rift.Id} region={rift.RegionId} lord {was} -> {rift.TargetUserId}");
            // The old lord's contract was theirs to make (R7): it ends, and its gold goes back.
            await VoidForNewLordAsync(rift);
            await BroadcastAsync(rift);
        }
        return moved;
    }

    /// <summary>
    /// The realm's invasions (R4), ongoing and ended, for the rules that ask whether a region was invaded at a
    /// moment: its roads' ambushes and its diggings. A season's reset deletes its rifts, so these are this season's.
    /// </summary>
    public static async Task<List<Invasion>> InvasionsAsync(ApplicationDbContext context, Guid gameInstanceId)
    {
        var rows = await context.Rifts.AsNoTracking()
            .Where(r => r.GameInstanceId == gameInstanceId && r.Kind == RiftKind.Lesser && r.InvadedAt != null)
            .Select(r => new { r.Id, r.RegionId, r.InvadedAt, r.Stage, r.SealedAt })
            .ToListAsync();
        return rows.Select(r => new Invasion
        {
            RiftId = r.Id.ToString(),
            RegionId = r.RegionId,
            From = Utc(r.InvadedAt!.Value),
            Until = r.Stage == RiftStage.Invaded ? null : Utc(r.SealedAt) ?? Utc(r.InvadedAt!.Value),
        }).ToList();
    }

    /// <summary>One rift's stage, brought up to <paramref name="now"/>. False if nothing was due, or another request moved it first.</summary>
    private async Task<bool> MoveOnAsync(Rift rift, DateTime now)
    {
        var was = rift.Stage;
        if (rift.Stage == RiftStage.Omen && now >= rift.OpensAt) rift.Stage = RiftStage.Open;
        if (rift.Stage == RiftStage.Open && now >= rift.ClosesAt)
        {
            if (rift.Kind == RiftKind.Great)
            {
                rift.Stage = RiftStage.Erupted;
            }
            else
            {
                rift.Stage = RiftStage.Invaded;
                rift.InvadedAt = rift.ClosesAt;
            }
        }
        if (rift.Stage == was) return false;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // The scheduler and a read moved it at once; theirs is the same move, and they announce it.
            await _context.Entry(rift).ReloadAsync();
            return false;
        }

        if (rift.Stage == RiftStage.Erupted) await EruptAsync(rift);
        await AnnounceStageAsync(rift, was);
        return true;
    }

    /// <summary>
    /// What an erupted Great Rift does to its region (R8): wild land becomes the Riftborn's, held land gets a Riftborn
    /// siege sized by the Breach left, and past the season's cap on land from eruptions the Riftborn surge instead.
    /// Only the request that moved the rift to Erupted gets here, so it happens once.
    /// </summary>
    private async Task EruptAsync(Rift rift)
    {
        if (_factions == null) return;
        try
        {
            int landErupted = await _context.Rifts.AsNoTracking().CountAsync(r =>
                r.GameInstanceId == rift.GameInstanceId && r.SeasonNumber == rift.SeasonNumber && r.Id != rift.Id &&
                (r.Eruption == RiftEruption.Seized || r.Eruption == RiftEruption.Besieged));
            rift.Eruption = await _factions.EruptAsync(rift.GameInstanceId, rift.RegionId, rift.Breach, rift.BreachMax,
                landErupted, rift.ClosesAt);
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The eruption of rift {Rift} could not seat the Riftborn.", rift.Id);
        }
    }

    /// <summary>The war log, letters and broadcast for a rift that has just moved from <paramref name="was"/>.</summary>
    private async Task AnnounceStageAsync(Rift rift, RiftStage was)
    {
        var at = rift.Stage switch
        {
            RiftStage.Open => rift.OpensAt,
            RiftStage.Erupted or RiftStage.Invaded => rift.ClosesAt,
            _ => Clock(),
        };

        if (_warLog != null)
        {
            // A Great Rift is the realm's news at every turn; a Lesser Rift only when it goes wrong.
            if (rift.Kind == RiftKind.Great && rift.Stage == RiftStage.Open && was == RiftStage.Omen)
                await _warLog.RecordAsync(rift.GameInstanceId, WarLogKind.RiftOpened, null, null, rift.RegionId, "Great", at);
            if (rift.Stage == RiftStage.Erupted)
                await _warLog.RecordAsync(rift.GameInstanceId, WarLogKind.RiftErupted, null, null, rift.RegionId,
                    rift.Eruption == RiftEruption.None ? "Great" : rift.Eruption.ToString(), at);
            if (rift.Stage == RiftStage.Invaded)
                await _warLog.RecordAsync(rift.GameInstanceId, WarLogKind.RegionInvaded, null, rift.TargetUserId, rift.RegionId, "Lesser", at);
        }

        if (_letters != null && rift.Stage == RiftStage.Invaded && rift.TargetUserId != null)
            await _letters.SendAsync(rift.GameInstanceId, rift.TargetUserId, LetterKind.RegionInvaded, at,
                $"rift:{rift.Id}:invaded", rift.RegionId, rift.Id.ToString());

        // Those told it was coming hear what it did (a besieged lord also has the siege's own letter).
        if (_letters != null && rift.Stage == RiftStage.Erupted && rift.Eruption != RiftEruption.None)
        {
            var world = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == rift.GameInstanceId);
            if (world != null)
                foreach (var neighbour in NeighboursOf(rift.RegionId, WorldRegionBlob.ReadAllRegions(JsonNode.Parse(world.GameData))))
                    await _letters.SendAsync(rift.GameInstanceId, neighbour, LetterKind.RiftErupted, at,
                        $"rift:{rift.Id}:erupted", rift.RegionId, rift.Id.ToString(), detail: rift.Eruption.ToString());
        }

        _sessionLog.Log("RIFT", $"realm={rift.GameInstanceId} rift={rift.Id} kind={rift.Kind} region={rift.RegionId} {was}->{rift.Stage}");
        await BroadcastAsync(rift);
    }

    /// <summary>
    /// Announces the next rift of <paramref name="kind"/> if its omen is due: a Great Rift once the last
    /// is over, a Lesser Rift in an online player's region. Null if none was announced.
    /// </summary>
    private async Task<Rift?> AnnounceIfDueAsync(GameInstance instance, RiftKind kind, IReadOnlyList<WorldRegionData> regions,
        int activePlayers, DateTime now)
    {
        var mine = await _context.Rifts.AsNoTracking()
            .Where(r => r.GameInstanceId == instance.Id && r.SeasonNumber == instance.SeasonNumber && r.Kind == kind)
            .ToListAsync();

        // Only one Great Rift at a time.
        if (kind == RiftKind.Great && mine.Any(r => RiftRules.IsLive(r.Stage))) return null;

        var last = mine.OrderByDescending(r => r.EventNumber).FirstOrDefault();
        int number = (last?.EventNumber ?? 0) + 1;
        string realmId = instance.Id.ToString();

        var due = RiftRules.DueToOpen(kind, realmId, number, instance.SeasonStartedAt, last?.OpensAt, last?.ClosesAt);
        if (now < due - RiftRules.OmenLeadOf(kind)) return null;

        var opens = RiftRules.OpensAt(kind, due, now);
        if (!RiftRules.InSeason(kind, opens, instance.SeasonStartedAt, instance.SeasonEndsAt)) return null;

        WorldRegionData? region;
        if (kind == RiftKind.Great)
        {
            region = RiftRules.PickGreat(regions, realmId, number);
        }
        else
        {
            var online = _presence.SeenSince(instance.Id, now - RiftRules.OnlineWithin);
            var withRift = mine.Where(r => RiftRules.IsLive(r.Stage) && r.TargetUserId != null)
                .Select(r => r.TargetUserId!).ToHashSet();
            region = RiftRules.PickLesser(RiftRules.LesserCandidates(regions, online, withRift), realmId, number);
        }
        if (region == null) return null;

        int breach = kind == RiftKind.Great ? RiftRules.BreachMax(activePlayers) : 1;
        var rift = new Rift
        {
            GameInstanceId = instance.Id,
            SeasonNumber = instance.SeasonNumber,
            Kind = kind,
            EventNumber = number,
            RegionId = region.RegionId,
            TargetUserId = kind == RiftKind.Lesser ? region.OwnerUserId : null,
            Stage = RiftStage.Omen,
            OmenAt = now,
            OpensAt = opens,
            ClosesAt = opens + RiftRules.OpenForOf(kind),
            BreachMax = breach,
            Breach = breach,
        };
        if (!await TryAddAsync(rift)) return null;

        await AnnounceOmenAsync(rift, regions);
        return rift;
    }

    /// <summary>Adds a new rift; false if another request announced the same one first (the unique number).</summary>
    private async Task<bool> TryAddAsync(Rift rift)
    {
        _context.Rifts.Add(rift);
        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            _context.Entry(rift).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>The war log and letters for a newly announced rift: its holder for a Lesser Rift, its neighbours for a Great one.</summary>
    private async Task AnnounceOmenAsync(Rift rift, IReadOnlyList<WorldRegionData> regions)
    {
        if (rift.Kind == RiftKind.Great)
        {
            if (_warLog != null)
                await _warLog.RecordAsync(rift.GameInstanceId, WarLogKind.RiftOmen, null, null, rift.RegionId, "Great", rift.OmenAt);

            if (_letters != null)
                foreach (var neighbour in NeighboursOf(rift.RegionId, regions))
                    await _letters.SendAsync(rift.GameInstanceId, neighbour, LetterKind.RiftNear, rift.OmenAt,
                        $"rift:{rift.Id}:near", rift.RegionId, rift.Id.ToString());
        }
        else if (_letters != null && rift.TargetUserId != null)
        {
            await _letters.SendAsync(rift.GameInstanceId, rift.TargetUserId, LetterKind.RiftOmen, rift.OmenAt,
                $"rift:{rift.Id}:omen", rift.RegionId, rift.Id.ToString());
        }

        _sessionLog.Log("RIFT", $"realm={rift.GameInstanceId} rift={rift.Id} kind={rift.Kind} #{rift.EventNumber} " +
            $"region={rift.RegionId} omen opens={rift.OpensAt:O} breach={rift.BreachMax} target={rift.TargetUserId}");
        await BroadcastAsync(rift);
    }

    /// <summary>The players who hold the rift's region or one next to it.</summary>
    private static HashSet<string> NeighboursOf(string regionId, IReadOnlyList<WorldRegionData> regions)
    {
        var at = regions.FirstOrDefault(r => r.RegionId == regionId);
        var players = new HashSet<string>(StringComparer.Ordinal);
        if (at == null) return players;
        foreach (var r in regions)
            if (r.Ownership == LocationOwnership.Player && !string.IsNullOrEmpty(r.OwnerUserId) &&
                HexCoord.Distance(r.Hex, at.Hex) <= 1)
                players.Add(r.OwnerUserId);
        return players;
    }

    /// <summary>The realm's players seen in the last day: by a save, or by being connected.</summary>
    private async Task<int> ActivePlayersAsync(Guid gameInstanceId, DateTime now)
    {
        var since = now - RiftRules.DeadRealmAfter;
        var saved = await _context.PlayerGameData.AsNoTracking()
            .Where(p => p.GameInstanceId == gameInstanceId && p.UpdatedAt >= since)
            .Select(p => p.UserId).ToListAsync();
        var seen = _presence.SeenSince(gameInstanceId, since);
        seen.UnionWith(saved);
        return seen.Count;
    }

    // -----------------------------------------------------------------
    // Wearing it down, and sealing it
    // -----------------------------------------------------------------

    /// <summary>
    /// A win against the rift: takes up to <paramref name="breach"/> off it (all of a Lesser Rift's one
    /// point), credits the player, and seals it if that emptied it. Returns what was taken, or 0 if the
    /// rift could not be fought. Two wins at once are both counted, and only one of them seals it.
    /// Who may fight it is the caller's to check.
    /// </summary>
    public async Task<int> RecordWinAsync(Guid riftId, string userId, int breach)
    {
        int taken = 0;
        bool sealedNow = false;
        Rift? rift = null;

        await Concurrency.RetryAsync(_context, async () =>
        {
            await Concurrency.RefreshAsync<Rift>(_context, r => r.Id == riftId);
            rift = await _context.Rifts.FirstOrDefaultAsync(r => r.Id == riftId);
            taken = 0;
            sealedNow = false;
            if (rift == null || !RiftRules.CanSeal(rift.Stage) || rift.Breach <= 0) return;

            var mine = _context.RiftContributions.Local.FirstOrDefault(c => c.RiftId == riftId && c.UserId == userId)
                ?? await _context.RiftContributions.FirstOrDefaultAsync(c => c.RiftId == riftId && c.UserId == userId);
            if (mine == null)
            {
                mine = new RiftContribution { RiftId = riftId, UserId = userId };
                _context.RiftContributions.Add(mine);
            }

            var now = Clock();
            taken = rift.Kind == RiftKind.Lesser ? rift.Breach : Math.Clamp(breach, 0, rift.Breach);
            rift.Breach -= taken;
            mine.Breach += taken;
            mine.Wins++;
            mine.LastWinAt = now;

            if (rift.Breach == 0)
            {
                sealedNow = true;
                rift.Stage = RiftStage.Sealed;
                rift.SealedAt = now;
                rift.SealedByUserId = userId;
            }
            await _context.SaveChangesAsync();
        });

        if (sealedNow && rift != null)
        {
            if (rift.Kind == RiftKind.Lesser) await SettleContractAsync(rift, userId);
            await PayAsync(rift);
        }
        else if (taken > 0 && rift != null) await BroadcastAsync(rift);   // the Breach bars move for everyone
        return taken;
    }

    /// <summary>
    /// A sealed rift's pay: each contributor's chest (rolled when opened), season points, a letter, and
    /// the war log line. A Lesser Rift pays its sealer alone.
    /// </summary>
    private async Task PayAsync(Rift rift)
    {
        var shares = await _context.RiftContributions.Where(c => c.RiftId == rift.Id).ToListAsync();
        var ranked = shares.Where(s => s.Breach > 0).OrderByDescending(s => s.Breach).ToList();

        foreach (var (share, index) in ranked.Select((s, i) => (s, i)))
        {
            double part = rift.BreachMax <= 0 ? 0 : (double)share.Breach / rift.BreachMax;
            share.ChestRarity = rift.Kind == RiftKind.Great ? RiftRules.GreatChestFor(part, index + 1) : RiftRules.LesserChest;
        }
        await Concurrency.RetryAsync(_context, () => _context.SaveChangesAsync());

        foreach (var share in ranked)
        {
            double part = rift.BreachMax <= 0 ? 0 : (double)share.Breach / rift.BreachMax;
            double points = rift.Kind == RiftKind.Great ? RiftRules.GreatSealPoints(part) : RiftRules.LesserSealPoints;
            if (_scores != null) await _scores.AwardAsync(rift.GameInstanceId, share.UserId, SeasonDeed.RiftSealed, points);

            if (_letters != null && share.ChestRarity != null)
                await _letters.SendAsync(rift.GameInstanceId, share.UserId, LetterKind.RiftSealed, rift.SealedAt ?? Clock(),
                    $"rift:{rift.Id}:sealed", rift.RegionId, rift.Id.ToString(),
                    detail: $"{rift.Kind}:{share.ChestRarity}:{(int)Math.Round(part * 100)}");
        }

        if (_warLog != null)
            await _warLog.RecordAsync(rift.GameInstanceId, WarLogKind.RiftSealed, rift.SealedByUserId,
                rift.Kind == RiftKind.Lesser ? rift.TargetUserId : null, rift.RegionId, rift.Kind.ToString(), rift.SealedAt);

        _sessionLog.Log("RIFT", $"realm={rift.GameInstanceId} rift={rift.Id} kind={rift.Kind} region={rift.RegionId} " +
            $"sealed by={rift.SealedByUserId} paid={ranked.Count}");
        await BroadcastAsync(rift);
    }

    /// <summary>Opens this player's chest from a sealed rift: rolled now, at their roster's level, as a quest chest is.</summary>
    public async Task<(RiftChestError Error, RiftChestResponse? Chest)> OpenChestAsync(Guid gameInstanceId, string userId, Guid riftId)
    {
        var rift = await _context.Rifts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == riftId && r.GameInstanceId == gameInstanceId);
        if (rift == null) return (RiftChestError.NoSuchRift, null);

        RiftContribution? mine = null;
        var opened = await Concurrency.RetryAsync(_context, async () =>
        {
            await Concurrency.RefreshAsync<RiftContribution>(_context, c => c.RiftId == riftId && c.UserId == userId);
            mine = await _context.RiftContributions.FirstOrDefaultAsync(c => c.RiftId == riftId && c.UserId == userId);
            if (mine?.ChestRarity == null) return RiftChestError.NoChest;
            if (mine.ChestOpenedAt != null) return RiftChestError.AlreadyOpened;
            mine.ChestOpenedAt = Clock();
            await _context.SaveChangesAsync();
            return RiftChestError.None;
        });
        if (opened != RiftChestError.None) return (opened, null);

        var rarity = mine!.ChestRarity!.Value;
        int level = await RealmGoalService.RosterLevelAsync(_context, _logger, gameInstanceId, userId);
        var pieces = QuestRules.RollChest(_content.DroppableItems.ToList(), Dice, level, rarity);
        if (pieces.Count == 0) return (RiftChestError.NothingToRoll, null);

        await _items.GrantAsync(gameInstanceId, userId, pieces, $"rift {riftId}");
        _sessionLog.Log("RIFT-CHEST", $"realm={gameInstanceId} rift={riftId} user={userId} rarity={rarity} items={pieces.Count}");
        return (RiftChestError.None, new RiftChestResponse(rarity, pieces));
    }

    // -----------------------------------------------------------------
    // Reading
    // -----------------------------------------------------------------

    /// <summary>The realm's live rifts and those ended in the last day, with this player's part in each.</summary>
    public async Task<RiftsResponse> ViewAsync(Guid gameInstanceId, string userId)
    {
        await AdvanceAsync(gameInstanceId);

        var now = Clock();
        var season = await _context.GameInstances.AsNoTracking()
            .Where(g => g.Id == gameInstanceId).Select(g => g.SeasonNumber).FirstOrDefaultAsync();
        var rifts = await _context.Rifts.AsNoTracking()
            .Where(r => r.GameInstanceId == gameInstanceId && r.SeasonNumber == season)
            .ToListAsync();

        var recentSince = now - RecentFor;
        var live = rifts.Where(r => RiftRules.IsLive(r.Stage)).OrderBy(r => r.OpensAt).ToList();
        var recent = rifts.Where(r => !RiftRules.IsLive(r.Stage) && (r.SealedAt ?? r.ClosesAt) >= recentSince)
            .OrderByDescending(r => r.SealedAt ?? r.ClosesAt).ToList();

        var ids = live.Concat(recent).Select(r => r.Id).ToList();
        var shares = await _context.RiftContributions.AsNoTracking().Where(c => ids.Contains(c.RiftId)).ToListAsync();
        var contracts = await _context.RiftContracts.AsNoTracking()
            .Where(c => ids.Contains(c.RiftId) && (c.Status == RiftContractStatus.Posted || c.Status == RiftContractStatus.Taken))
            .ToListAsync();

        var people = shares.Select(s => s.UserId)
            .Concat(live.Concat(recent).SelectMany(r => new[] { r.TargetUserId, r.SealedByUserId }))
            .Concat(contracts.SelectMany(c => new[] { c.OwnerUserId, c.HolderUserId }))
            .Where(id => id != null).Distinct().ToList();
        var names = await _context.Users.AsNoTracking()
            .Where(u => people.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.UserName ?? "A herald");
        string? NameOf(string? id) => id == null ? null : names.TryGetValue(id, out var n) ? n : "A herald";

        RiftView View(Rift r)
        {
            var theirs = shares.Where(s => s.RiftId == r.Id).OrderByDescending(s => s.Breach).ToList();
            var me = theirs.FirstOrDefault(s => s.UserId == userId);
            var top = theirs.Take(TopShown).Select(s => new RiftContributor(NameOf(s.UserId)!, s.Breach, s.UserId == userId)).ToList();
            return new RiftView(r.Id, r.Kind, r.Stage, r.RegionId, r.TargetUserId, NameOf(r.TargetUserId),
                Utc(r.OmenAt), Utc(r.OpensAt), Utc(r.ClosesAt), Utc(r.InvadedAt),
                r.BreachMax, r.Breach, Utc(r.SealedAt), NameOf(r.SealedByUserId),
                me?.Breach ?? 0, me?.ChestRarity, me?.ChestOpenedAt != null, top, ContractOf(r), r.Eruption);
        }

        RiftContractView? ContractOf(Rift r)
        {
            var c = contracts.Where(x => x.RiftId == r.Id).OrderByDescending(x => x.PostedAt).FirstOrDefault();
            if (c == null) return null;
            return new RiftContractView(c.Kind, c.Status, c.Price, NameOf(c.OwnerUserId)!, NameOf(c.HolderUserId),
                Utc(c.PostedAt), Utc(c.HeldUntil), c.OwnerUserId == userId, c.HolderUserId == userId);
        }

        return new RiftsResponse(live.Select(View).ToList(), recent.Select(View).ToList());
    }

    private static DateTime Utc(DateTime at) => DateTime.SpecifyKind(at, DateTimeKind.Utc);
    private static DateTime? Utc(DateTime? at) => at == null ? null : Utc(at.Value);

    private async Task BroadcastAsync(Rift rift)
    {
        try
        {
            await _hub.Clients.Group(rift.GameInstanceId.ToString())
                .SendAsync("RiftUpdated", new RiftUpdatedNotification(rift.Id, rift.Kind, rift.Stage, rift.RegionId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not broadcast rift {Rift}.", rift.Id);
        }
    }

    /// <summary>A new season's map has new regions: last season's rifts go with the old one.</summary>
    public static async Task ResetRealmAsync(ApplicationDbContext context, Guid gameInstanceId)
    {
        var rifts = context.Rifts.Where(r => r.GameInstanceId == gameInstanceId);
        context.RiftContributions.RemoveRange(context.RiftContributions.Where(c => rifts.Any(r => r.Id == c.RiftId)));
        context.Rifts.RemoveRange(rifts);
        await Task.CompletedTask;
    }

    // -----------------------------------------------------------------
    // Debug (Development only; Combat Debug → Rifts)
    // -----------------------------------------------------------------

    /// <summary>
    /// Opens a rift now, with no omen: a Lesser Rift in one of this player's regions (or the one named),
    /// or a Great Rift where the rules would put it (or the one named). Null with a reason if it cannot.
    /// </summary>
    public async Task<(Rift? Rift, string? Error)> DebugOpenAsync(Guid gameInstanceId, string userId, RiftKind kind, string? regionId)
    {
        var now = Clock();
        var instance = await _context.GameInstances.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        var world = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (instance == null || world == null) return (null, "This realm has no world yet.");
        var regions = WorldRegionBlob.ReadAllRegions(JsonNode.Parse(world.GameData));

        var mine = await _context.Rifts.AsNoTracking()
            .Where(r => r.GameInstanceId == gameInstanceId && r.SeasonNumber == instance.SeasonNumber && r.Kind == kind)
            .ToListAsync();
        if (kind == RiftKind.Great && mine.Any(r => RiftRules.IsLive(r.Stage)))
            return (null, "A Great Rift is already live. Seal it or erupt it first.");

        WorldRegionData? region = regionId != null ? regions.FirstOrDefault(r => r.RegionId == regionId) : null;
        if (region == null)
        {
            region = kind == RiftKind.Great
                ? RiftRules.PickGreat(regions, gameInstanceId.ToString(), mine.Count + 1)
                : regions.Where(r => r.IsOwnedByPlayer(userId)).OrderBy(r => r.IsCapital).FirstOrDefault();
        }
        if (region == null) return (null, kind == RiftKind.Lesser ? "You hold no region." : "No region qualifies.");

        int breach = kind == RiftKind.Great ? RiftRules.BreachMax(await ActivePlayersAsync(gameInstanceId, now)) : 1;
        var rift = new Rift
        {
            GameInstanceId = gameInstanceId,
            SeasonNumber = instance.SeasonNumber,
            Kind = kind,
            EventNumber = (mine.Count == 0 ? 0 : mine.Max(r => r.EventNumber)) + 1,
            RegionId = region.RegionId,
            TargetUserId = kind == RiftKind.Lesser ? region.OwnerUserId : null,
            Stage = RiftStage.Open,
            OmenAt = now,
            OpensAt = now,
            ClosesAt = now + RiftRules.OpenForOf(kind),
            BreachMax = breach,
            Breach = breach,
        };
        if (!await TryAddAsync(rift)) return (null, "Another rift was announced at the same moment; try again.");

        await AnnounceOmenAsync(rift, regions);
        await AnnounceStageAsync(rift, RiftStage.Omen);
        return (rift, null);
    }

    /// <summary>The newest live rift of a kind (a Lesser Rift of this player's first, if they have one).</summary>
    public async Task<Rift?> DebugNewestAsync(Guid gameInstanceId, string userId, RiftKind kind)
    {
        var live = await _context.Rifts
            .Where(r => r.GameInstanceId == gameInstanceId && r.Kind == kind &&
                        (r.Stage == RiftStage.Omen || r.Stage == RiftStage.Open || r.Stage == RiftStage.Invaded))
            .OrderByDescending(r => r.OmenAt)
            .ToListAsync();
        return live.FirstOrDefault(r => r.TargetUserId == userId) ?? live.FirstOrDefault();
    }

    /// <summary>Runs the rift's clock out now: a Great Rift erupts, a Lesser Rift's region is invaded.</summary>
    public async Task DebugTimeOutAsync(Rift rift)
    {
        var now = Clock();
        if (rift.Stage == RiftStage.Omen) rift.OpensAt = now;
        rift.ClosesAt = now;
        await _context.SaveChangesAsync();
        await MoveOnAsync(rift, now);
    }

    /// <summary>Sets a live Great Rift's Breach to a fraction of its whole, as if others had fought it.</summary>
    public async Task DebugSetBreachAsync(Rift rift, double fraction)
    {
        rift.Breach = Math.Max(1, (int)Math.Round(rift.BreachMax * Math.Clamp(fraction, 0, 1)));
        await _context.SaveChangesAsync();
        await BroadcastAsync(rift);
    }
}
