using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Hubs;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// Acting against a rival's region. The client names a target and a marching party; the server
/// decides what happens, applies it to the shared world, and broadcasts the change.
///
/// <para>Raiding wears a region down; it never takes it. Sieging (<see cref="SiegeController"/>) is what
/// moves territory, and it is gated behind raids having already worn the region down.</para>
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/raid")]
[Authorize]
public class RaidController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHubContext<GameHub> _hubContext;
    private readonly WorldRaidService _raids;
    private readonly SeasonScoreService _seasons;
    private readonly ISessionLog _sessionLog;
    private readonly WarLogService _warLog;

    public RaidController(
        ApplicationDbContext context,
        IHubContext<GameHub> hubContext,
        WorldRaidService raids,
        SeasonScoreService seasons,
        ISessionLog sessionLog,
        WarLogService warLog)
    {
        _context = context;
        _hubContext = hubContext;
        _raids = raids;
        _seasons = seasons;
        _sessionLog = sessionLog;
        _warLog = warLog;
    }

    /// <summary>Raids a rival region, wearing its resolve down.</summary>
    [HttpPost]
    public async Task<ActionResult<RegionRaidResponse>> Raid(Guid gameInstanceId, [FromBody] RegionRaidRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        // Raiding requires membership of the instance, not ownership of it.
        if (!await HasAccessToGameInstance(gameInstanceId, userId))
            return Forbid();

        // Marching after the bell would score into a season that has already been settled.
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);

        var (outcome, updatedWorld) = await _raids.RaidAsync(gameInstanceId, userId, request);

        if (!outcome.Succeeded)
        {
            _sessionLog.Log("RAID-DENY", $"user={userId} region={request.RegionId} {outcome.Error}: {outcome.Message}");
            return ToError(outcome);
        }

        // A successful outcome always carries both; treat anything else as a server fault rather
        // than reporting a raid we then fail to persist.
        if (updatedWorld == null || outcome.Response == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "The raid resolved without producing world state." });
        }

        await PersistAndBroadcastAsync(gameInstanceId, updatedWorld);

        var r = outcome.Response;
        _sessionLog.Log("RAID",
            $"user={userId} region={r.RegionId} {(r.AttackerWins ? "WIN" : "REPELLED")} " +
            $"march={r.MarchingPower:F0} hold={r.Hold} bar={r.RaidBar} " +
            $"roll={r.D20Roll}{(r.Modifier >= 0 ? "+" : "")}{r.Modifier}={r.Total} " +
            $"resolve={r.ResolveBefore}->{r.ResolveAfter}");

        // Both sides of a raid can score, and which one does is the whole point of the fight:
        // contesting has to beat sitting still, and a defence that holds has to be worth something
        // to a defender who was not even online for it. Neither moves a rate - a raid takes no
        // ground, it only wears it down.
        if (r.AttackerWins)
            await _seasons.AwardAsync(gameInstanceId, userId, SeasonDeed.RaidLanded);
        else if (!string.IsNullOrEmpty(outcome.DefenderUserId))
            await _seasons.AwardAsync(gameInstanceId, outcome.DefenderUserId, SeasonDeed.RaidRepelled);

        await _warLog.RecordAsync(gameInstanceId,
            r.AttackerWins ? WarLogKind.RaidLanded : WarLogKind.RaidRepelled,
            userId, outcome.DefenderUserId, r.RegionId,
            r.AttackerWins ? $"resolve {r.ResolveBefore} → {r.ResolveAfter}" : null);

        return Ok(r);
    }

    /// <summary>
    /// When this player may next raid a given region. The dossier asks so it can show the wait
    /// rather than offering a march that will be refused.
    /// </summary>
    [HttpGet("cooldown/{regionId}")]
    public async Task<ActionResult<RaidCooldownResponse>> Cooldown(Guid gameInstanceId, string regionId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var endsAt = await _raids.CooldownEndsAtAsync(gameInstanceId, userId, regionId);
        return Ok(new RaidCooldownResponse(regionId, endsAt, endsAt <= DateTime.UtcNow));
    }

    private ActionResult ToError(RaidOutcome outcome) => outcome.Error switch
    {
        RaidError.WorldNotFound or RaidError.RegionNotFound
            => NotFound(new { message = outcome.Message }),
        RaidError.ContractMismatch
            => Conflict(new { message = outcome.Message }),
        // A cooldown is not a malformed request, it is a "not yet" — and the client shows the wait.
        RaidError.OnCooldown
            => StatusCode(StatusCodes.Status429TooManyRequests, new { message = outcome.Message }),
        _ => BadRequest(new { message = outcome.Message })
    };

    /// <summary>
    /// Writes the server-mutated world back and notifies everyone in the instance, so a defender
    /// sees their resolve drop without polling. Mirrors what PveController does on a claim.
    /// </summary>
    private async Task PersistAndBroadcastAsync(Guid gameInstanceId, JsonNode world)
    {
        var row = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (row == null) return;

        row.GameData = world.ToJsonString();
        row.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var payload = JsonSerializer.Deserialize<object>(row.GameData) ?? new { };
        await _hubContext.Clients.Group(gameInstanceId.ToString())
            .SendAsync("WorldViewGameDataUpdated", new WorldViewGameDataUpdated(gameInstanceId, payload, row.UpdatedAt));
    }

    private async Task<bool> HasAccessToGameInstance(Guid gameInstanceId, string userId)
    {
        return await _context.GameInstances
            .AnyAsync(g => g.Id == gameInstanceId &&
                (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
    }
}
