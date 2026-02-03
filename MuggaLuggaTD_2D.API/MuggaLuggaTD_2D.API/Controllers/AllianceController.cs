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
[Route("api/gameinstance/{gameInstanceId:guid}/alliance")]
[Authorize]
public class AllianceController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public AllianceController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<AllianceListResponse>> GetAllAlliances(Guid gameInstanceId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!await HasAccessToGameInstance(gameInstanceId, userId))
        {
            return Forbid();
        }

        var alliances = await _context.Alliances
            .Where(a => a.GameInstanceId == gameInstanceId)
            .OrderBy(a => a.Name)
            .Select(a => new AllianceSummary(a.Id, a.GameInstanceId, a.Name, a.CreatedAt, a.UpdatedAt))
            .ToListAsync();

        return Ok(new AllianceListResponse(alliances));
    }

    [HttpGet("{allianceId:guid}")]
    public async Task<ActionResult<AllianceResponse>> GetAlliance(Guid gameInstanceId, Guid allianceId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!await HasAccessToGameInstance(gameInstanceId, userId))
        {
            return Forbid();
        }

        var alliance = await _context.Alliances
            .FirstOrDefaultAsync(a => a.GameInstanceId == gameInstanceId && a.Id == allianceId);

        if (alliance == null)
        {
            return NotFound(new { message = "Alliance not found" });
        }

        var gameData = JsonSerializer.Deserialize<object>(alliance.GameData) ?? new { };
        return Ok(new AllianceResponse(
            alliance.Id,
            alliance.GameInstanceId,
            alliance.Name,
            gameData,
            alliance.CreatedAt,
            alliance.UpdatedAt));
    }

    [HttpPost]
    public async Task<ActionResult<AllianceResponse>> CreateAlliance(
        Guid gameInstanceId,
        [FromBody] CreateAllianceRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!await HasAccessToGameInstance(gameInstanceId, userId))
        {
            return Forbid();
        }

        // Check if alliance name already exists in this game instance
        var existingAlliance = await _context.Alliances
            .AnyAsync(a => a.GameInstanceId == gameInstanceId && a.Name == request.Name);

        if (existingAlliance)
        {
            return Conflict(new { message = "An alliance with this name already exists" });
        }

        var gameDataJson = request.GameData != null
            ? JsonSerializer.Serialize(request.GameData)
            : "{}";

        var alliance = new Alliance
        {
            GameInstanceId = gameInstanceId,
            Name = request.Name,
            GameData = gameDataJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Alliances.Add(alliance);
        await _context.SaveChangesAsync();

        var responseGameData = request.GameData ?? new { };
        return CreatedAtAction(
            nameof(GetAlliance),
            new { gameInstanceId, allianceId = alliance.Id },
            new AllianceResponse(
                alliance.Id,
                alliance.GameInstanceId,
                alliance.Name,
                responseGameData,
                alliance.CreatedAt,
                alliance.UpdatedAt));
    }

    [HttpPut("{allianceId:guid}")]
    public async Task<ActionResult<AllianceResponse>> UpdateAlliance(
        Guid gameInstanceId,
        Guid allianceId,
        [FromBody] UpdateAllianceRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!await HasAccessToGameInstance(gameInstanceId, userId))
        {
            return Forbid();
        }

        var alliance = await _context.Alliances
            .FirstOrDefaultAsync(a => a.GameInstanceId == gameInstanceId && a.Id == allianceId);

        if (alliance == null)
        {
            return NotFound(new { message = "Alliance not found" });
        }

        if (request.Name != null)
        {
            // Check if the new name conflicts with existing alliance
            var nameConflict = await _context.Alliances
                .AnyAsync(a => a.GameInstanceId == gameInstanceId && a.Name == request.Name && a.Id != allianceId);

            if (nameConflict)
            {
                return Conflict(new { message = "An alliance with this name already exists" });
            }

            alliance.Name = request.Name;
        }

        if (request.GameData != null)
        {
            alliance.GameData = JsonSerializer.Serialize(request.GameData);
        }

        alliance.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var gameData = JsonSerializer.Deserialize<object>(alliance.GameData) ?? new { };
        return Ok(new AllianceResponse(
            alliance.Id,
            alliance.GameInstanceId,
            alliance.Name,
            gameData,
            alliance.CreatedAt,
            alliance.UpdatedAt));
    }

    [HttpDelete("{allianceId:guid}")]
    public async Task<IActionResult> DeleteAlliance(Guid gameInstanceId, Guid allianceId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        // Only owner can delete alliances
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

        var alliance = await _context.Alliances
            .FirstOrDefaultAsync(a => a.GameInstanceId == gameInstanceId && a.Id == allianceId);

        if (alliance == null)
        {
            return NotFound(new { message = "Alliance not found" });
        }

        _context.Alliances.Remove(alliance);
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
