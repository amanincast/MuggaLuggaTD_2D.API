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
/// Holding your own ground: who is stationed where, and buying back the ones a rival took.
///
/// <para>Garrisoning used to happen entirely on the client, which wrote the shared world itself. That
/// is the last such write in the game and it was also the most damaging — see
/// <see cref="WorldGarrisonService"/>. The client now names a site and a list of characters, exactly as
/// it does for a raid or a siege, and the server decides the rest.</para>
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/garrison")]
[Authorize]
public class GarrisonController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHubContext<GameHub> _hubContext;
    private readonly WorldGarrisonService _garrisons;
    private readonly ISessionLog _sessionLog;

    public GarrisonController(
        ApplicationDbContext context,
        IHubContext<GameHub> hubContext,
        WorldGarrisonService garrisons,
        ISessionLog sessionLog)
    {
        _context = context;
        _hubContext = hubContext;
        _garrisons = garrisons;
        _sessionLog = sessionLog;
    }

    /// <summary>Sets a site's garrison to exactly the characters the player may station there.</summary>
    [HttpPost]
    public async Task<ActionResult<GarrisonResponse>> Set(Guid gameInstanceId, [FromBody] GarrisonRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response, world) = await _garrisons.SetAsync(gameInstanceId, userId, request);

        if (!outcome.Succeeded)
        {
            _sessionLog.Log("GARRISON-DENY",
                $"user={userId} site={request.SiteId} {outcome.Error}: {outcome.Message}");
            return ToError(outcome);
        }

        if (world == null || response == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "The garrison resolved without producing world state." });
        }

        await PersistAndBroadcastAsync(gameInstanceId, world);
        return Ok(response);
    }

    /// <summary>Buys this player's prisoners back out of the site holding them.</summary>
    [HttpPost("ransom")]
    public async Task<ActionResult<RansomResponse>> Ransom(Guid gameInstanceId, [FromBody] RansomRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response, world) = await _garrisons.RansomAsync(gameInstanceId, userId, request);

        if (!outcome.Succeeded)
        {
            _sessionLog.Log("RANSOM-DENY",
                $"user={userId} site={request.SiteId} {outcome.Error}: {outcome.Message}");
            return ToError(outcome);
        }

        if (world == null || response == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "The ransom resolved without producing world state." });
        }

        await PersistAndBroadcastAsync(gameInstanceId, world);
        return Ok(response);
    }

    private ActionResult ToError(GarrisonOutcome outcome) => outcome.Error switch
    {
        GarrisonError.WorldNotFound or GarrisonError.SiteNotFound
            => NotFound(new { message = outcome.Message }),
        GarrisonError.ContractMismatch or GarrisonError.CannotAfford
            => Conflict(new { message = outcome.Message }),
        GarrisonError.NotYours or GarrisonError.NotYourPrisoners
            => Forbid(),
        _ => BadRequest(new { message = outcome.Message })
    };

    /// <summary>
    /// Writes the server-mutated world back and tells everyone in the instance. A garrison is visible
    /// to an attacker sizing the region up, so it has to reach them the same way a raid does.
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
