using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/playerdata")]
[Authorize]
public class PlayerGameDataController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly PlayerSaveValidator _saveValidator;
    private readonly ISessionLog _sessionLog;

    private readonly TavernService _tavern;
    private readonly ItemLedgerService _itemLedger;

    public PlayerGameDataController(
        ApplicationDbContext context, PlayerSaveValidator saveValidator, TavernService tavern, ISessionLog sessionLog,
        ItemLedgerService itemLedger)
    {
        _itemLedger = itemLedger;
        _context = context;
        _saveValidator = saveValidator;
        _tavern = tavern;
        _sessionLog = sessionLog;
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

        // A member who has joined but not yet saved has no save: answering 404 is what starts them.
        if (playerData == null || playerData.GameData == RealmMembershipService.NoSaveYet)
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

        var existingData = await _context.PlayerGameData
            .FirstOrDefaultAsync(p => p.GameInstanceId == gameInstanceId && p.UserId == userId);

        // A save no longer makes a member (Hardening 2): POST gameinstance/{id}/join does, after
        // checking the realm's access type and capacity. Only the owner may save without having joined.
        if (existingData == null && gameInstance.OwnerId != userId)
        {
            _sessionLog.Log("SAVE-DENY", $"user={userId} instance={gameInstanceId} not a member");
            return Forbid();
        }

        // Merge rather than replace: the roster save and fog discovery each send only their own
        // fields, and replacing the blob let either one erase the other (see PlayerDataMerger).
        var saveNode = PlayerDataMerger.Merge(existingData?.GameData,
            JsonSerializer.SerializeToNode(request.GameData));

        // A save carries no ability upgrades: a run's picks end with the run. Any it holds is an old
        // build or a forgery, and upgrades raise the PvP power priced from this roster, so they go.
        // It runs on the merged document, so it always sees the full roster.
        var validation = _saveValidator.StripRunPicks(saveNode);
        if (validation.Rejected > 0)
        {
            _sessionLog.Log("SAVE-REJECT",
                $"user={userId} accepted={validation.Accepted} rejected={validation.Rejected} " +
                $"[{string.Join(", ", validation.RejectedDetails)}]");
        }
        else if (_sessionLog.Enabled)
        {
            _sessionLog.Log("SAVE", $"user={userId} upgrades ok={validation.Accepted}");
        }

        // Materials are server-owned (they buy characters at the Tavern), so a save cannot carry them.
        // Old clients still write them; dropping them costs those clients nothing, because the wallet
        // is what the game reads.
        var materials = _saveValidator.StripMaterials(saveNode);
        if (materials.Changed)
        {
            _sessionLog.Log("SAVE-MATERIALS",
                $"user={userId} dropped={materials.Removed} stack(s) quantity={materials.TotalQuantity}");
        }

        // Equipment is held to the ledger: an item is what the server granted, or it is not kept.
        var items = await _itemLedger.ReconcileSaveAsync(gameInstanceId, userId, saveNode);
        if (items.Changed)
        {
            _sessionLog.Log("SAVE-ITEMS",
                $"user={userId} kept={items.Kept} corrected={items.Corrected} dropped={items.Dropped} " +
                $"[{string.Join(", ", items.Details.Take(20))}]");
        }

        // Level is worth power, and the experience curve now has a cap to hold it to.
        var levels = _saveValidator.ClampLevels(saveNode);
        if (levels.Changed)
        {
            _sessionLog.Log("SAVE-LEVEL",
                $"user={userId} clamped={levels.Clamped} highest={levels.HighestSeen} " +
                $"max={MuggaLuggaTD.Shared.Gameplay.CharacterProgression.MaxLevel}");
        }

        // The identity roll drives the kit and PvP power is computed from it, so a roll the server
        // did not hand out is a character the player awarded themselves. Every roster is written back
        // to what the hire records say - design doc 05 §5.2.
        var roster = _saveValidator.ReconcileRoster(
            saveNode, await _tavern.ReadHiredAsync(gameInstanceId, userId));

        if (roster.Changed)
        {
            _sessionLog.Log("SAVE-ROSTER",
                $"user={userId} corrected={roster.Corrected} stripped={roster.Stripped} " +
                $"[{string.Join(", ", roster.Details)}]");
        }

        // Talents are the Trainer's to write (TalentService): each character keeps what the stored
        // save held, whatever this one claims. They are worth power, like the roll above.
        int talents = TalentService.ReconcileTalents(saveNode, existingData?.GameData);
        if (talents > 0)
            _sessionLog.Log("SAVE-TALENTS", $"user={userId} corrected={talents}");

        var gameDataJson = saveNode?.ToJsonString() ?? JsonSerializer.Serialize(request.GameData);

        if (existingData != null)
        {
            existingData.GameData = gameDataJson;
            existingData.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new PlayerGameDataResponse(
                existingData.Id,
                existingData.GameInstanceId,
                existingData.UserId,
                (object?)saveNode ?? request.GameData,
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
                (object?)saveNode ?? request.GameData,
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
