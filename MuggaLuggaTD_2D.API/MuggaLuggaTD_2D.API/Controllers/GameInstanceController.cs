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
            .Select(g => new GameInstanceSummary(g.Id, g.Name, g.OwnerId, g.AccessType, g.Capacity, g.CreatedAt, g.UpdatedAt))
            .ToListAsync();

        return Ok(new GameInstanceListResponse(gameInstances));
    }

    [HttpGet("browse")]
    public async Task<ActionResult<GameInstanceListResponse>> BrowseGameInstances()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var gameInstances = await _context.GameInstances
            .Where(g => g.OwnerId != userId
                && !g.PlayerGameData.Any(p => p.UserId == userId)
                && !_context.UserBlocks.Any(b =>
                    (b.BlockerId == userId && b.BlockedUserId == g.OwnerId) ||
                    (b.BlockerId == g.OwnerId && b.BlockedUserId == userId))
                && (g.AccessType == GameInstanceAccessType.Public
                    || (g.AccessType == GameInstanceAccessType.FriendsAndInviteOnly
                        && _context.Friendships.Any(f =>
                            f.Status == FriendshipStatus.Accepted
                            && ((f.RequesterId == userId && f.AddresseeId == g.OwnerId)
                                || (f.RequesterId == g.OwnerId && f.AddresseeId == userId))))))
            .OrderByDescending(g => g.UpdatedAt)
            .Select(g => new GameInstanceSummary(g.Id, g.Name, g.OwnerId, g.AccessType, g.Capacity, g.CreatedAt, g.UpdatedAt))
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

        if (!await HasAccessToGameInstance(gameInstance, userId))
        {
            return NotFound(new { message = "Game instance not found" });
        }

        return Ok(new GameInstanceResponse(
            gameInstance.Id,
            gameInstance.Name,
            gameInstance.OwnerId,
            gameInstance.AccessType,
            gameInstance.Capacity,
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
            AccessType = request.AccessType,
            Capacity = request.Capacity,
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
                gameInstance.AccessType,
                gameInstance.Capacity,
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
        gameInstance.AccessType = request.AccessType;
        gameInstance.Capacity = request.Capacity;
        gameInstance.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(new GameInstanceResponse(
            gameInstance.Id,
            gameInstance.Name,
            gameInstance.OwnerId,
            gameInstance.AccessType,
            gameInstance.Capacity,
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

    private async Task<bool> HasAccessToGameInstance(GameInstance gameInstance, string userId)
    {
        // Block check: if a block exists in either direction, deny access
        var blockExists = await _context.UserBlocks.AnyAsync(b =>
            (b.BlockerId == userId && b.BlockedUserId == gameInstance.OwnerId) ||
            (b.BlockerId == gameInstance.OwnerId && b.BlockedUserId == userId));

        if (blockExists)
            return false;

        // Owner or existing player always has access
        var isOwnerOrPlayer = gameInstance.OwnerId == userId
            || await _context.PlayerGameData.AnyAsync(p => p.GameInstanceId == gameInstance.Id && p.UserId == userId);

        if (isOwnerOrPlayer)
            return true;

        // Access based on AccessType
        return gameInstance.AccessType switch
        {
            GameInstanceAccessType.Public => true,
            GameInstanceAccessType.FriendsAndInviteOnly => await _context.Friendships.AnyAsync(f =>
                f.Status == FriendshipStatus.Accepted
                && ((f.RequesterId == userId && f.AddresseeId == gameInstance.OwnerId)
                    || (f.RequesterId == gameInstance.OwnerId && f.AddresseeId == userId))),
            GameInstanceAccessType.InviteOnly => false,
            _ => false
        };
    }
}
