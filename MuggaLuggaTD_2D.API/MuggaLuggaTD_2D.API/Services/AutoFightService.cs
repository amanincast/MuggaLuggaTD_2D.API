using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Services;

public enum AutoError
{
    None = 0,
    ContractMismatch,
    WorldNotFound,
    PartyNotFound,
    Busy,
    Empty,
    NotInAutoMode,
    RegionNotHeld,
    TooStrong,
}

public record AutoOutcome(AutoError Error, string? Message = null)
{
    public bool Succeeded => Error == AutoError.None;
}

/// <summary>
/// Companies in auto mode (<c>docs/design/auto-fight.md</c>, Unity repo, phase 2; Mike, 2026-10-04).
///
/// <para><b>Nothing ticks.</b> A company's day is replayed from where it was last settled
/// (<see cref="PlayerParty.AutoSettledAt"/>) to now whenever its player's companies are read: it
/// walks to its next site, fights, is paid or Bloodied, picks the next, and so on, until it is
/// caught up, out of provisions, or has nothing it may fight. Each fight is rolled from the
/// company's fight count (<see cref="AutoFightRules.RollWin"/>), so a settle that is run again rolls
/// nothing twice. At most <see cref="AutoFightRules.MaximumReplay"/> is replayed.</para>
///
/// <para><b>What it pays.</b> Gold and materials go straight into the server's purse and wallet. The
/// experience and gear belong to the save, which only the client writes, so they are banked in an
/// <see cref="AutoFightReport"/> for the client to collect, and the gear is recorded in the item
/// ledger now so the save that carries it is believed.</para>
///
/// <para><b>What it costs.</b> Provisions from the wallet (<see cref="ProvisionRules"/>), paid as each
/// fight or patrol stint begins. A loss Bloodies its fighters (<see cref="BloodiedRules"/>): the
/// company rests where it stands, then takes up its order again by itself.</para>
/// </summary>
public class AutoFightService
{
    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly MaterialWalletService _wallet;
    private readonly GoldService _gold;
    private readonly ItemLedgerService _items;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<AutoFightService> _logger;

    /// <summary>Brings this player's workers' goods in before provisions are counted; optional so tests can leave it out.</summary>
    private readonly HiringService? _hiring;

    /// <summary>Counts auto mode's wins toward the player's quests, at its share (quests.md §4).</summary>
    private readonly QuestService? _quests;
    private readonly LetterService? _letters;

    /// <summary>Where spoils are rolled from. Tests fix it.</summary>
    public Random Dice { get; set; } = Random.Shared;

    /// <summary>How a fight is rolled (<see cref="AutoFightRules.RollWin"/>). Tests fix the outcome.</summary>
    public Func<double, string, long, bool> Roll { get; set; } = AutoFightRules.RollWin;

    /// <summary>Rolls a road: where along it an ambush strikes, or null. Tests fix it.</summary>
    public Func<double, string, DateTime, double?> RollRoad { get; set; } = AutoFightRules.RollAmbush;

    /// <summary>Now. Tests move it.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>How many sites a company remembers having fought (it avoids the last three).</summary>
    private const int RecentKept = 6;

    public AutoFightService(ApplicationDbContext context, IGameContentProvider content, MaterialWalletService wallet,
        GoldService gold, ItemLedgerService items, ISessionLog sessionLog, ILogger<AutoFightService> logger,
        HiringService? hiring = null, QuestService? quests = null, LetterService? letters = null)
    {
        _letters = letters;
        _quests = quests;
        _context = context;
        _content = content;
        _wallet = wallet;
        _gold = gold;
        _items = items;
        _sessionLog = sessionLog;
        _logger = logger;
        _hiring = hiring;
    }

    // -----------------------------------------------------------------
    // Orders
    // -----------------------------------------------------------------

    /// <summary>
    /// Puts a company into auto mode, or takes it out. Into: it must be at rest and have somebody in
    /// it, and it waits for an order. Out of: a walk becomes an ordinary journey that lands where it
    /// was going; a fight in progress is dropped at no cost (its provisions are already eaten).
    /// </summary>
    public async Task<AutoOutcome> SetModeAsync(Guid gameInstanceId, string userId, Guid partyId, AutoModeRequest request)
    {
        if (request.SharedContractVersion != SharedContract.Version) return Mismatch;

        await SettleAsync(gameInstanceId, userId);
        var party = await _context.PlayerParties.FirstOrDefaultAsync(p =>
            p.Id == partyId && p.GameInstanceId == gameInstanceId && p.UserId == userId);
        if (party == null) return new AutoOutcome(AutoError.PartyNotFound, "No such company.");

        var now = Clock();
        if (request.On)
        {
            if (party.AutoMode) return new AutoOutcome(AutoError.None);
            if (party.State != CompanyState.Idle)
                return new AutoOutcome(AutoError.Busy, $"{party.Name} is away. It can be sent off on its own once it is at rest.");
            if (MarchingArmy.ReadIds(party.CharacterIdsJson).Count == 0)
                return new AutoOutcome(AutoError.Empty, $"{party.Name} has nobody in it to send.");

            party.AutoMode = true;
            party.AutoOrder = AutoOrder.None;
            party.AutoRegionId = null;
            party.AutoStatus = AutoStatus.Ready;
            party.AutoStepEndsAt = null;
            party.AutoTargetSiteId = null;
            party.AutoSettledAt = now;
        }
        else
        {
            if (!party.AutoMode) return new AutoOutcome(AutoError.None);
            if (party.AutoStatus == AutoStatus.Fighting)
            {
                party.State = CompanyState.Idle;
                party.SiteId = party.AutoTargetSiteId ?? party.SiteId;
            }
            // A walk is left as it is: an ordinary journey now, which lands by the clock like any other.
            // An ambush already rolled for it stays, and now halts it to be asked, as any steered company is.
            party.AutoMode = false;
            party.AutoOrder = AutoOrder.None;
            party.AutoRegionId = null;
            party.AutoStatus = AutoStatus.Ready;
            party.AutoStepEndsAt = null;
            party.AutoTargetSiteId = null;
            party.AutoSettledAt = null;
        }

        party.UpdatedAt = now;
        await _context.SaveChangesAsync();
        _sessionLog.Log("AUTO-MODE", $"user={userId} party={party.Id} on={request.On}");
        return new AutoOutcome(AutoError.None);
    }

    /// <summary>
    /// Gives a company in auto mode its order: roam a region's sites, or patrol it. Only a region its
    /// player holds (Mike). A patrol must be able to beat the region's mobs, so the region must be below
    /// the company's level. A step in progress finishes first; a stopped company starts again.
    /// </summary>
    public async Task<AutoOutcome> OrderAsync(Guid gameInstanceId, string userId, Guid partyId, AutoOrderRequest request)
    {
        if (request.SharedContractVersion != SharedContract.Version) return Mismatch;

        await SettleAsync(gameInstanceId, userId);
        var party = await _context.PlayerParties.FirstOrDefaultAsync(p =>
            p.Id == partyId && p.GameInstanceId == gameInstanceId && p.UserId == userId);
        if (party == null) return new AutoOutcome(AutoError.PartyNotFound, "No such company.");
        if (!party.AutoMode) return new AutoOutcome(AutoError.NotInAutoMode, $"{party.Name} is not in auto mode.");
        if (request.Order == AutoOrder.None) return new AutoOutcome(AutoError.NotInAutoMode, "Give it an order: roam or patrol.");

        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null) return new AutoOutcome(AutoError.WorldNotFound, "This realm has no world yet.");

        var region = WorldRegionBlob.ReadAllRegions(world).FirstOrDefault(r => r.RegionId == request.RegionId);
        if (region == null || !region.IsOwnedByPlayer(userId))
            return new AutoOutcome(AutoError.RegionNotHeld, "A company in auto mode only goes where you hold the land.");

        if (request.Order == AutoOrder.Patrol)
        {
            var save = await MarchingArmy.LoadPlayerSaveAsync(_context, _logger, gameInstanceId, userId);
            int level = AutoFightRules.CompanyLevel(LevelsOf(save, MarchingArmy.ReadIds(party.CharacterIdsJson)));
            int mobs = SkirmishLevel(RegionGenerator.Generate(region));
            if (!AutoFightRules.CanFight(level, mobs))
                return new AutoOutcome(AutoError.TooStrong,
                    $"The mobs of {Naming.RegionName(region)} are level {mobs}; {party.Name} (level {level}) can only patrol land below its level.");
        }

        party.AutoOrder = request.Order;
        party.AutoRegionId = region.RegionId;
        if (!IsStep(party.AutoStatus))
        {
            party.AutoStatus = AutoStatus.Ready;
            party.AutoSettledAt = Clock();
        }
        party.UpdatedAt = Clock();
        await _context.SaveChangesAsync();
        _sessionLog.Log("AUTO-ORDER", $"user={userId} party={party.Id} order={request.Order} region={region.RegionId}");

        await SettleAsync(gameInstanceId, userId);
        return new AutoOutcome(AutoError.None);
    }

    /// <summary>The reports the client has not collected yet: the "While you were away" card.</summary>
    public async Task<AutoReportsResponse> ReportsAsync(Guid gameInstanceId, string userId)
    {
        await SettleAsync(gameInstanceId, userId);
        var rows = await _context.AutoFightReports.AsNoTracking()
            .Where(r => r.GameInstanceId == gameInstanceId && r.UserId == userId && r.CollectedAt == null)
            .OrderBy(r => r.At)
            .ToListAsync();
        return new AutoReportsResponse(rows.Select(ToDto).ToList(), Clock());
    }

    /// <summary>
    /// Marks reports collected: the client has put their experience and gear into its save. Only this
    /// player's, and a report collected twice is no error.
    /// </summary>
    public async Task<int> CollectAsync(Guid gameInstanceId, string userId, AutoCollectRequest request)
    {
        var ids = (request.ReportIds ?? new List<Guid>()).ToHashSet();
        if (ids.Count == 0) return 0;

        var rows = await _context.AutoFightReports
            .Where(r => r.GameInstanceId == gameInstanceId && r.UserId == userId && r.CollectedAt == null && ids.Contains(r.Id))
            .ToListAsync();
        var now = Clock();
        foreach (var row in rows) row.CollectedAt = now;
        await _context.SaveChangesAsync();
        _sessionLog.Log("AUTO-COLLECT", $"user={userId} reports={rows.Count}");
        return rows.Count;
    }

    // -----------------------------------------------------------------
    // Bloodied
    // -----------------------------------------------------------------

    /// <summary>This player's characters still Bloodied at <paramref name="utcNow"/>, and when each recovers.</summary>
    public static async Task<Dictionary<string, DateTime>> BloodiedAsync(ApplicationDbContext context,
        Guid gameInstanceId, string userId, DateTime utcNow)
    {
        var rows = await context.BloodiedCharacters.AsNoTracking()
            .Where(b => b.GameInstanceId == gameInstanceId && b.UserId == userId && b.RecoversAt > utcNow)
            .ToListAsync();
        return rows.ToDictionary(b => b.CharacterId, b => DateTime.SpecifyKind(b.RecoversAt, DateTimeKind.Utc), StringComparer.Ordinal);
    }

    /// <summary>
    /// Bloodies <paramref name="characterIds"/> from <paramref name="at"/> (BloodiedRules): a lost
    /// ambush, an abandoned run, a lost raid, a repelled siege. A later recovery already on the books
    /// stands. The caller saves.
    /// </summary>
    public static async Task BloodyAsync(ApplicationDbContext context, Guid gameInstanceId, string userId,
        IEnumerable<string> characterIds, DateTime at)
    {
        var ids = characterIds.Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return;
        var recovers = BloodiedRules.RecoversAt(at);
        var rows = await context.BloodiedCharacters
            .Where(b => b.GameInstanceId == gameInstanceId && b.UserId == userId && ids.Contains(b.CharacterId))
            .ToDictionaryAsync(b => b.CharacterId, StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!rows.TryGetValue(id, out var row))
                context.BloodiedCharacters.Add(row = new BloodiedCharacter { GameInstanceId = gameInstanceId, UserId = userId, CharacterId = id });
            else if (row.RecoversAt >= recovers)
                continue;
            row.RecoversAt = recovers;
        }
    }

    /// <summary>A season reset: every company comes out of auto mode and every wound heals. Uncollected reports stay; their gear is the player's.</summary>
    public static async Task ResetRealmAsync(ApplicationDbContext context, Guid realmId)
    {
        context.BloodiedCharacters.RemoveRange(context.BloodiedCharacters.Where(b => b.GameInstanceId == realmId));
        var auto = await context.PlayerParties.Where(p => p.GameInstanceId == realmId && p.AutoMode).ToListAsync();
        foreach (var party in auto)
        {
            party.AutoMode = false;
            party.AutoOrder = AutoOrder.None;
            party.AutoRegionId = null;
            party.AutoStatus = AutoStatus.Ready;
            party.AutoStepEndsAt = null;
            party.AutoTargetSiteId = null;
            party.AutoSettledAt = null;
            party.AutoRecentJson = "[]";
        }
    }

    // -----------------------------------------------------------------
    // The replay
    // -----------------------------------------------------------------

    /// <summary>Everything one settle needs, read once for all of a player's auto companies.</summary>
    private sealed class Day
    {
        public required Guid Instance { get; init; }
        public required string UserId { get; init; }
        public required DateTime Now { get; init; }
        public required Dictionary<string, WorldRegionData> Regions { get; init; }
        public required Dictionary<HexCoord, WorldRegionData> ByHex { get; init; }
        public required Dictionary<string, RegionLayout> Layouts { get; init; }
        public required Dictionary<string, int> Levels { get; init; }
        public required HashSet<string> Committed { get; init; }

        /// <summary>The regions this player has a company ordered to patrol: their roads are safer (§6).</summary>
        public required HashSet<string> Patrolled { get; init; }
        /// <summary>When each wounded character recovers; read untracked, written after the companies are.</summary>
        public required Dictionary<string, DateTime> Bloodied { get; init; }

        /// <summary>The wallet's goods as the replay spends them; read untracked, written after the companies are.</summary>
        public required Dictionary<string, int> Goods { get; init; }

        public readonly List<AutoFightReport> Reports = new();
        public readonly HashSet<string> Wounded = new(StringComparer.Ordinal);

        public long Gold;
        public readonly List<ItemSaveData> Items = new();
        public readonly Dictionary<string, int> Materials = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Eaten = new(StringComparer.Ordinal);
        public int Fights;

        public RegionLayout LayoutOf(WorldRegionData region)
        {
            if (!Layouts.TryGetValue(region.RegionId, out var layout))
                Layouts[region.RegionId] = layout = RegionGenerator.Generate(region);
            return layout;
        }
    }

    /// <summary>
    /// Replays every one of this player's companies in auto mode up to now, pays what they won, and
    /// saves. Cheap when there are none, so every read of the companies can call it.
    /// </summary>
    public async Task SettleAsync(Guid gameInstanceId, string userId)
    {
        if (!await _context.PlayerParties.AnyAsync(p => p.GameInstanceId == gameInstanceId && p.UserId == userId && p.AutoMode))
            return;

        // One settle at a time per player. The Hall reads the companies and the reports together, and
        // two replays of the same stretch would pay it twice: on Postgres the second waits here for the
        // first to commit, then finds nothing left to do.
        // Inside a caller's transaction (a trade, Hardening 3) it joins that one; the row lock still holds.
        await using var transaction = _context.Database.IsRelational() && _context.Database.CurrentTransaction == null
            ? await _context.Database.BeginTransactionAsync() : null;
        if (_context.Database.IsRelational())
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"PlayerParties\" WHERE \"GameInstanceId\" = {gameInstanceId} AND \"UserId\" = {userId} AND \"AutoMode\" FOR UPDATE");

        var parties = await _context.PlayerParties
            .Where(p => p.GameInstanceId == gameInstanceId && p.UserId == userId && p.AutoMode)
            .ToListAsync();
        if (parties.Count == 0) return;

        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null) return;

        // Goods gathered since the last read arrive first, so a larder that has been restocked feeds it.
        if (_hiring != null) await _hiring.SettlePlayerAsync(gameInstanceId, userId);

        var regions = WorldRegionBlob.ReadAllRegions(world).ToList();
        var save = await MarchingArmy.LoadPlayerSaveAsync(_context, _logger, gameInstanceId, userId);
        var committed = (await PartyService.CommitmentsAsync(_context, world, gameInstanceId, userId)).Keys
            .ToHashSet(StringComparer.Ordinal);

        var day = new Day
        {
            Instance = gameInstanceId,
            UserId = userId,
            Now = Clock(),
            Regions = regions.ToDictionary(r => r.RegionId),
            ByHex = regions.GroupBy(r => r.Hex).ToDictionary(g => g.Key, g => g.First()),
            Layouts = new Dictionary<string, RegionLayout>(StringComparer.Ordinal),
            Levels = save?.Characters?.Where(c => c?.Id != null).GroupBy(c => c.Id)
                         .ToDictionary(g => g.Key, g => (int)Math.Max(1, g.First().Level), StringComparer.Ordinal)
                     ?? new Dictionary<string, int>(StringComparer.Ordinal),
            Committed = committed,
            Patrolled = parties.Where(p => AutoFightRules.Guards(p.AutoOrder, p.AutoStatus) && p.AutoRegionId != null)
                .Select(p => p.AutoRegionId!).ToHashSet(StringComparer.Ordinal),
            Bloodied = await _context.BloodiedCharacters.AsNoTracking()
                .Where(b => b.GameInstanceId == gameInstanceId && b.UserId == userId)
                .ToDictionaryAsync(b => b.CharacterId, b => b.RecoversAt, StringComparer.Ordinal),
            Goods = await _context.PlayerMaterials.AsNoTracking()
                .Where(m => m.GameInstanceId == gameInstanceId && m.UserId == userId)
                .ToDictionaryAsync(m => m.MaterialName, m => m.Quantity, StringComparer.Ordinal),
        };

        foreach (var party in parties) Replay(party, day);

        // The companies are written first, and alone: AutoSettledAt is a concurrency token, so if
        // another read settled them meanwhile this save fails before any report, wound or spent
        // provision of this replay is written. (The in-memory provider is not transactional, so the
        // order matters there too.)
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            _context.ChangeTracker.Clear();
            _sessionLog.Log("AUTO-SETTLE-RACE", $"user={userId} another read settled first; nothing paid twice");
            return;
        }

        _context.AutoFightReports.AddRange(day.Reports);
        if (day.Wounded.Count > 0)
        {
            var rows = await _context.BloodiedCharacters
                .Where(b => b.GameInstanceId == gameInstanceId && b.UserId == userId && day.Wounded.Contains(b.CharacterId))
                .ToDictionaryAsync(b => b.CharacterId, StringComparer.Ordinal);
            foreach (var id in day.Wounded)
            {
                if (!rows.TryGetValue(id, out var row))
                    _context.BloodiedCharacters.Add(row = new BloodiedCharacter { GameInstanceId = gameInstanceId, UserId = userId, CharacterId = id });
                row.RecoversAt = day.Bloodied[id];
            }
        }
        await _context.SaveChangesAsync();
        // Through the wallet, so a provision eaten while the player spends the same goods cannot lose either write.
        await _wallet.ConsumeAsync(gameInstanceId, userId, day.Eaten, day.Now);

        // Paid as one sum each, after the replay: the purse settles its land income up to now first.
        string source = $"auto user={userId} fights={day.Fights}";
        if (day.Materials.Count > 0)
            await _wallet.GrantAsync(gameInstanceId, userId,
                day.Materials.Select(m => new MaterialGrant { MaterialName = m.Key, Quantity = m.Value }).ToList(), source);
        if (day.Items.Count > 0) await _items.GrantAsync(gameInstanceId, userId, day.Items, source);
        if (day.Gold > 0) await _gold.GrantAsync(gameInstanceId, userId, day.Gold, source);

        if (transaction != null) await transaction.CommitAsync();

        if (_quests != null) await RecordQuestDeedsAsync(day);

        // One letter per company per settle (the inbox): "fought 4 times: 312 g, 2 gear", dated at its last fight.
        if (_letters != null)
        {
            foreach (var company in day.Reports.GroupBy(r => r.PartyId))
            {
                var last = company.Max(r => r.At);
                var lastSite = company.OrderBy(r => r.At).Last().SiteId;
                int items = company.Sum(r => (JsonNode.Parse(r.ItemsJson) as JsonArray)?.Count ?? 0);
                await _letters.SendAsync(gameInstanceId, userId, LetterKind.AutoReport, last,
                    $"auto:{company.Key}:{last.Ticks}", string.IsNullOrEmpty(lastSite) ? null : SiteSpec.RegionIdOf(lastSite),
                    company.Key.ToString(),
                    detail: $"{company.First().PartyName}|{company.Count()}|{company.Count(r => r.Won)}|{company.Sum(r => r.Gold)}|{items}");
            }
        }

        if (day.Fights > 0 || day.Eaten.Count > 0)
            _sessionLog.Log("AUTO-SETTLE",
                $"user={userId} companies={parties.Count} fights={day.Fights} gold={day.Gold} items={day.Items.Count} " +
                $"eaten={string.Join(",", day.Eaten.Select(e => $"{e.Key}x{e.Value}"))}");
    }

    private static bool IsStep(AutoStatus status) =>
        status is AutoStatus.Walking or AutoStatus.Fighting or AutoStatus.Patrolling or AutoStatus.Resting
            or AutoStatus.FallingBack;

    private static bool IsStopped(AutoStatus status) =>
        status is AutoStatus.OutOfProvisions or AutoStatus.RegionLost or AutoStatus.NobodyFree;

    private void Replay(PlayerParty party, Day day)
    {
        var floor = day.Now - AutoFightRules.MaximumReplay;
        var t = party.AutoSettledAt ?? day.Now;
        if (t < floor) t = floor;

        // Bounded however long the absence: a 48h replay is a few hundred steps at most.
        for (int guard = 0; guard < 5000; guard++)
        {
            if (IsStep(party.AutoStatus) && party.AutoStepEndsAt is DateTime ends)
            {
                if (ends > day.Now) break;
                var at = ends < floor ? floor : ends;
                Finish(party, day, at);
                t = at;
                continue;
            }

            if (IsStopped(party.AutoStatus) || party.AutoOrder == AutoOrder.None) break;
            if (!Decide(party, day, t)) break;
        }

        party.AutoSettledAt = day.Now;
        party.UpdatedAt = day.Now;
    }

    /// <summary>Ends the step in progress at <paramref name="at"/>: arrives, settles a fight or skirmish, or recovers.</summary>
    private void Finish(PlayerParty party, Day day, DateTime at)
    {
        var status = party.AutoStatus;
        party.AutoStepEndsAt = null;
        party.AutoStatus = AutoStatus.Ready;

        switch (status)
        {
            case AutoStatus.Walking:
                // Struck on the road: the ambush is fought where it halted the company.
                if (party.AmbushAt != null && party.ArrivesAt is DateTime arrives && at < arrives)
                {
                    Ambushed(party, day, at);
                    break;
                }
                Arrive(party, at);
                // Arriving at a site to fight starts the fight; arriving to patrol starts the patrol at the next decision.
                if (party.AutoOrder == AutoOrder.Roam && FindSite(day, party.AutoTargetSiteId) is SiteSpec site && site.IsFightable)
                    BeginFight(party, day, site, at);
                break;

            case AutoStatus.Fighting:
                if (FindSite(day, party.AutoTargetSiteId) is SiteSpec fought) SettleFight(party, day, fought, at);
                party.State = CompanyState.Idle;
                break;

            case AutoStatus.FallingBack:
                Arrive(party, at);
                break;

            case AutoStatus.Patrolling:
                if (day.Regions.TryGetValue(party.RegionId ?? "", out var land)) SettleSkirmish(party, day, land, at);
                break;
        }
    }

    /// <summary>
    /// What the company does next, from <paramref name="t"/>. Returns false when it has stopped or has
    /// nothing to do until something changes.
    /// </summary>
    private bool Decide(PlayerParty party, Day day, DateTime t)
    {
        var fighters = FightersOf(party, day);
        if (fighters.Count == 0) return Stop(party, AutoStatus.NobodyFree);

        var recovers = fighters.Select(id => day.Bloodied.TryGetValue(id, out var until) ? until : DateTime.MinValue).Max();
        if (recovers > t)
        {
            party.AutoStatus = AutoStatus.Resting;
            party.AutoStepEndsAt = recovers;
            return true;
        }

        if (party.AutoRegionId == null || !day.Regions.TryGetValue(party.AutoRegionId, out var region)
            || !region.IsOwnedByPlayer(day.UserId))
            return Stop(party, AutoStatus.RegionLost);

        int level = AutoFightRules.CompanyLevel(fighters.Select(id => day.Levels.TryGetValue(id, out var l) ? l : 1));
        var layout = day.LayoutOf(region);

        if (party.AutoOrder == AutoOrder.Patrol)
        {
            var keep = layout.Sites.FirstOrDefault(s => s.Type == LocationType.Castle) ?? layout.Sites.FirstOrDefault();
            if (keep == null) return Stop(party, AutoStatus.NothingToFight);
            if (!string.Equals(party.SiteId, keep.SiteId, StringComparison.Ordinal))
                return Walk(party, day, keep, t) || Stop(party, AutoStatus.NothingToFight);

            if (!AutoFightRules.CanFight(level, SkirmishLevel(layout))) return Stop(party, AutoStatus.NothingToFight);
            var stint = new List<(string, int)> { (ResourceNodeRules.Grain, ProvisionRules.PatrolGrainEaten(AmbushRules.PatrolSkirmishEvery)) };
            if (!Spend(day, stint)) return Stop(party, AutoStatus.OutOfProvisions);

            party.AutoTargetSiteId = keep.SiteId;
            party.AutoStatus = AutoStatus.Patrolling;
            party.AutoStepEndsAt = t + AmbushRules.PatrolSkirmishEvery;
            return true;
        }

        // Roam: the next site in its own rotation.
        var plans = new Dictionary<string, RoutePlan?>(StringComparer.Ordinal);
        var candidates = new List<AutoFightCandidate>();
        foreach (var site in layout.Sites.Where(s => s.IsFightable))
        {
            var plan = string.Equals(site.SiteId, party.SiteId, StringComparison.Ordinal) ? null : PlanTo(party, day, site);
            if (plan == null && !string.Equals(site.SiteId, party.SiteId, StringComparison.Ordinal)) continue;
            plans[site.SiteId] = plan;
            candidates.Add(new AutoFightCandidate(site.SiteId, site.Type, site.Level, plan?.Duration ?? TimeSpan.Zero));
        }

        var nextId = AutoFightRules.NextSite(candidates, level, ReadRecent(party));
        if (nextId == null) return Stop(party, AutoStatus.NothingToFight);
        var next = layout.Sites.First(s => s.SiteId == nextId);

        if (!Spend(day, ProvisionRules.CostOfFight(next.Type))) return Stop(party, AutoStatus.OutOfProvisions);

        party.AutoTargetSiteId = next.SiteId;
        var route = plans[next.SiteId];
        if (route == null || route.Duration <= TimeSpan.Zero) BeginFight(party, day, next, t);
        else StartWalk(party, day, next, route, t);
        return true;
    }

    private static bool Stop(PlayerParty party, AutoStatus why)
    {
        party.AutoStatus = why;
        party.AutoStepEndsAt = null;
        return false;
    }

    private bool Walk(PlayerParty party, Day day, SiteSpec to, DateTime t)
    {
        var plan = PlanTo(party, day, to);
        if (plan == null) return false;
        party.AutoTargetSiteId = to.SiteId;
        if (plan.Duration <= TimeSpan.Zero) { Arrive(party, t, to.SiteId); return true; }
        StartWalk(party, day, to, plan, t);
        return true;
    }

    /// <summary>
    /// Sets out on the road, as an ordinary journey the client already knows how to draw. The road is
    /// rolled now, by the same chance a steered company's is (patrols included), and seeded, so a
    /// re-run meets the same road. If it is ambushed, the step ends where the ambush strikes.
    /// </summary>
    private void StartWalk(PlayerParty party, Day day, SiteSpec to, RoutePlan route, DateTime t)
    {
        party.State = CompanyState.Travelling;
        party.FromSiteId = party.SiteId;
        party.ToSiteId = to.SiteId;
        party.SiteId = null;
        party.DepartedAt = t;
        party.ArrivesAt = t + route.Duration;
        party.RouteJson = JsonSerializer.Serialize(route.Legs);
        party.AmbushAt = null;
        party.HaltedAt = null;
        party.AutoStatus = AutoStatus.Walking;
        party.AutoStepEndsAt = t + route.Duration;

        var conditions = RegionConditionRules.For(day.Instance.ToString(), HarassmentRules.HourOf(t), day.Regions.Values);
        double chance = AmbushRules.ChanceForRoute(route.Legs.Select(leg =>
        {
            day.Regions.TryGetValue(leg.RegionId, out var land);
            var walk = TimeSpan.FromSeconds(leg.Seconds.Count > 0 ? leg.Seconds[^1] - leg.Seconds[0] : 0);
            double hunt = RegionConditionRules.AmbushChanceFactor(conditions.GetValueOrDefault(leg.RegionId));
            return (land?.Tier ?? 1, land != null && land.IsOwnedByPlayer(day.UserId), walk, day.Patrolled.Contains(leg.RegionId), hunt);
        }));
        if (RollRoad(chance, party.Id.ToString(), t) is double strikes)
        {
            party.AmbushAt = strikes;
            party.AutoStepEndsAt = t + TimeSpan.FromTicks((long)(route.Duration.Ticks * strikes));
        }
    }

    /// <summary>
    /// An auto company ambushed on the road fights it by itself (§6): a skirmish at the level of the
    /// land it was struck in. Won, it is paid as a patrol's skirmish and walks on; lost, it is Bloodied
    /// and walks back where it set out, as a steered company that fled would.
    /// </summary>
    private void Ambushed(PlayerParty party, Day day, DateTime at)
    {
        var bound = party.ToSiteId ?? party.AutoTargetSiteId ?? "";
        var regionId = PartyService.RegionAlong(party, at) ?? party.RegionId ?? SiteSpec.RegionIdOf(bound);
        int mobs = day.Regions.TryGetValue(regionId ?? "", out var land)
            ? SkirmishLevel(day.LayoutOf(land))
            : FindSite(day, bound)?.Level ?? 1;

        var fighters = FightersOf(party, day);
        int level = AutoFightRules.CompanyLevel(fighters.Select(id => day.Levels.TryGetValue(id, out var l) ? l : 1));
        bool won = Roll(AutoFightRules.WinChance(level, mobs), party.Id.ToString(), party.AutoFightCount++);

        var report = NewReport(party, day, bound, mobs, true, at, won, fighters);
        report.Ambush = true;
        party.AmbushAt = null;

        if (won)
        {
            PaySkirmish(report, day, mobs, RegionConditionRules.AmbushRewardFactor(
                RegionConditionRules.At(day.Instance.ToString(), regionId ?? "", at, day.Regions.Values)));
            party.AutoStatus = AutoStatus.Walking;
            party.AutoStepEndsAt = party.ArrivesAt;
        }
        else
        {
            Bloody(day, fighters, at);
            party.HaltedAt = at;
            PartyService.TurnBack(party, at);
            party.AutoTargetSiteId = null;
            party.AutoStatus = AutoStatus.FallingBack;
            party.AutoStepEndsAt = party.ArrivesAt;
        }
        day.Reports.Add(report);
    }

    private static void Arrive(PlayerParty party, DateTime at, string? siteId = null)
    {
        var to = siteId ?? party.ToSiteId ?? party.AutoTargetSiteId;
        party.State = CompanyState.Idle;
        if (!string.IsNullOrEmpty(to))
        {
            party.SiteId = to;
            party.RegionId = SiteSpec.RegionIdOf(to);
        }
        party.FromSiteId = null;
        party.ToSiteId = null;
        party.DepartedAt = null;
        party.ArrivesAt = null;
        party.RouteJson = null;
        party.AmbushAt = null;
        party.HaltedAt = null;
    }

    private void BeginFight(PlayerParty party, Day day, SiteSpec site, DateTime t)
    {
        party.State = CompanyState.InRun;
        party.SiteId = site.SiteId;
        party.RegionId = SiteSpec.RegionIdOf(site.SiteId);
        party.AutoTargetSiteId = site.SiteId;
        party.AutoStatus = AutoStatus.Fighting;
        party.AutoStepEndsAt = t + AutoFightRules.FightDuration(site.Type, BossRules.HasBoss(_content.RunTuning, site.Tier));
    }

    private void SettleFight(PlayerParty party, Day day, SiteSpec site, DateTime at)
    {
        var fighters = FightersOf(party, day);
        int level = AutoFightRules.CompanyLevel(fighters.Select(id => day.Levels.TryGetValue(id, out var l) ? l : 1));
        bool won = Roll(AutoFightRules.WinChance(level, site.Level), party.Id.ToString(), party.AutoFightCount++);

        var recent = ReadRecent(party);
        recent.Insert(0, site.SiteId);
        party.AutoRecentJson = JsonSerializer.Serialize(recent.Take(RecentKept).ToList());

        var report = NewReport(party, day, site.SiteId, site.Level, false, at, won, fighters);
        report.ProvisionsJson = JsonSerializer.Serialize(Grants(ProvisionRules.CostOfFight(site.Type)));

        if (won)
        {
            var full = RunRewardCalculator.Calculate(site.Level, site.Tier, _content.RunTuning, _content.DroppableItems,
                Dice, AutoFightRules.RarityStepsDown, site: site.Type);
            var materials = MaterialRewardCalculator.Calculate(site.Level, site.Tier, _content.RunTuning, _content.Materials, Dice, site: site.Type)
                .Select(m => (m.MaterialName, (int)AutoFightRules.Share(m.Quantity, Dice)));
            Pay(report, day, AutoFightRules.Share(full.Experience, Dice),
                full.Items.Where(_ => Dice.NextDouble() < AutoFightRules.RewardShare).ToList(), materials);
        }
        else Bloody(day, fighters, at);

        day.Reports.Add(report);
    }

    /// <summary>A patrol meets the mobs it keeps down: an ambush's skirmish at the region's level, paid at an ambush's share of a third.</summary>
    private void SettleSkirmish(PlayerParty party, Day day, WorldRegionData region, DateTime at)
    {
        var fighters = FightersOf(party, day);
        int level = AutoFightRules.CompanyLevel(fighters.Select(id => day.Levels.TryGetValue(id, out var l) ? l : 1));
        int mobs = SkirmishLevel(day.LayoutOf(region));
        bool won = Roll(AutoFightRules.WinChance(level, mobs), party.Id.ToString(), party.AutoFightCount++);

        var report = NewReport(party, day, party.AutoTargetSiteId ?? party.SiteId ?? "", mobs, true, at, won, fighters);
        report.ProvisionsJson = JsonSerializer.Serialize(new List<MaterialGrant>
        {
            new() { MaterialName = ResourceNodeRules.Grain, Quantity = ProvisionRules.PatrolGrainEaten(AmbushRules.PatrolSkirmishEvery) }
        });

        if (won) PaySkirmish(report, day, mobs);
        else Bloody(day, fighters, at);

        day.Reports.Add(report);
    }

    /// <summary>A skirmish's pay: an ambush's share of a tier-1 clear at the mobs' level, then a third of that.</summary>
    private void PaySkirmish(AutoFightReport report, Day day, int mobs, double factor = 1.0)
    {
        double share = AmbushRules.RewardShare * AutoFightRules.RewardShare;
        var full = RunRewardCalculator.Calculate(mobs, AmbushRules.SkirmishTier, _content.RunTuning, _content.DroppableItems,
            Dice, AutoFightRules.RarityStepsDown);
        var materials = MaterialRewardCalculator.Calculate(mobs, AmbushRules.SkirmishTier, _content.RunTuning, _content.Materials, Dice)
            .Select(m => (m.MaterialName, (int)Math.Round(AutoFightRules.Share(AmbushRules.Share(m.Quantity, Dice), Dice) * factor)));
        Pay(report, day, AutoFightRules.Share((long)Math.Round(full.Experience * AmbushRules.RewardShare * factor), Dice),
            full.Items.Where(_ => Dice.NextDouble() < share).ToList(), materials);
    }

    private AutoFightReport NewReport(PlayerParty party, Day day, string siteId, int level, bool skirmish, DateTime at,
        bool won, List<string> fighters)
    {
        day.Fights++;
        return new AutoFightReport
        {
            GameInstanceId = day.Instance,
            UserId = day.UserId,
            PartyId = party.Id,
            PartyName = party.Name.Length > 64 ? party.Name[..64] : party.Name,
            SiteId = siteId,
            Level = level,
            Skirmish = skirmish,
            At = at,
            Won = won,
            FighterIdsJson = MarchingArmy.WriteIds(fighters),
        };
    }

    private static void Pay(AutoFightReport report, Day day, long experience, List<ItemSaveData> items,
        IEnumerable<(string Name, int Quantity)> materials)
    {
        var won = materials.Where(m => m.Quantity > 0).ToList();
        report.Experience = experience;
        report.Gold = GoldRules.GoldForClear(experience);
        report.ItemsJson = JsonSerializer.Serialize(items);
        report.MaterialsJson = JsonSerializer.Serialize(won.Select(m => new MaterialGrant { MaterialName = m.Name, Quantity = m.Quantity }).ToList());

        day.Gold += report.Gold;
        day.Items.AddRange(items);
        foreach (var (name, quantity) in won)
            day.Materials[name] = day.Materials.TryGetValue(name, out var had) ? had + quantity : quantity;
    }

    private static void Bloody(Day day, List<string> fighters, DateTime at)
    {
        var recovers = BloodiedRules.RecoversAt(at);
        foreach (var id in fighters)
        {
            if (day.Bloodied.TryGetValue(id, out var until) && until >= recovers) continue;
            day.Bloodied[id] = recovers;
            day.Wounded.Add(id);
        }
    }

    /// <summary>Takes provisions from the wallet, all or none.</summary>
    private static bool Spend(Day day, IEnumerable<(string Good, int Quantity)> cost)
    {
        var list = cost.Where(c => c.Quantity > 0).ToList();
        var held = day.Goods.ToDictionary(g => g.Key, g => (long)g.Value, StringComparer.Ordinal);
        if (!ProvisionRules.CanAfford(held, list)) return false;
        foreach (var (good, quantity) in list)
        {
            day.Goods[good] -= quantity;
            day.Eaten[good] = day.Eaten.TryGetValue(good, out var had) ? had + quantity : quantity;
        }
        return true;
    }

    private static List<MaterialGrant> Grants(IEnumerable<(string Good, int Quantity)> cost) =>
        cost.Select(c => new MaterialGrant { MaterialName = c.Good, Quantity = c.Quantity }).ToList();

    /// <summary>Its members who may fight: not garrisoned, held or in a siege. Bloodied ones count; they rest.</summary>
    private static List<string> FightersOf(PlayerParty party, Day day) =>
        MarchingArmy.ReadIds(party.CharacterIdsJson).Where(id => !day.Committed.Contains(id)).ToList();

    private static SiteSpec? FindSite(Day day, string? siteId)
    {
        if (string.IsNullOrEmpty(siteId)) return null;
        return day.Regions.TryGetValue(SiteSpec.RegionIdOf(siteId), out var region)
            ? day.LayoutOf(region).FindSite(siteId)
            : null;
    }

    private static RoutePlan? PlanTo(PlayerParty party, Day day, SiteSpec to)
    {
        if (string.IsNullOrEmpty(party.SiteId) || string.IsNullOrEmpty(party.RegionId)) return null;
        if (!day.Regions.TryGetValue(party.RegionId, out var home)) return null;
        var from = day.LayoutOf(home).FindSite(party.SiteId);
        if (from == null || !day.Regions.TryGetValue(SiteSpec.RegionIdOf(to.SiteId), out var region)) return null;
        return TravelRules.PlanRoute(hex => day.ByHex.TryGetValue(hex, out var r) ? r : null!, home, from.Cell, region, to.Cell);
    }

    private static List<string> ReadRecent(PlayerParty party)
    {
        try { return JsonSerializer.Deserialize<List<string>>(party.AutoRecentJson) ?? new List<string>(); }
        catch (JsonException) { return new List<string>(); }
    }

    /// <summary>
    /// Auto mode's wins count toward quests at the share it is paid at (Mike, 2026-10-07): its kills at a
    /// third of the plan, and a clear or an ambush counts one time in three.
    /// </summary>
    private async Task RecordQuestDeedsAsync(Day day)
    {
        foreach (var report in day.Reports.Where(r => r.Won))
        {
            var regionId = SiteSpec.RegionIdOf(report.SiteId ?? "");
            if (regionId == null || !day.Regions.TryGetValue(regionId, out var region)) continue;
            var site = report.Skirmish ? null : day.LayoutOf(region).FindSite(report.SiteId!);

            bool counts = Dice.NextDouble() < AutoFightRules.RewardShare;
            await _quests!.RecordAsync(day.Instance, day.UserId, new QuestDeed
            {
                RegionId = regionId,
                ClearedSiteId = counts && site != null && site.IsFightable ? site.SiteId : null,
                AmbushWon = counts && report.Ambush,
                Kills = new Dictionary<string, int>(_quests.EstimatedKills(region.Biome,
                    site?.Tier ?? AmbushRules.SkirmishTier, site?.Type, AutoFightRules.RewardShare))
            });
        }
    }

    /// <summary>The level of a region's mobs (<see cref="AutoFightRules.MobLevel"/>).</summary>
    private static int SkirmishLevel(RegionLayout layout) => AutoFightRules.MobLevel(layout.Sites);

    private static IEnumerable<int> LevelsOf(UserSaveData? save, List<string> ids) =>
        ids.Select(id => (int)Math.Max(1, save?.Characters?.FirstOrDefault(c => c?.Id == id)?.Level ?? 1));

    private static AutoReportDto ToDto(AutoFightReport r) => new(
        r.Id, r.PartyId, r.PartyName, r.SiteId, r.Skirmish, r.Level,
        DateTime.SpecifyKind(r.At, DateTimeKind.Utc), r.Won,
        Read<List<string>>(r.FighterIdsJson) ?? new List<string>(),
        r.Experience, r.Gold,
        Read<List<ItemSaveData>>(r.ItemsJson) ?? new List<ItemSaveData>(),
        Read<List<MaterialGrant>>(r.MaterialsJson) ?? new List<MaterialGrant>(),
        Read<List<MaterialGrant>>(r.ProvisionsJson) ?? new List<MaterialGrant>(),
        r.Ambush);

    private static T? Read<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return default; }
    }

    private static AutoOutcome Mismatch => new(AutoError.ContractMismatch, "Your game is running different rules from the server.");

    private async Task<JsonNode?> LoadWorldAsync(Guid gameInstanceId)
    {
        var row = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        return row == null ? null : JsonNode.Parse(row.GameData);
    }
}
