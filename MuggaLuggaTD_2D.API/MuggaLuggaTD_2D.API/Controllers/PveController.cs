using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Hubs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// PvE conquest. The client reports entering and completing a location; the server decides whether
/// that claim is admissible and applies the resulting world change itself.
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/pve")]
[Authorize]
public class PveController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHubContext<GameHub> _hubContext;
    private readonly WorldPveService _pve;
    private readonly ISessionLog _sessionLog;

    public PveController(ApplicationDbContext context, IHubContext<GameHub> hubContext, WorldPveService pve, ISessionLog sessionLog)
    {
        _context = context;
        _hubContext = hubContext;
        _pve = pve;
        _sessionLog = sessionLog;
    }

    /// <summary>Opens a run as the player enters the combat scene.</summary>
    [HttpPost("begin")]
    public async Task<ActionResult<PveBeginResponse>> Begin(Guid gameInstanceId, [FromBody] PveBeginRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, runId) = await _pve.BeginAsync(gameInstanceId, userId, request);
        if (!outcome.Succeeded)
        {
            _sessionLog.Log("PVE-BEGIN-DENY", $"user={userId} loc={request.LocationId} {outcome.Error}: {outcome.Message}");
            return ToError(outcome);
        }

        _sessionLog.Log("PVE-BEGIN", $"user={userId} loc={request.LocationId} run={runId}");
        return Ok(new PveBeginResponse(runId));
    }

    /// <summary>Claims the conquest for a completed run.</summary>
    [HttpPost("claim")]
    public async Task<ActionResult<PveClaimResponse>> Claim(Guid gameInstanceId, [FromBody] PveClaimRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        var displayName = user?.DisplayName ?? user?.UserName;

        var (outcome, response, updatedWorld) = await _pve.ClaimAsync(gameInstanceId, userId, displayName, request);
        if (!outcome.Succeeded)
        {
            _sessionLog.Log("PVE-CLAIM-DENY", $"user={userId} run={request.RunId} {outcome.Error}: {outcome.Message}");
            return ToError(outcome);
        }

        if (updatedWorld == null || response == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "Conquest resolved without producing world state." });
        }

        await PersistAndBroadcastAsync(gameInstanceId, updatedWorld);
        _sessionLog.Log("PVE-CLAIM",
            $"user={userId} loc={response.LocationId} outcome={response.ConquestOutcome} " +
            $"xp={response.Experience} items={response.Items.Count}");
        return Ok(response);
    }

    private ActionResult ToError(PveOutcome outcome) => outcome.Error switch
    {
        PveError.WorldNotFound or PveError.LocationNotFound or PveError.RunNotFound
            => NotFound(new { message = outcome.Message }),
        PveError.ContractMismatch or PveError.RunAlreadyClaimed
            => Conflict(new { message = outcome.Message }),
        _ => BadRequest(new { message = outcome.Message })
    };

    /// <summary>
    /// Writes the server-mutated world back and notifies everyone in the instance, so other players
    /// see the capture or the cleared location without polling.
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
