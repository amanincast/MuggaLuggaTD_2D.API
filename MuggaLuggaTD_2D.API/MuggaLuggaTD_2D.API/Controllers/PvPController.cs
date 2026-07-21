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
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// Server-authoritative passive PvP. The client asks to attack a location; the server decides the
/// result, applies it to the shared world, and broadcasts the change.
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/pvp")]
[Authorize]
public class PvPController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHubContext<GameHub> _hubContext;
    private readonly WorldPvPService _pvp;
    private readonly ISessionLog _sessionLog;

    public PvPController(ApplicationDbContext context, IHubContext<GameHub> hubContext, WorldPvPService pvp, ISessionLog sessionLog)
    {
        _context = context;
        _hubContext = hubContext;
        _pvp = pvp;
        _sessionLog = sessionLog;
    }

    [HttpPost("attack")]
    public async Task<ActionResult<PvPAttackResponse>> Attack(Guid gameInstanceId, [FromBody] PvPAttackRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        // Attacking requires membership of the instance, not ownership of it.
        if (!await HasAccessToGameInstance(gameInstanceId, userId))
            return Forbid();

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        var displayName = user?.DisplayName ?? user?.UserName;

        var (outcome, updatedWorld) = await _pvp.ResolveAsync(gameInstanceId, userId, displayName, request);

        if (!outcome.Succeeded)
        {
            return outcome.Error switch
            {
                PvPAttackError.WorldNotFound or PvPAttackError.LocationNotFound
                    => NotFound(new { message = outcome.Message }),
                PvPAttackError.ContractMismatch
                    => Conflict(new { message = outcome.Message }),
                _ => BadRequest(new { message = outcome.Message })
            };
        }

        // A successful outcome always carries both; treat anything else as a server fault rather than
        // reporting a win we then fail to persist.
        if (updatedWorld == null || outcome.Response == null)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "PvP resolved without producing world state." });
        }

        await PersistAndBroadcastAsync(gameInstanceId, updatedWorld);

        var r = outcome.Response;
        _sessionLog.Log("PVP-ATTACK",
            $"user={userId} loc={r.LocationId} {(r.AttackerWins ? "WIN" : "LOSS")} " +
            $"atk={r.AttackerPower:F0} def={r.DefenderPower:F0} roll={r.D20Roll}{(r.Modifier >= 0 ? "+" : "")}{r.Modifier}={r.Total} " +
            $"{r.ConquestOutcome ?? r.DefeatOutcome}");

        return Ok(outcome.Response);
    }

    /// <summary>
    /// Writes the server-mutated world back and notifies everyone in the instance, so defenders see
    /// the loss without polling. Mirrors what WorldViewGameDataController does on a client save.
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
