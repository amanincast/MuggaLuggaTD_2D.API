using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// Laying siege to a rival's region. The client names a region and an army; the server decides
/// whether the siege may be declared, locks the army, and moves the siege through its windows.
///
/// <para>The assault - the part that actually moves the region - is not built yet. For now a siege
/// can be declared, mustered against, closed early by the defender, and lapses if its assault window
/// runs out.</para>
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/siege")]
[Authorize]
public class SiegeController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly WorldSiegeService _sieges;
    private readonly SeasonScoreService _seasons;
    private readonly ISessionLog _sessionLog;

    public SiegeController(
        ApplicationDbContext context, WorldSiegeService sieges, SeasonScoreService seasons, ISessionLog sessionLog)
    {
        _context = context;
        _sieges = sieges;
        _seasons = seasons;
        _sessionLog = sessionLog;
    }

    /// <summary>Every live siege in the realm.</summary>
    [HttpGet]
    public async Task<ActionResult<SiegeListResponse>> List(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        return Ok(new SiegeListResponse(await _sieges.LiveSiegesAsync(gameInstanceId, userId)));
    }

    /// <summary>Declares a siege on a rival region, locking the army that marches.</summary>
    [HttpPost]
    public async Task<ActionResult<SiegeResponse>> Declare(Guid gameInstanceId, [FromBody] SiegeDeclareRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        // A declaration after the bell would be for a season that has already been settled.
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);

        var outcome = await _sieges.DeclareAsync(gameInstanceId, userId, request);
        if (!outcome.Succeeded)
        {
            _sessionLog.Log("SIEGE-DENY",
                $"user={userId} region={request.RegionId} {outcome.Error}/{outcome.Refusal}: {outcome.Message}");
            return ToError(outcome);
        }

        return Ok(outcome.Siege);
    }

    /// <summary>The defender closes the muster early, to fight at a time they are awake for.</summary>
    [HttpPost("{siegeId:guid}/ready")]
    public async Task<ActionResult<SiegeResponse>> DeclareReady(Guid gameInstanceId, Guid siegeId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var outcome = await _sieges.DeclareReadyAsync(gameInstanceId, userId, siegeId);
        if (!outcome.Succeeded) return ToError(outcome);

        return Ok(outcome.Siege);
    }

    private ActionResult ToError(SiegeOutcome outcome) => outcome.Error switch
    {
        SiegeError.WorldNotFound or SiegeError.RegionNotFound or SiegeError.SiegeNotFound
            => NotFound(new { message = outcome.Message }),
        SiegeError.ContractMismatch or SiegeError.RegionAlreadyBesieged or SiegeError.AlreadyBesieging
            or SiegeError.WrongState
            => Conflict(new { message = outcome.Message }),
        SiegeError.NotDefender
            => StatusCode(StatusCodes.Status403Forbidden, new { message = outcome.Message }),
        // Like a raid cooldown, this is "not yet" rather than "no".
        SiegeError.OnCooldown
            => StatusCode(StatusCodes.Status429TooManyRequests, new { message = outcome.Message }),
        _ => BadRequest(new { message = outcome.Message, refusal = outcome.Refusal.ToString() })
    };

    private async Task<bool> HasAccessToGameInstance(Guid gameInstanceId, string userId)
    {
        return await _context.GameInstances
            .AnyAsync(g => g.Id == gameInstanceId &&
                (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
    }
}
