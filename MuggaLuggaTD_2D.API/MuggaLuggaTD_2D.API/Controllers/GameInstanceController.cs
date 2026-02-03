using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class GameInstanceController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public GameInstanceController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<GameInstanceListResponse>> GetAllGameInstances()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        // Get game instances owned by the user OR where user has player data
        var gameInstances = await _context.GameInstances
            .Where(g => g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId))
            .OrderByDescending(g => g.UpdatedAt)
            .Select(g => new GameInstanceSummary(g.Id, g.Name, g.OwnerId, g.CreatedAt, g.UpdatedAt))
            .ToListAsync();

        return Ok(new GameInstanceListResponse(gameInstances));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<GameInstanceResponse>> GetGameInstance(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var gameInstance = await _context.GameInstances
            .FirstOrDefaultAsync(g => g.Id == id);

        if (gameInstance == null)
        {
            return NotFound(new { message = "Game instance not found" });
        }

        // Check access: owner or player
        if (!await HasAccessToGameInstance(id, userId))
        {
            return Forbid();
        }

        return Ok(new GameInstanceResponse(
            gameInstance.Id,
            gameInstance.Name,
            gameInstance.OwnerId,
            gameInstance.CreatedAt,
            gameInstance.UpdatedAt));
    }

    [HttpPost]
    public async Task<ActionResult<GameInstanceResponse>> CreateGameInstance([FromBody] CreateGameInstanceRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var gameInstance = new GameInstance
        {
            Name = request.Name,
            OwnerId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.GameInstances.Add(gameInstance);
        await _context.SaveChangesAsync();

        return CreatedAtAction(
            nameof(GetGameInstance),
            new { id = gameInstance.Id },
            new GameInstanceResponse(
                gameInstance.Id,
                gameInstance.Name,
                gameInstance.OwnerId,
                gameInstance.CreatedAt,
                gameInstance.UpdatedAt));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<GameInstanceResponse>> UpdateGameInstance(Guid id, [FromBody] UpdateGameInstanceRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var gameInstance = await _context.GameInstances
            .FirstOrDefaultAsync(g => g.Id == id);

        if (gameInstance == null)
        {
            return NotFound(new { message = "Game instance not found" });
        }

        // Only owner can update
        if (gameInstance.OwnerId != userId)
        {
            return Forbid();
        }

        gameInstance.Name = request.Name;
        gameInstance.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(new GameInstanceResponse(
            gameInstance.Id,
            gameInstance.Name,
            gameInstance.OwnerId,
            gameInstance.CreatedAt,
            gameInstance.UpdatedAt));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteGameInstance(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var gameInstance = await _context.GameInstances
            .FirstOrDefaultAsync(g => g.Id == id);

        if (gameInstance == null)
        {
            return NotFound(new { message = "Game instance not found" });
        }

        // Only owner can delete
        if (gameInstance.OwnerId != userId)
        {
            return Forbid();
        }

        _context.GameInstances.Remove(gameInstance);
        await _context.SaveChangesAsync();

        return NoContent();
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
