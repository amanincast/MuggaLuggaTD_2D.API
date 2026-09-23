using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// The Tavern: the six recruits a player may take on, and taking one on.
///
/// <para>Design doc 05 §4. The board is rolled and held by the server, restocked by clearing a
/// dungeon, and paid for out of the material wallet. The client draws cards and names a slot; it
/// never sends a recruit, and the character id it ends up with is the one chosen here.</para>
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/tavern")]
[Authorize]
public class TavernController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly TavernService _tavern;
    private readonly MaterialWalletService _wallet;

    public TavernController(
        ApplicationDbContext context, TavernService tavern, MaterialWalletService wallet)
    {
        _context = context;
        _tavern = tavern;
        _wallet = wallet;
    }

    [HttpGet]
    public async Task<ActionResult<TavernBoardResponse>> Read(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        return Ok(await BoardResponseAsync(gameInstanceId, userId));
    }

    [HttpPost("hire")]
    public async Task<ActionResult<TavernHireResponse>> Hire(
        Guid gameInstanceId, [FromBody] TavernHireRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        if (request.SharedContractVersion != SharedContract.Version)
        {
            return Conflict(new
            {
                error = "Shared contract version mismatch.",
                expected = SharedContract.Version,
                received = request.SharedContractVersion
            });
        }

        var (outcome, hired) = await _tavern.HireAsync(gameInstanceId, userId, request.Slot);

        if (!outcome.Succeeded || hired == null)
        {
            return outcome.Error switch
            {
                TavernError.NoSuchSlot => NotFound(new { error = outcome.Message }),
                TavernError.AlreadyHired => Conflict(new { error = outcome.Message }),
                TavernError.RosterFull => Conflict(new { error = outcome.Message }),
                TavernError.CannotAfford => Conflict(new { error = outcome.Message }),
                _ => BadRequest(new { error = outcome.Message })
            };
        }

        var materials = await _wallet.ReadAsync(gameInstanceId, userId);

        return Ok(new TavernHireResponse(
            ToDto(hired),
            await BoardResponseAsync(gameInstanceId, userId),
            materials.Select(m => new MaterialBalance(m.MaterialName, m.Quantity)).ToList()));
    }

    private async Task<TavernBoardResponse> BoardResponseAsync(Guid gameInstanceId, string userId)
    {
        var board = await _tavern.ReadBoardAsync(gameInstanceId, userId);
        var roster = await _tavern.ReadHiredAsync(gameInstanceId, userId);

        return new TavernBoardResponse(
            board.Select(ToCard).ToList(),
            board.Count > 0 ? board[0].RolledAt : DateTime.UtcNow,
            roster.Count,
            TavernRules.RosterCap);
    }

    private static TavernRecruitCard ToCard(TavernRecruit recruit)
        => new(
            recruit.Slot,
            recruit.Name,
            recruit.Sheet,
            recruit.CharacterClass,
            recruit.SignatureId,
            recruit.Affinity,
            recruit.Rarity,
            TavernRules.HireCost(recruit.Rarity)
                .Select(c => new MaterialBalance(c.MaterialName, c.Quantity)).ToList(),
            recruit.HiredAt != null);

    private static HiredCharacterDto ToDto(HiredCharacter hired)
        => new(
            hired.CharacterId,
            hired.Name,
            hired.Sheet,
            hired.CharacterClass,
            hired.SignatureId,
            hired.Affinity,
            hired.Rarity,
            hired.HiredAt);

    private async Task<bool> HasAccessToGameInstance(Guid gameInstanceId, string userId)
        => await _context.GameInstances.AnyAsync(g => g.Id == gameInstanceId &&
            (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
}
