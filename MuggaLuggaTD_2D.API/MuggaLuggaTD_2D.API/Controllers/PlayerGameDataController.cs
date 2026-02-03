using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Controllers;

[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/playerdata")]
[Authorize]
public class PlayerGameDataController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public PlayerGameDataController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<PlayerGameDataListResponse>> GetAllPlayerData(Guid gameInstanceId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        // Only owner can see all player data
        var gameInstance = await _context.GameInstances
            .FirstOrDefaultAsync(g => g.Id == gameInstanceId);

        if (gameInstance == null)
        {
            return NotFound(new { message = "Game instance not found" });
        }

        if (gameInstance.OwnerId != userId)
        {
            return Forbid();
        }

        var playerData = await _context.PlayerGameData
            .Where(p => p.GameInstanceId == gameInstanceId)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new PlayerGameDataSummary(p.Id, p.GameInstanceId, p.UserId, p.CreatedAt, p.UpdatedAt))
            .ToListAsync();

        return Ok(new PlayerGameDataListResponse(playerData));
    }

    [HttpGet("me")]
    public async Task<ActionResult<PlayerGameDataResponse>> GetMyPlayerData(Guid gameInstanceId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var gameInstance = await _context.GameInstances
            .FirstOrDefaultAsync(g => g.Id == gameInstanceId);

        if (gameInstance == null)
        {
            return NotFound(new { message = "Game instance not found" });
        }

        var playerData = await _context.PlayerGameData
            .FirstOrDefaultAsync(p => p.GameInstanceId == gameInstanceId && p.UserId == userId);

        if (playerData == null)
        {
            return NotFound(new { message = "Player data not found" });
        }

        var gameData = JsonSerializer.Deserialize<object>(playerData.GameData) ?? new { };
        return Ok(new PlayerGameDataResponse(
            playerData.Id,
            playerData.GameInstanceId,
            playerData.UserId,
            gameData,
            playerData.CreatedAt,
            playerData.UpdatedAt));
    }

    [HttpPost("me")]
    public async Task<ActionResult<PlayerGameDataResponse>> SaveMyPlayerData(
        Guid gameInstanceId,
        [FromBody] SavePlayerGameDataRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var gameInstance = await _context.GameInstances
            .FirstOrDefaultAsync(g => g.Id == gameInstanceId);

        if (gameInstance == null)
        {
            return NotFound(new { message = "Game instance not found" });
        }

        var gameDataJson = JsonSerializer.Serialize(request.GameData);
        var existingData = await _context.PlayerGameData
            .FirstOrDefaultAsync(p => p.GameInstanceId == gameInstanceId && p.UserId == userId);

        if (existingData != null)
        {
            existingData.GameData = gameDataJson;
            existingData.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new PlayerGameDataResponse(
                existingData.Id,
                existingData.GameInstanceId,
                existingData.UserId,
                request.GameData,
                existingData.CreatedAt,
                existingData.UpdatedAt));
        }

        var playerData = new PlayerGameData
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            GameData = gameDataJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.PlayerGameData.Add(playerData);
        await _context.SaveChangesAsync();

        return CreatedAtAction(
            nameof(GetMyPlayerData),
            new { gameInstanceId },
            new PlayerGameDataResponse(
                playerData.Id,
                playerData.GameInstanceId,
                playerData.UserId,
                request.GameData,
                playerData.CreatedAt,
                playerData.UpdatedAt));
    }

    [HttpDelete("me")]
    public async Task<IActionResult> DeleteMyPlayerData(Guid gameInstanceId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var playerData = await _context.PlayerGameData
            .FirstOrDefaultAsync(p => p.GameInstanceId == gameInstanceId && p.UserId == userId);

        if (playerData == null)
        {
            return NotFound(new { message = "Player data not found" });
        }

        _context.PlayerGameData.Remove(playerData);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    private string? GetUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
