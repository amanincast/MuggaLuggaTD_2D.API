using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

public enum HiringError
{
    None,
    NoSuchSlot,
    NoBeds,
    CannotAfford,
    NoSuchWorker,
    NoSuchSite,
    NotYourLand,
    WrongTrade,
    SiteFull,
    NoWorld,
    KeepFull
}

public record HiringOutcome(HiringError Error, string? Message = null)
{
    public bool Succeeded => Error == HiringError.None;
}

/// <summary>A resource site as the Hiring Hall sees it: where it is, what it yields, how full it is.</summary>
public record HiringSite(string SiteId, string RegionId, ResourceTrade Trade, int Tier, BiomeType Biome, int Slots, int Used);

/// <summary>
/// The Hiring Hall (design 12e; plan docs/design/hiring-hall.md in the Unity repo). Locals are hired
/// with gold from a six-seat board and sent to resource sites in regions their employer holds, where
/// they gather goods into the material wallet.
///
/// <para><b>Output is lazy, like gold</b>: a worker carries a rate and a since-when, and
/// <see cref="SettleAllAsync"/> pays what they have gathered. It is called from
/// <see cref="SeasonScoreService.SettleAllAsync"/>, beside gold, because a region changing hands is
/// exactly when output must be settled and the workers there sent home; and before anything reads
/// or spends a player's goods.</para>
/// </summary>
public class HiringService
{
    private readonly ApplicationDbContext _context;
    private readonly MaterialWalletService _wallet;
    private readonly GoldService _gold;
    private readonly ISessionLog _sessionLog;

    /// <summary>Overridable so tests can roll a board they know.</summary>
    public Random Dice { get; set; } = Random.Shared;

    /// <summary>Overridable so tests can move time.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public HiringService(ApplicationDbContext context, MaterialWalletService wallet, GoldService gold, ISessionLog sessionLog)
    {
        _context = context;
        _wallet = wallet;
        _gold = gold;
        _sessionLog = sessionLog;
    }

    // -----------------------------------------------------------------
    // The board
    // -----------------------------------------------------------------

    /// <summary>
    /// The board, with whoever has arrived since it was last read: one local into a free seat every
    /// <see cref="HiringRules.ArrivalInterval"/>. A first visit rolls a full board.
    /// </summary>
    public async Task<List<HiringCandidate>> ReadBoardAsync(Guid gameInstanceId, string userId)
    {
        var now = Clock();
        var state = await StateAsync(gameInstanceId, userId);
        var board = await LoadBoardAsync(gameInstanceId, userId);
        var regions = await RegionsAsync(gameInstanceId);

        if (board.Count == 0 && state.UpdatedAt == default)
        {
            await FillAsync(gameInstanceId, userId, board, HiringRules.BoardSize, regions, now);
            state.LastArrivalAt = now;
        }
        else
        {
            long due = (now - state.LastArrivalAt).Ticks / HiringRules.ArrivalInterval.Ticks;
            if (due > 0)
            {
                int free = HiringRules.BoardSize - board.Count;
                await FillAsync(gameInstanceId, userId, board, (int)Math.Min(due, free), regions, now);
                // Arrivals into a full room are not banked: the clock keeps its phase, nothing more.
                state.LastArrivalAt += TimeSpan.FromTicks(HiringRules.ArrivalInterval.Ticks * due);
            }
        }

        state.UpdatedAt = now;
        await _context.SaveChangesAsync();
        return board.OrderBy(c => c.Slot).ToList();
    }

    public async Task<DateTime> NextArrivalAsync(Guid gameInstanceId, string userId)
        => (await StateAsync(gameInstanceId, userId)).LastArrivalAt + HiringRules.ArrivalInterval;

    public async Task<long> RefreshCostAsync(Guid gameInstanceId, string userId)
        => HiringRules.RefreshCostFor((await StateAsync(gameInstanceId, userId)).RefreshesSinceClear);

    /// <summary>A paid refresh: a whole new board. Doubles in price until a dungeon is cleared.</summary>
    public async Task<HiringOutcome> RefreshAsync(Guid gameInstanceId, string userId)
    {
        var state = await StateAsync(gameInstanceId, userId);
        long cost = HiringRules.RefreshCostFor(state.RefreshesSinceClear);
        var paid = await _gold.SpendAsync(gameInstanceId, userId, cost, "hiring-refresh");
        if (!paid.Succeeded) return new HiringOutcome(HiringError.CannotAfford, paid.Message);

        var board = await LoadBoardAsync(gameInstanceId, userId);
        _context.HiringCandidates.RemoveRange(board);
        board.Clear();
        await FillAsync(gameInstanceId, userId, board, HiringRules.BoardSize, await RegionsAsync(gameInstanceId), Clock());

        state.RefreshesSinceClear++;
        state.UpdatedAt = Clock();
        await _context.SaveChangesAsync();
        _sessionLog.Log("HIRING-REFRESH", $"user={userId} realm={gameInstanceId} cost={cost}");
        return new HiringOutcome(HiringError.None);
    }

    /// <summary>A cleared dungeon or portal puts the refresh back to its base price, as at the Tavern.</summary>
    public async Task ResetRefreshAsync(Guid gameInstanceId, string userId)
    {
        var state = await _context.HiringStates.FirstOrDefaultAsync(s => s.GameInstanceId == gameInstanceId && s.UserId == userId);
        if (state == null || state.RefreshesSinceClear == 0) return;
        state.RefreshesSinceClear = 0;
        await _context.SaveChangesAsync();
    }

    /// <summary>Hires the local in a seat, if there is a bed for them and the gold to pay.</summary>
    public async Task<(HiringOutcome Outcome, HiredWorker? Worker)> HireAsync(Guid gameInstanceId, string userId, int slot)
    {
        var candidate = await _context.HiringCandidates.FirstOrDefaultAsync(c =>
            c.GameInstanceId == gameInstanceId && c.UserId == userId && c.Slot == slot);
        if (candidate == null) return (new HiringOutcome(HiringError.NoSuchSlot, "Nobody is sitting there."), null);

        var (beds, used) = await BedsAsync(gameInstanceId, userId);
        if (used >= beds)
            return (new HiringOutcome(HiringError.NoBeds,
                $"Every bed is taken ({used} of {beds}). Hold more land to house more workers."), null);

        long cost = HiringRules.CostOf(candidate.Tier);
        var paid = await _gold.SpendAsync(gameInstanceId, userId, cost, $"hiring slot={slot}");
        if (!paid.Succeeded) return (new HiringOutcome(HiringError.CannotAfford, paid.Message), null);

        var now = Clock();
        var worker = new HiredWorker
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            Name = candidate.Name,
            Trade = candidate.Trade,
            Tier = candidate.Tier,
            Traits = candidate.Traits,
            SecondTrade = candidate.SecondTrade,
            HomeBiome = candidate.HomeBiome,
            Look = candidate.Look,
            HiredAt = now,
            LastSettledAt = now
        };
        _context.HiringCandidates.Remove(candidate);
        _context.HiredWorkers.Add(worker);
        await _context.SaveChangesAsync();

        _sessionLog.Log("HIRING-HIRE", $"user={userId} realm={gameInstanceId} {worker.Name} {worker.Trade}/{worker.Tier} traits={worker.Traits} cost={cost}");
        return (new HiringOutcome(HiringError.None), worker);
    }

    // -----------------------------------------------------------------
    // The workforce
    // -----------------------------------------------------------------

    public async Task<List<HiredWorker>> WorkersAsync(Guid gameInstanceId, string userId)
        => await _context.HiredWorkers.Where(w => w.GameInstanceId == gameInstanceId && w.UserId == userId)
            .OrderBy(w => w.HiredAt).ToListAsync();

    /// <summary>Beds (two a region held) and how many are taken.</summary>
    public async Task<(int Beds, int Used)> BedsAsync(Guid gameInstanceId, string userId)
    {
        int held = RosterCapRules.RegionsHeldBy(userId, await RegionsAsync(gameInstanceId));
        int used = await _context.HiredWorkers.CountAsync(w => w.GameInstanceId == gameInstanceId && w.UserId == userId);
        return (HiringRules.BedsFor(held), used);
    }

    /// <summary>Every resource site in the regions this player holds, with how many of their workers are there.</summary>
    public async Task<List<HiringSite>> SitesAsync(Guid gameInstanceId, string userId)
    {
        var regions = (await RegionsAsync(gameInstanceId)).Where(r => r.IsOwnedByPlayer(userId)).ToList();
        var used = await _context.HiredWorkers
            .Where(w => w.GameInstanceId == gameInstanceId && w.UserId == userId && w.SiteId != null)
            .GroupBy(w => w.SiteId!).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);

        var sites = new List<HiringSite>();
        foreach (var region in regions)
            foreach (var site in RegionGenerator.Generate(region).Sites.Where(s => s.Type == LocationType.ResourceNode))
                sites.Add(new HiringSite(site.SiteId, region.RegionId, ResourceNodeRules.TradeOf(site.SiteId, region.Biome),
                    region.Tier, region.Biome, ResourceNodeRules.SlotsFor(region.Tier), used.GetValueOrDefault(site.SiteId)));
        return sites;
    }

    /// <summary>
    /// Sends a worker to a site, or home to the Hall when <paramref name="siteId"/> is null. What they
    /// had gathered where they were is paid first, and both sites' rates are worked out again (a
    /// Foreman arriving or leaving changes everyone's).
    /// </summary>
    public async Task<HiringOutcome> AssignAsync(Guid gameInstanceId, string userId, Guid workerId, string? siteId)
    {
        var worker = await _context.HiredWorkers.FirstOrDefaultAsync(w =>
            w.Id == workerId && w.GameInstanceId == gameInstanceId && w.UserId == userId);
        if (worker == null) return new HiringOutcome(HiringError.NoSuchWorker, "You employ nobody by that name.");

        var regions = await RegionsAsync(gameInstanceId);
        HiringSite? target = null;
        if (siteId != null)
        {
            target = (await SitesAsync(gameInstanceId, userId)).FirstOrDefault(s => s.SiteId == siteId);
            if (target == null)
            {
                var region = regions.FirstOrDefault(r => r.RegionId == SiteSpec.RegionIdOf(siteId));
                return region != null && !region.IsOwnedByPlayer(userId)
                    ? new HiringOutcome(HiringError.NotYourLand, "Workers can only be sent to land you hold.")
                    : new HiringOutcome(HiringError.NoSuchSite, "There is no resource site there.");
            }
            if (!HiringRules.CanWork(worker.Trade, worker.SecondTrade, target.Trade))
                return new HiringOutcome(HiringError.WrongTrade,
                    $"{worker.Name} cannot work a {ResourceNodeRules.PlaceOf(target.Trade).ToLowerInvariant()}.");
            if (worker.SiteId != siteId && target.Used >= target.Slots)
                return new HiringOutcome(HiringError.SiteFull, "Every place at that site is taken.");
        }

        var now = Clock();
        await SettleAllAsync(gameInstanceId, regions, now, userId);

        string? from = worker.SiteId;
        worker.SiteId = siteId;
        worker.AssignedAt = siteId == null ? null : now;
        worker.LastSettledAt = now;
        await _context.SaveChangesAsync();

        await RerateAsync(gameInstanceId, regions, new[] { from, siteId });
        _sessionLog.Log("HIRING-ASSIGN", $"user={userId} worker={worker.Id} {from ?? "hall"}->{siteId ?? "hall"}");
        return new HiringOutcome(HiringError.None);
    }

    // -----------------------------------------------------------------
    // Settling
    // -----------------------------------------------------------------

    /// <summary>
    /// Pays every assigned worker in the realm (or one player's, with <paramref name="onlyUserId"/>)
    /// what they have gathered up to <paramref name="until"/>, then sends home anyone whose site is in
    /// a region their employer no longer holds. Never claws anything back.
    /// </summary>
    public async Task SettleAllAsync(Guid gameInstanceId, IReadOnlyList<WorldRegionData> regions, DateTime until, string? onlyUserId = null)
    {
        var query = _context.HiredWorkers.Where(w => w.GameInstanceId == gameInstanceId && w.SiteId != null);
        if (onlyUserId != null) query = query.Where(w => w.UserId == onlyUserId);
        var workers = await query.ToListAsync();
        if (workers.Count == 0) return;

        var byId = regions.ToDictionary(r => r.RegionId ?? "", r => r);
        var touched = new HashSet<string?>();
        var pay = new Dictionary<string, Dictionary<string, int>>();
        var guarded = await GuardedAsync(gameInstanceId, onlyUserId);
        string realm = gameInstanceId.ToString();
        int season = await SeasonAsync(gameInstanceId);

        // Mentors lift the experience of the others at their site; computed before anyone changes.
        var mentorsAt = workers.Where(w => w.PerkList.Contains(WorkerPerk.Mentor))
            .GroupBy(w => w.SiteId!).ToDictionary(g => g.Key, g => g.Select(w => w.Id).ToHashSet());

        foreach (var worker in workers)
        {
            if (until > worker.LastSettledAt)
            {
                // Raiders harry the diggings of a region nobody patrols, now and then (auto-fight.md §7).
                string regionId = SiteSpec.RegionIdOf(worker.SiteId!);
                bool patrolled = guarded.Contains((worker.UserId, regionId));
                byId.TryGetValue(regionId, out var siteRegion);
                var sheet = worker.Sheet();
                double gathered = worker.Carry + WorkerLevelRules.Gathered(worker.RatePerHour, worker.LastSettledAt, until,
                    sheet, worker.Id.ToString(),
                    patrolled ? null : h => HarassmentRules.IsHarried(realm, regionId, h), worker.AssignedAt);
                int whole = (int)Math.Floor(gathered);
                worker.Carry = gathered - whole;
                worker.LifetimeOutput += whole;

                if (!pay.TryGetValue(worker.UserId, out var goods)) pay[worker.UserId] = goods = new();
                var trade = SiteTrade(worker.SiteId!, byId);
                if (whole > 0 && trade.HasValue)
                {
                    string good = ResourceNodeRules.GoodOf(trade.Value);
                    goods[good] = goods.GetValueOrDefault(good) + whole;
                }
                // Keen Eye and Lucky Strike turn things up now and then.
                if (siteRegion != null)
                    foreach (var (find, count) in WorkerLevelRules.Finds(sheet, worker.Id.ToString(), worker.LastSettledAt, until, siteRegion.Biome))
                        goods[find] = goods.GetValueOrDefault(find) + count;

                // Experience is time at work. A level gained re-rates the site; a roll level rolls.
                bool mentorBeside = mentorsAt.TryGetValue(worker.SiteId!, out var mentors) && mentors.Any(id => id != worker.Id);
                int before = worker.Level;
                worker.HoursWorked += (until - worker.LastSettledAt).TotalHours * WorkerLevelRules.ExperienceFactor(sheet, mentorBeside);
                worker.LastSettledAt = until;
                if (RollUp(worker, season)) touched.Add(worker.SiteId);
                if (worker.Level != before) touched.Add(worker.SiteId);
            }

            // Land lost: home to the Hall, with what they had gathered already paid above. The fraction
            // they carry is kept for their next site.
            byId.TryGetValue(SiteSpec.RegionIdOf(worker.SiteId!), out var region);
            if (region == null || !region.IsOwnedByPlayer(worker.UserId))
            {
                touched.Add(worker.SiteId);
                worker.SiteId = null;
                worker.AssignedAt = null;
                worker.RatePerHour = 0;
                _sessionLog.Log("HIRING-HOME", $"user={worker.UserId} worker={worker.Id} region lost");
            }
        }

        foreach (var goods in pay.Values)
            foreach (var key in goods.Where(g => g.Value <= 0).Select(g => g.Key).ToList())
                goods.Remove(key);

        await _context.SaveChangesAsync();

        foreach (var (userId, goods) in pay.Where(p => p.Value.Count > 0))
            await _wallet.GrantAsync(gameInstanceId, userId,
                goods.Select(g => new MaterialGrant { MaterialName = g.Key, Quantity = g.Value }).ToList(), "hiring-output");

        if (touched.Count > 0) await RerateAsync(gameInstanceId, regions, touched);
    }

    /// <summary>
    /// The regions each player keeps a patrol in (<see cref="AutoFightRules.Guards"/>), as they stand
    /// now. A settle covering a long stretch takes the patrol as it is at the end of it: every read of
    /// the player's companies or wallet settles both, so the stretch is short while they play.
    /// </summary>
    private async Task<HashSet<(string UserId, string RegionId)>> GuardedAsync(Guid gameInstanceId, string? onlyUserId)
    {
        var query = _context.PlayerParties.AsNoTracking()
            .Where(p => p.GameInstanceId == gameInstanceId && p.AutoMode && p.AutoOrder == AutoOrder.Patrol && p.AutoRegionId != null);
        if (onlyUserId != null) query = query.Where(p => p.UserId == onlyUserId);
        var patrols = await query.Select(p => new { p.UserId, p.AutoRegionId, p.AutoOrder, p.AutoStatus }).ToListAsync();
        return patrols.Where(p => AutoFightRules.Guards(p.AutoOrder, p.AutoStatus))
            .Select(p => (p.UserId, p.AutoRegionId!)).ToHashSet();
    }

    /// <summary>Settles one player's workers against the realm's current world. Before their goods are read or spent.</summary>
    public async Task SettlePlayerAsync(Guid gameInstanceId, string userId)
        => await SettleAllAsync(gameInstanceId, await RegionsAsync(gameInstanceId), Clock(), userId);

    // -----------------------------------------------------------------
    // Veterancy (Workers spec)
    // -----------------------------------------------------------------

    /// <summary>Debug: the next promotion roll per player is forced to succeed (true) or fail (false).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ForcedPromotions = new();

    /// <summary>
    /// Makes the rolls for every roll level a worker has reached and not yet rolled, in order, and
    /// applies them: a perk, a promotion's tier and trait, a second trade. True if anything changed
    /// their rate.
    /// </summary>
    private bool RollUp(HiredWorker worker, int season)
    {
        int level = worker.Level;
        if (level <= worker.LevelRolledTo) return false;

        var rolls = worker.RollList;
        bool changed = false;
        for (int l = worker.LevelRolledTo + 1; l <= level; l++)
        {
            if (!WorkerLevelRules.RollsAt(l)) continue;
            bool? force = null;
            if (WorkerLevelRules.IsPromotionLevel(l) && ForcedPromotions.TryRemove(worker.UserId, out var forced)) force = forced;

            var sheet = worker.Sheet();
            var roll = WorkerLevelRules.Roll(worker.Id.ToString(), l, season, sheet, force);
            if (roll == null) continue;
            WorkerLevelRules.Apply(sheet, roll);
            worker.Tier = sheet.Tier;
            worker.Traits = HiringTraits.Write(sheet.Traits);
            worker.SecondTrade = sheet.SecondTrade;
            rolls.Add(roll);
            changed = true;
            _sessionLog.Log("HIRING-ROLL", $"user={worker.UserId} worker={worker.Id} level={l} {roll.Kind} " +
                $"perk={roll.Perk} tier={roll.NewTier} trait={roll.NewTrait}");
        }
        worker.Rolls = WorkerRollLog.Write(rolls);
        worker.LevelRolledTo = level;
        return changed;
    }

    /// <summary>★ KEEP: marks a worker to go with the player into the next season. At most two.</summary>
    public async Task<HiringOutcome> KeepAsync(Guid gameInstanceId, string userId, Guid workerId, bool keep)
    {
        var workers = await WorkersAsync(gameInstanceId, userId);
        var worker = workers.FirstOrDefault(w => w.Id == workerId);
        if (worker == null) return new HiringOutcome(HiringError.NoSuchWorker, "You employ nobody by that name.");
        if (keep && !worker.Keep && workers.Count(w => w.Keep) >= WorkerLevelRules.VeteransKept)
            return new HiringOutcome(HiringError.KeepFull,
                $"You can keep only {WorkerLevelRules.VeteransKept} workers into the next season. Unmark one first.");
        worker.Keep = keep;
        await _context.SaveChangesAsync();
        return new HiringOutcome(HiringError.None);
    }

    /// <summary>DISMISS: a worker leaves for good (no refund), freeing their bed. What they gathered is paid first.</summary>
    public async Task<HiringOutcome> DismissAsync(Guid gameInstanceId, string userId, Guid workerId)
    {
        var worker = await _context.HiredWorkers.FirstOrDefaultAsync(w =>
            w.Id == workerId && w.GameInstanceId == gameInstanceId && w.UserId == userId);
        if (worker == null) return new HiringOutcome(HiringError.NoSuchWorker, "You employ nobody by that name.");

        var regions = await RegionsAsync(gameInstanceId);
        await SettleAllAsync(gameInstanceId, regions, Clock(), userId);
        string? site = worker.SiteId;
        _context.HiredWorkers.Remove(worker);
        await _context.SaveChangesAsync();
        await RerateAsync(gameInstanceId, regions, new[] { site });
        _sessionLog.Log("HIRING-DISMISS", $"user={userId} worker={worker.Id} {worker.Name} level={worker.Level}");
        return new HiringOutcome(HiringError.None);
    }

    /// <summary>The reveals have played: every roll so far is seen.</summary>
    public async Task MarkRollsSeenAsync(Guid gameInstanceId, string userId)
    {
        foreach (var worker in await WorkersAsync(gameInstanceId, userId))
            worker.RollsSeen = worker.RollList.Count;
        await _context.SaveChangesAsync();
    }

    /// <summary>Debug: adds hours of work to every worker of this player (assigned or not), rolling any levels crossed.</summary>
    public async Task DebugAddHoursAsync(Guid gameInstanceId, string userId, double hours)
    {
        var regions = await RegionsAsync(gameInstanceId);
        await SettleAllAsync(gameInstanceId, regions, Clock(), userId);
        int season = await SeasonAsync(gameInstanceId);
        var sites = new HashSet<string?>();
        foreach (var worker in await WorkersAsync(gameInstanceId, userId))
        {
            worker.HoursWorked += hours;
            RollUp(worker, season);
            sites.Add(worker.SiteId);
        }
        await _context.SaveChangesAsync();
        await RerateAsync(gameInstanceId, regions, sites);
    }

    /// <summary>Debug: sets a worker's level (its hours to that level's start), rolling any levels crossed.</summary>
    public async Task<HiringOutcome> DebugSetLevelAsync(Guid gameInstanceId, string userId, Guid workerId, int level)
    {
        var worker = await _context.HiredWorkers.FirstOrDefaultAsync(w =>
            w.Id == workerId && w.GameInstanceId == gameInstanceId && w.UserId == userId);
        if (worker == null) return new HiringOutcome(HiringError.NoSuchWorker, "You employ nobody by that name.");
        worker.HoursWorked = WorkerLevelRules.HoursAt(level);
        RollUp(worker, await SeasonAsync(gameInstanceId));
        await _context.SaveChangesAsync();
        await RerateAsync(gameInstanceId, await RegionsAsync(gameInstanceId), new[] { worker.SiteId });
        return new HiringOutcome(HiringError.None);
    }

    /// <summary>Debug: this player's next promotion roll succeeds (true), fails (false), or is left to chance (null).</summary>
    public static void DebugForcePromotion(string userId, bool? succeed)
    {
        if (succeed.HasValue) ForcedPromotions[userId] = succeed.Value;
        else ForcedPromotions.TryRemove(userId, out _);
    }

    /// <summary>Debug: every roll is unseen again, so the reveals replay.</summary>
    public async Task DebugReplayRevealsAsync(Guid gameInstanceId, string userId)
    {
        foreach (var worker in await WorkersAsync(gameInstanceId, userId)) worker.RollsSeen = 0;
        await _context.SaveChangesAsync();
    }

    /// <summary>Who would go with this player into the next season, and at what level.</summary>
    public async Task<List<(HiredWorker Worker, int Level)>> CarryOverPreviewAsync(Guid gameInstanceId, string userId)
    {
        var workers = await WorkersAsync(gameInstanceId, userId);
        var ids = WorkerLevelRules.Veterans(workers.Select(Candidate));
        return ids.Select(id => workers.First(w => w.Id.ToString() == id))
            .Select(w => (w, WorkerLevelRules.CarriedLevel(w.Level))).ToList();
    }

    private static WorkerLevelRules.Candidate Candidate(HiredWorker w) => new() { Id = w.Id.ToString(), Keep = w.Keep, Hours = w.HoursWorked };

    /// <summary>
    /// Readies a veteran for the new season (spec §3.4): half their level, hours reset to its start;
    /// tier and traits kept, promotions included; perks rolled above the new level dropped, to be
    /// rolled afresh when reached again; at the Hall, nothing carried.
    /// </summary>
    public static void CarryOver(HiredWorker worker, DateTime now)
    {
        int level = WorkerLevelRules.CarriedLevel(worker.Level);
        var kept = worker.RollList.Where(r => r.Level <= level).ToList();
        worker.HoursWorked = WorkerLevelRules.HoursAt(level);
        worker.Rolls = WorkerRollLog.Write(kept);
        worker.RollsSeen = kept.Count;
        worker.LevelRolledTo = level;
        // A second trade from a Jack of Trades who no longer has the perk goes with it.
        if (!worker.TraitList.Contains(WorkerTrait.Versatile) && !kept.Any(r => r.Perk == WorkerPerk.JackOfTrades))
            worker.SecondTrade = null;
        worker.SiteId = null;
        worker.AssignedAt = null;
        worker.RatePerHour = 0;
        worker.Carry = 0;
        worker.LastSettledAt = now;
        worker.SeasonsServed++;
    }

    /// <summary>
    /// A season reset: the board and the goods go, and every worker but each player's two veterans
    /// (★ KEEP first, then the highest levels), who come back at half their level (Workers spec §3.4).
    /// Goods gathered for the old map still fortify nothing on the new.
    /// </summary>
    public static async Task ResetRealmAsync(ApplicationDbContext context, Guid realmId)
    {
        var now = DateTime.UtcNow;
        var workers = await context.HiredWorkers.Where(w => w.GameInstanceId == realmId).ToListAsync();
        foreach (var employer in workers.GroupBy(w => w.UserId))
        {
            var veterans = WorkerLevelRules.Veterans(employer.Select(Candidate)).ToHashSet();
            foreach (var worker in employer)
            {
                if (veterans.Contains(worker.Id.ToString())) CarryOver(worker, now);
                else context.HiredWorkers.Remove(worker);
            }
        }
        context.HiringCandidates.RemoveRange(context.HiringCandidates.Where(c => c.GameInstanceId == realmId));
        context.HiringStates.RemoveRange(context.HiringStates.Where(s => s.GameInstanceId == realmId));
        var goods = ResourceNodeRules.Trades.Select(ResourceNodeRules.GoodOf).ToList();
        context.PlayerMaterials.RemoveRange(context.PlayerMaterials.Where(m => m.GameInstanceId == realmId && goods.Contains(m.MaterialName)));
        await Task.CompletedTask;
    }

    // -----------------------------------------------------------------

    /// <summary>Works out every rate at the given sites again, from who is there now.</summary>
    private async Task RerateAsync(Guid gameInstanceId, IReadOnlyList<WorldRegionData> regions, IEnumerable<string?> siteIds)
    {
        var byId = regions.ToDictionary(r => r.RegionId ?? "", r => r);
        foreach (var siteId in siteIds.Where(s => s != null).Distinct())
        {
            var crew = await _context.HiredWorkers.Where(w => w.GameInstanceId == gameInstanceId && w.SiteId == siteId).ToListAsync();
            byId.TryGetValue(SiteSpec.RegionIdOf(siteId!), out var region);
            var trade = SiteTrade(siteId!, byId);
            var sheets = crew.ToDictionary(w => w.Id, w => w.Sheet());
            foreach (var worker in crew)
            {
                // A Foreman (or an Overseer) lifts everyone else at the site.
                double bonus = crew.Where(o => o.Id != worker.Id).Sum(o => WorkerLevelRules.ForemanBonusOf(sheets[o.Id]));
                worker.RatePerHour = region == null || !trade.HasValue ? 0
                    : WorkerLevelRules.RateAt(sheets[worker.Id], trade.Value, region.Biome, region.Tier, bonus);
            }
        }
        await _context.SaveChangesAsync();
    }

    private static ResourceTrade? SiteTrade(string siteId, IReadOnlyDictionary<string, WorldRegionData> regions)
        => regions.TryGetValue(SiteSpec.RegionIdOf(siteId), out var region)
            ? ResourceNodeRules.TradeOf(siteId, region.Biome)
            : null;

    private async Task FillAsync(Guid gameInstanceId, string userId, List<HiringCandidate> board, int count,
        IReadOnlyList<WorldRegionData> regions, DateTime now)
    {
        // Locals come from the land the player holds, so where they rule shapes who walks in.
        var held = regions.Where(r => r.IsOwnedByPlayer(userId)).ToList();
        for (int i = 0; i < count; i++)
        {
            int slot = Enumerable.Range(0, HiringRules.BoardSize).FirstOrDefault(s => board.All(c => c.Slot != s), -1);
            if (slot < 0) return;

            var biome = held.Count > 0 ? held[Dice.Next(held.Count)].Biome : BiomeType.Grassland;
            var roll = HiringRules.Roll(Dice, biome);
            var candidate = new HiringCandidate
            {
                GameInstanceId = gameInstanceId,
                UserId = userId,
                Slot = slot,
                Name = roll.Name,
                Trade = roll.Trade,
                Tier = roll.Tier,
                Traits = HiringTraits.Write(roll.Traits),
                SecondTrade = roll.SecondTrade,
                HomeBiome = roll.HomeBiome,
                Look = roll.Look,
                RolledAt = now
            };
            _context.HiringCandidates.Add(candidate);
            board.Add(candidate);
        }
    }

    private async Task<List<HiringCandidate>> LoadBoardAsync(Guid gameInstanceId, string userId)
        => await _context.HiringCandidates.Where(c => c.GameInstanceId == gameInstanceId && c.UserId == userId).ToListAsync();

    private async Task<HiringState> StateAsync(Guid gameInstanceId, string userId)
    {
        var state = await _context.HiringStates.FirstOrDefaultAsync(s => s.GameInstanceId == gameInstanceId && s.UserId == userId);
        if (state != null) return state;
        state = new HiringState { GameInstanceId = gameInstanceId, UserId = userId, LastArrivalAt = Clock(), UpdatedAt = default };
        _context.HiringStates.Add(state);
        return state;
    }

    /// <summary>The realm's season number, which seeds the workers' rolls (a veteran re-rolls afresh).</summary>
    private async Task<int> SeasonAsync(Guid gameInstanceId)
        => await _context.GameInstances.AsNoTracking().Where(g => g.Id == gameInstanceId)
            .Select(g => g.SeasonNumber).FirstOrDefaultAsync();

    private async Task<IReadOnlyList<WorldRegionData>> RegionsAsync(Guid gameInstanceId)
    {
        var row = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        return row == null ? Array.Empty<WorldRegionData>() : WorldRegionBlob.ReadAllRegions(JsonNode.Parse(row.GameData));
    }
}
