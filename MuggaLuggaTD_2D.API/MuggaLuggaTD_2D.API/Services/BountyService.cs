using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Hubs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>One wanted champion, as the board and the map show it.</summary>
public record BountyView(
    long Slot, string SiteId, string Name, string Title, string People, BiomeType Biome,
    string RegionId, int Tier, int Level, List<RiftMutator> Mutators, long Gold,
    DateTime AppearedAt, DateTime MovesAt, DateTime EscapesAt,
    string? ClaimedByName, DateTime? ClaimedAt, bool ClaimedByMe, DateTime? MyNextAttemptAt);

/// <summary>The realm's champions still roaming, and those brought down or escaped in the last day.</summary>
public record BountiesResponse(List<BountyView> Live, List<BountyView> Recent, DateTime ServerNow);

/// <summary>What the hub sends when a champion is brought down; the client asks for the rest.</summary>
public record BountyClaimedNotification(long Slot, string Name, string Title, string ClaimedByName, string RegionId);

/// <summary>A champion, where it stands now, and what is stored about it.</summary>
public sealed record BountyState(BountyChampion Champion, string RegionId, WorldRegionData Region, Bounty? Row, int Season, DateTime SeasonStart);

/// <summary>
/// Bounties: wanted champions (Active Content spec, A). Who each champion is and where it walks is
/// <see cref="BountyRules"/>; this service reads the realm for them, keeps the claims, and answers the
/// board. Nothing ticks: a champion appears, moves and escapes on the clock alone.
/// </summary>
public class BountyService
{
    private static readonly TimeSpan RecentFor = TimeSpan.FromHours(24);

    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly ILogger<BountyService> _logger;
    private readonly IHubContext<GameHub>? _hub;
    private readonly WarLogService? _warLog;
    private readonly LetterService? _letters;

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public BountyService(ApplicationDbContext context, IGameContentProvider content, ILogger<BountyService> logger,
        IHubContext<GameHub>? hub = null, WarLogService? warLog = null, LetterService? letters = null)
    {
        _warLog = warLog;
        _letters = letters;
        _context = context;
        _content = content;
        _logger = logger;
        _hub = hub;
    }

    // -----------------------------------------------------------------
    // Reading
    // -----------------------------------------------------------------

    /// <summary>
    /// The champion of <paramref name="slot"/> and where it stands at <paramref name="at"/>; null when the realm
    /// has no world, the slot is not of this season, or the map has no land for it to walk.
    /// </summary>
    public async Task<BountyState?> StateAsync(Guid gameInstanceId, long slot, DateTime at)
    {
        var instance = await _context.GameInstances.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        if (instance == null || BountyRules.SlotStart(slot) < instance.SeasonStartedAt) return null;
        var regions = await RegionsAsync(gameInstanceId);
        if (regions == null) return null;

        var row = await _context.Bounties.AsNoTracking().FirstOrDefaultAsync(b =>
            b.GameInstanceId == gameInstanceId && b.SeasonNumber == instance.SeasonNumber && b.Slot == slot);
        return StateOf(gameInstanceId, slot, at, regions, row, instance.SeasonNumber, instance.SeasonStartedAt);
    }

    private BountyState? StateOf(Guid gameInstanceId, long slot, DateTime at, List<WorldRegionData> regions, Bounty? row,
        int season, DateTime seasonStart)
    {
        var champion = BountyRules.Champion(gameInstanceId.ToString(), slot, regions, _content.EnemyPeoples);
        if (champion == null) return null;

        string regionId = row?.ClaimedInRegionId ?? row?.PinnedRegionId ?? BountyRules.RegionAt(champion, at)!;
        var region = regions.FirstOrDefault(r => r.RegionId == regionId);
        return region == null ? null : new BountyState(champion, regionId, region, row, season, seasonStart);
    }

    /// <summary>The champion's level and tier where it stands, and the bounty on its head.</summary>
    public (int Tier, int Level, long Gold) Terms(BountyState state)
    {
        int tier = BountyRules.TierIn(state.Region.Tier);
        int level = BountyRules.LevelOf(tier);
        long clearGold = RunRewardCalculator.Calculate(level, tier, _content.RunTuning, null, null, site: BountyRules.SiteType).Gold;
        return (tier, level, BountyRules.Bounty(clearGold));
    }

    /// <summary>The board: every champion still roaming, and those claimed or escaped in the last day.</summary>
    public async Task<BountiesResponse> ViewAsync(Guid gameInstanceId, string userId)
    {
        var now = Clock();
        var instance = await _context.GameInstances.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        var regions = instance == null ? null : await RegionsAsync(gameInstanceId);
        if (instance == null || regions == null) return new BountiesResponse(new(), new(), now);

        // Everything that roamed in the last day and a half, plus the day after: live, claimed or escaped.
        long newest = BountyRules.SlotOf(now);
        int back = (int)Math.Ceiling((BountyRules.Lifetime + RecentFor).TotalHours / BountyRules.SlotLength.TotalHours);
        var rows = await _context.Bounties.AsNoTracking()
            .Where(b => b.GameInstanceId == gameInstanceId && b.SeasonNumber == instance.SeasonNumber && b.Slot >= newest - back)
            .ToListAsync();
        var claimers = rows.Where(r => r.ClaimedByUserId != null).Select(r => r.ClaimedByUserId!).Distinct().ToList();
        var names = await _context.Users.AsNoTracking().Where(u => claimers.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => PlayerNames.Of(u.DisplayName, u.UserName));

        var attempts = await LastAttemptsAsync(gameInstanceId, userId, now);

        var live = new List<BountyView>();
        var recent = new List<BountyView>();
        for (long slot = newest - back; slot <= newest; slot++)
        {
            if (BountyRules.SlotStart(slot) < instance.SeasonStartedAt || BountyRules.SlotStart(slot) > now) continue;
            var row = rows.FirstOrDefault(r => r.Slot == slot);
            var state = StateOf(gameInstanceId, slot, now, regions, row, instance.SeasonNumber, instance.SeasonStartedAt);
            if (state == null) continue;

            bool claimed = row?.ClaimedByUserId != null;
            DateTime endedAt = claimed ? row!.ClaimedAt!.Value : BountyRules.EscapesAt(slot);
            bool roaming = !claimed && now < BountyRules.EscapesAt(slot);
            if (!roaming && now - endedAt > RecentFor) continue;

            var view = ViewOf(state, now, claimed ? names.GetValueOrDefault(row!.ClaimedByUserId!, "A herald") : null,
                row?.ClaimedAt, claimed && row!.ClaimedByUserId == userId,
                attempts.TryGetValue(BountyRules.SiteId(slot), out var last) ? last + BountyRules.AttemptEvery : null, now);
            (roaming ? live : recent).Add(view);
        }
        return new BountiesResponse(live, recent.OrderByDescending(v => v.ClaimedAt ?? v.EscapesAt).ToList(), now);
    }

    private BountyView ViewOf(BountyState state, DateTime now, string? claimedBy, DateTime? claimedAt, bool mine,
        DateTime? nextAttempt, DateTime serverNow)
    {
        var (tier, level, gold) = Terms(state);
        var c = state.Champion;
        return new BountyView(c.Slot, BountyRules.SiteId(c.Slot), c.Name, c.Title, c.People, c.Biome,
            state.RegionId, tier, level, c.Mutators, gold,
            BountyRules.SlotStart(c.Slot), BountyRules.MovesAt(c.Slot, now), BountyRules.EscapesAt(c.Slot),
            claimedBy, claimedAt, mine, nextAttempt > serverNow ? nextAttempt : null);
    }

    /// <summary>When this player last went in after each champion within the attempt window.</summary>
    private async Task<Dictionary<string, DateTime>> LastAttemptsAsync(Guid gameInstanceId, string userId, DateTime now)
    {
        var since = now - BountyRules.AttemptEvery;
        var runs = await _context.PveRuns.AsNoTracking()
            .Where(r => r.GameInstanceId == gameInstanceId && r.UserId == userId
                        && r.LocationId.StartsWith(BountyRules.SitePrefix) && r.StartedAt > since)
            .Select(r => new { r.LocationId, r.StartedAt })
            .ToListAsync();
        return runs.GroupBy(r => r.LocationId).ToDictionary(g => g.Key, g => g.Max(r => r.StartedAt));
    }

    /// <summary>When this player last went in after <paramref name="siteId"/>, if within the attempt window.</summary>
    public async Task<DateTime?> LastAttemptAsync(Guid gameInstanceId, string userId, string siteId, DateTime now) =>
        (await LastAttemptsAsync(gameInstanceId, userId, now)).TryGetValue(siteId, out var at) ? at : null;

    private async Task<List<WorldRegionData>?> RegionsAsync(Guid gameInstanceId)
    {
        var worldRow = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        return worldRow == null ? null : WorldRegionBlob.ReadAllRegions(JsonNode.Parse(worldRow.GameData));
    }

    // -----------------------------------------------------------------
    // Claiming
    // -----------------------------------------------------------------

    /// <summary>
    /// Claims a champion for <paramref name="userId"/>, if nobody has: true for the first claim, false for
    /// any later one. Two winners racing cannot both be first: the row's revision (or, for a champion with
    /// no row yet, its unique slot) refuses the second write.
    /// </summary>
    public async Task<bool> TryClaimAsync(Guid gameInstanceId, int season, long slot, string userId, string regionId)
    {
        var now = Clock();
        for (int tries = 0; tries < Concurrency.Tries; tries++)
        {
            var row = await _context.Bounties.FirstOrDefaultAsync(b =>
                b.GameInstanceId == gameInstanceId && b.SeasonNumber == season && b.Slot == slot);
            if (row != null)
            {
                await _context.Entry(row).ReloadAsync();
                if (row.ClaimedByUserId != null) return false;
            }
            else
            {
                row = new Bounty { GameInstanceId = gameInstanceId, SeasonNumber = season, Slot = slot };
                _context.Bounties.Add(row);
            }

            row.ClaimedByUserId = userId;
            row.ClaimedAt = now;
            row.ClaimedInRegionId = regionId;
            try
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation("Bounty {Slot} in {Realm} claimed by {User} in {Region}.", slot, gameInstanceId, userId, regionId);
                return true;
            }
            catch (DbUpdateException)
            {
                // Someone else wrote it first: forget ours, and look again.
                _context.Entry(row).State = EntityState.Detached;
            }
        }
        return false;
    }

    /// <summary>
    /// Tells the realm a champion fell: the war log, a letter to every other player who went after it, and the hub
    /// (the map's skull goes, and the board re-reads).
    /// </summary>
    public async Task AnnounceClaimedAsync(Guid gameInstanceId, string userId, BountyClaimedNotification claimed)
    {
        var now = Clock();
        string detail = $"{claimed.Name}|{claimed.Title}";
        if (_warLog != null)
            await _warLog.RecordAsync(gameInstanceId, WarLogKind.BountyClaimed, userId, null, claimed.RegionId, detail, now,
                actorName: claimed.ClaimedByName);

        if (_letters != null)
        {
            string site = BountyRules.SiteId(claimed.Slot);
            var hunters = await _context.PveRuns.AsNoTracking()
                .Where(r => r.GameInstanceId == gameInstanceId && r.LocationId == site && r.UserId != userId)
                .Select(r => r.UserId).Distinct().ToListAsync();
            foreach (var hunter in hunters)
                await _letters.SendAsync(gameInstanceId, hunter, LetterKind.BountyClaimed, now, $"bounty:{claimed.Slot}:claimed",
                    claimed.RegionId, claimed.Slot.ToString(), claimed.ClaimedByName, detail);
        }

        if (_hub != null)
            await _hub.Clients.Group(gameInstanceId.ToString()).SendAsync("BountyClaimed", claimed);
    }

    // -----------------------------------------------------------------
    // Debug (Combat Debug → Bounties)
    // -----------------------------------------------------------------

    /// <summary>Stands the newest unclaimed champion in <paramref name="regionId"/>. Returns why not, or null.</summary>
    public async Task<string?> DebugPinAsync(Guid gameInstanceId, string regionId)
    {
        var now = Clock();
        var instance = await _context.GameInstances.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        var regions = instance == null ? null : await RegionsAsync(gameInstanceId);
        if (instance == null || regions == null) return "No world.";
        if (!regions.Any(r => r.RegionId == regionId)) return $"No region {regionId}.";

        var rows = await _context.Bounties
            .Where(b => b.GameInstanceId == gameInstanceId && b.SeasonNumber == instance.SeasonNumber).ToListAsync();
        var slot = BountyRules.LiveSlots(now, instance.SeasonStartedAt)
            .Where(s => rows.FirstOrDefault(r => r.Slot == s)?.ClaimedByUserId == null)
            .Select(s => (long?)s).LastOrDefault();
        if (slot == null) return "No champion is roaming unclaimed (the season may be younger than its first slot).";

        var row = rows.FirstOrDefault(r => r.Slot == slot);
        if (row == null)
        {
            row = new Bounty { GameInstanceId = gameInstanceId, SeasonNumber = instance.SeasonNumber, Slot = slot.Value };
            _context.Bounties.Add(row);
        }
        row.PinnedRegionId = regionId;
        await _context.SaveChangesAsync();
        return null;
    }

    /// <summary>Forgets this season's claims and pins, and this player's attempts, so every champion can be fought again.</summary>
    public async Task DebugReleaseAsync(Guid gameInstanceId, string userId)
    {
        var instance = await _context.GameInstances.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        if (instance == null) return;
        _context.Bounties.RemoveRange(await _context.Bounties
            .Where(b => b.GameInstanceId == gameInstanceId && b.SeasonNumber == instance.SeasonNumber).ToListAsync());
        _context.PveRuns.RemoveRange(await _context.PveRuns
            .Where(r => r.GameInstanceId == gameInstanceId && r.UserId == userId && r.LocationId.StartsWith(BountyRules.SitePrefix))
            .ToListAsync());
        await _context.SaveChangesAsync();
    }
}
