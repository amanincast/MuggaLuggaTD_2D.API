using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Hubs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Controllers;

[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/worldview")]
[Authorize]
public class WorldViewGameDataController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHubContext<GameHub> _hubContext;

    public WorldViewGameDataController(ApplicationDbContext context, IHubContext<GameHub> hubContext)
    {
        _context = context;
        _hubContext = hubContext;
    }

    [HttpGet]
    public async Task<ActionResult<WorldViewGameDataResponse>> GetWorldViewGameData(Guid gameInstanceId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!await HasAccessToGameInstance(gameInstanceId, userId))
        {
            return Forbid();
        }

        var worldData = await _context.WorldViewGameData
            .FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);

        if (worldData == null)
        {
            return NotFound(new { message = "World view data not found" });
        }

        var gameData = JsonSerializer.Deserialize<object>(worldData.GameData) ?? new { };
        return Ok(new WorldViewGameDataResponse(
            worldData.Id,
            worldData.GameInstanceId,
            gameData,
            worldData.CreatedAt,
            worldData.UpdatedAt));
    }

    [HttpPost]
    public async Task<ActionResult<WorldViewGameDataResponse>> SaveWorldViewGameData(
        Guid gameInstanceId,
        [FromBody] SaveWorldViewGameDataRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var gameInstance = await _context.GameInstances
            .FirstOrDefaultAsync(g => g.Id == gameInstanceId);

        if (gameInstance == null)
        {
            return NotFound(new { message = "Game instance not found" });
        }

        // Any member of the instance may write the shared world, not just its owner. Restricting
        // this to the owner silently dropped every non-owner's conquest: their capture applied
        // locally, 403'd here, and was overwritten on the next sync from someone else.
        //
        // This is still a last-writer-wins blob write. Outcomes that must not be forgeable are not
        // resolved here at all — PvP goes through PvPController, which decides the result itself.
        if (!await HasAccessToGameInstance(gameInstanceId, userId))
        {
            return Forbid();
        }

        var gameDataJson = JsonSerializer.Serialize(request.GameData);
        var existingData = await _context.WorldViewGameData
            .FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);

        if (existingData != null)
        {
            existingData.GameData = gameDataJson;
            existingData.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _hubContext.Clients.Group(gameInstanceId.ToString())
                .SendAsync("WorldViewGameDataUpdated", new WorldViewGameDataUpdated(
                    gameInstanceId, request.GameData, existingData.UpdatedAt));

            return Ok(new WorldViewGameDataResponse(
                existingData.Id,
                existingData.GameInstanceId,
                request.GameData,
                existingData.CreatedAt,
                existingData.UpdatedAt));
        }

        var worldData = new WorldViewGameData
        {
            GameInstanceId = gameInstanceId,
            GameData = gameDataJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.WorldViewGameData.Add(worldData);
        await _context.SaveChangesAsync();

        await _hubContext.Clients.Group(gameInstanceId.ToString())
            .SendAsync("WorldViewGameDataUpdated", new WorldViewGameDataUpdated(
                gameInstanceId, request.GameData, worldData.UpdatedAt));

        return CreatedAtAction(
            nameof(GetWorldViewGameData),
            new { gameInstanceId },
            new WorldViewGameDataResponse(
                worldData.Id,
                worldData.GameInstanceId,
                request.GameData,
                worldData.CreatedAt,
                worldData.UpdatedAt));
    }

    private string? GetUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    private async Task<bool> HasAccessToGameInstance(Guid gameInstanceId, string userId)
    {
        return await _context.GameInstances
            .AnyAsync(g => g.Id == gameInstanceId &&
                (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
    }
}
