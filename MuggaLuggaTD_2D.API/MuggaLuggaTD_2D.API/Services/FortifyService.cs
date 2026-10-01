using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Hubs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

public enum FortifyError
{
    None,
    WorldNotFound,
    RegionNotFound,
    Refused,
    CannotAfford
}

public record FortifyOutcome(FortifyError Error, FortifyResponse? Response = null, string? Message = null)
{
    public bool Succeeded => Error == FortifyError.None;
}

/// <summary>
/// Fortifying a region (Hiring Hall phase 3): the holder spends goods, the works take
/// <see cref="FortifyRules.Duration"/>, and the region's entrenchment rises by one.
///
/// <para><b>Completion is applied by the sweep and on reads, not by a timer per region.</b>
/// <see cref="SiegeScheduler"/> calls <see cref="CompleteAllDueAsync"/> once a minute, and the world
/// GET calls <see cref="CompleteDueAsync"/> before it settles, so a player opening the map never sees
/// finished works still standing. Either way the realm is settled <i>at the moment the works
/// finished</i>, so the old walls earn up to then and the new ones from then on.</para>
/// </summary>
public class FortifyService
{
    private readonly ApplicationDbContext _context;
    private readonly MaterialWalletService _wallet;
    private readonly SeasonScoreService _seasons;
    private readonly WarLogService _warLog;
    private readonly IHubContext<GameHub> _hub;
    private readonly ISessionLog _sessionLog;
    private readonly HiringService? _hiring;

    /// <summary>Overridable so tests can move time.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public FortifyService(ApplicationDbContext context, MaterialWalletService wallet, SeasonScoreService seasons,
        WarLogService warLog, IHubContext<GameHub> hub, ISessionLog sessionLog, HiringService? hiring = null)
    {
        _context = context;
        _wallet = wallet;
        _seasons = seasons;
        _warLog = warLog;
        _hub = hub;
        _sessionLog = sessionLog;
        _hiring = hiring;
    }

    /// <summary>Starts the works: checks the region, pays the bill, marks the region for everyone to see.</summary>
    public async Task<FortifyOutcome> BeginAsync(Guid gameInstanceId, string userId, string regionId)
    {
        await CompleteDueAsync(gameInstanceId);

        var row = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (row == null) return new FortifyOutcome(FortifyError.WorldNotFound, Message: "World view data not found.");

        var world = JsonNode.Parse(row.GameData);
        var node = WorldRegionBlob.FindRegion(world, regionId);
        if (node == null) return new FortifyOutcome(FortifyError.RegionNotFound, Message: "Region not found in this world.");
        var region = WorldRegionBlob.ReadRegion(node);

        bool besieged = await _context.Sieges.AnyAsync(s =>
            s.GameInstanceId == gameInstanceId && s.RegionId == regionId
            && (s.State == SiegeState.Mustering || s.State == SiegeState.Assault));

        var refusal = FortifyRules.Check(region, userId, besieged);
        if (refusal != FortifyRefusal.None)
            return new FortifyOutcome(FortifyError.Refused, Message: FortifyRules.Explain(refusal));

        // Goods the workers have gathered so far are paid first, so the bill meets the real stock.
        if (_hiring != null) await _hiring.SettlePlayerAsync(gameInstanceId, userId);

        int toLevel = region.Entrenchment + 1;
        var bill = FortifyRules.CostFor(toLevel, region.Tier);
        var paid = await _wallet.SpendAsync(gameInstanceId, userId,
            bill.Select(b => new MaterialGrant { MaterialName = b.Good, Quantity = b.Quantity }).ToList(),
            $"fortify region={regionId} to={toLevel}");
        if (!paid.Succeeded) return new FortifyOutcome(FortifyError.CannotAfford, Message: paid.Message);

        var now = Clock();
        var endsAt = now + FortifyRules.Duration;
        WorldRegionBlob.SetFortifying(node, toLevel, endsAt);

        _context.RegionFortifications.Add(new RegionFortification
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            RegionId = regionId,
            FromLevel = region.Entrenchment,
            ToLevel = toLevel,
            Spent = string.Join(",", bill.Select(b => $"{b.Good}:{b.Quantity}")),
            StartedAt = now,
            CompletesAt = endsAt,
            State = FortificationState.UnderWay
        });

        await PersistAndBroadcastAsync(row, world!);
        _sessionLog.Log("FORTIFY-BEGIN", $"user={userId} realm={gameInstanceId} region={regionId} {region.Entrenchment}->{toLevel} ends={endsAt:O}");

        return new FortifyOutcome(FortifyError.None, new FortifyResponse(
            regionId, region.Entrenchment, toLevel, now, endsAt,
            bill.ToDictionary(b => b.Good, b => b.Quantity)));
    }

    /// <summary>
    /// Finishes every work in the realm that is due. Works whose region changed hands are cancelled.
    /// Returns how many finished or were cancelled.
    /// </summary>
    public async Task<int> CompleteDueAsync(Guid gameInstanceId)
    {
        var now = Clock();
        var due = await _context.RegionFortifications
            .Where(f => f.GameInstanceId == gameInstanceId && f.State == FortificationState.UnderWay && f.CompletesAt <= now)
            .OrderBy(f => f.CompletesAt)
            .ToListAsync();
        if (due.Count == 0) return 0;

        var row = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        var world = row == null ? null : JsonNode.Parse(row.GameData);
        var finished = new List<RegionFortification>();

        foreach (var works in due)
        {
            works.ResolvedAt = works.CompletesAt;
            var node = WorldRegionBlob.FindRegion(world, works.RegionId);
            var region = node == null ? null : WorldRegionBlob.ReadRegion(node);

            // These works, still standing on land the builder holds. Judged by owner and target, not
            // by the exact finishing tick: a mismatch there must not strand scaffolding that would
            // then block the region from ever being fortified again.
            bool ours = region != null
                        && region.IsOwnedByPlayer(works.UserId)
                        && region.FortifyingTo == works.ToLevel;

            if (!ours)
            {
                works.State = FortificationState.Cancelled;
                _sessionLog.Log("FORTIFY-CANCEL", $"realm={gameInstanceId} region={works.RegionId} user={works.UserId}");
                continue;
            }

            // The old walls earn up to the moment the new ones stand, then the realm is re-rated.
            await _seasons.SettleAllAsync(gameInstanceId, world, works.CompletesAt);
            node!["Entrenchment"] = works.ToLevel;
            WorldRegionBlob.ClearFortifying(node);
            await _seasons.SettleAllAsync(gameInstanceId, world, works.CompletesAt);

            works.State = FortificationState.Done;
            finished.Add(works);
        }

        if (row != null && world != null && finished.Count > 0)
            await PersistAndBroadcastAsync(row, world);
        else
            await _context.SaveChangesAsync();

        foreach (var works in finished)
        {
            _sessionLog.Log("FORTIFY-DONE", $"realm={gameInstanceId} region={works.RegionId} user={works.UserId} level={works.ToLevel}");
            await _warLog.RecordAsync(gameInstanceId, WarLogKind.Fortified, works.UserId, null, works.RegionId,
                RegionHoldCalculator.EntrenchmentLabel(works.ToLevel), works.CompletesAt);
        }

        return due.Count;
    }

    /// <summary>The sweep: every realm with works due.</summary>
    public async Task<int> CompleteAllDueAsync()
    {
        var now = Clock();
        var realms = await _context.RegionFortifications
            .Where(f => f.State == FortificationState.UnderWay && f.CompletesAt <= now)
            .Select(f => f.GameInstanceId).Distinct().ToListAsync();

        int total = 0;
        foreach (var realm in realms) total += await CompleteDueAsync(realm);
        return total;
    }

    /// <summary>A season reset: the map is new, so works on the old one go with it.</summary>
    public static Task ResetRealmAsync(ApplicationDbContext context, Guid realmId)
    {
        context.RegionFortifications.RemoveRange(context.RegionFortifications.Where(f => f.GameInstanceId == realmId));
        return Task.CompletedTask;
    }

    private async Task PersistAndBroadcastAsync(WorldViewGameData row, JsonNode world)
    {
        row.GameData = world.ToJsonString();
        row.UpdatedAt = Clock();
        await _context.SaveChangesAsync();

        var payload = JsonSerializer.Deserialize<object>(row.GameData) ?? new { };
        await _hub.Clients.Group(row.GameInstanceId.ToString())
            .SendAsync("WorldViewGameDataUpdated", new WorldViewGameDataUpdated(row.GameInstanceId, payload, row.UpdatedAt));
    }
}
