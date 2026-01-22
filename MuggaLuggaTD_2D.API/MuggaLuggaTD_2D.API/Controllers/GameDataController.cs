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
[Route("api/[controller]")]
[Authorize]
public class GameDataController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public GameDataController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<GameSaveListResponse>> GetAllSaves()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var saves = await _context.GameSaves
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new GameSaveSummary(s.Id, s.SlotName, s.CreatedAt, s.UpdatedAt))
            .ToListAsync();

        return Ok(new GameSaveListResponse(saves));
    }

    [HttpGet("{slotName}")]
    public async Task<ActionResult<GameSaveResponse>> GetSave(string slotName)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var save = await _context.GameSaves
            .FirstOrDefaultAsync(s => s.UserId == userId && s.SlotName == slotName);

        if (save == null)
        {
            return NotFound(new { message = $"Save slot '{slotName}' not found" });
        }

        var gameData = JsonSerializer.Deserialize<object>(save.GameData) ?? new { };
        return Ok(new GameSaveResponse(save.Id, save.SlotName, gameData, save.CreatedAt, save.UpdatedAt));
    }

    [HttpPost]
    public async Task<ActionResult<GameSaveResponse>> SaveGame([FromBody] SaveGameRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var existingSave = await _context.GameSaves
            .FirstOrDefaultAsync(s => s.UserId == userId && s.SlotName == request.SlotName);

        var gameDataJson = JsonSerializer.Serialize(request.GameData);

        if (existingSave != null)
        {
            existingSave.GameData = gameDataJson;
            existingSave.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new GameSaveResponse(
                existingSave.Id,
                existingSave.SlotName,
                request.GameData,
                existingSave.CreatedAt,
                existingSave.UpdatedAt));
        }

        var newSave = new GameSave
        {
            UserId = userId,
            SlotName = request.SlotName,
            GameData = gameDataJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.GameSaves.Add(newSave);
        await _context.SaveChangesAsync();

        return CreatedAtAction(
            nameof(GetSave),
            new { slotName = newSave.SlotName },
            new GameSaveResponse(newSave.Id, newSave.SlotName, request.GameData, newSave.CreatedAt, newSave.UpdatedAt));
    }

    [HttpDelete("{slotName}")]
    public async Task<IActionResult> DeleteSave(string slotName)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var save = await _context.GameSaves
            .FirstOrDefaultAsync(s => s.UserId == userId && s.SlotName == slotName);

        if (save == null)
        {
            return NotFound(new { message = $"Save slot '{slotName}' not found" });
        }

        _context.GameSaves.Remove(save);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    private string? GetUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
