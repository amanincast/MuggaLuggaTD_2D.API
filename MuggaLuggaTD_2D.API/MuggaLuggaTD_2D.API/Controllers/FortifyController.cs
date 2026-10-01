using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>Fortifying a region you hold with goods (Hiring Hall phase 3).</summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/fortify")]
[Authorize]
public class FortifyController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly FortifyService _fortify;
    private readonly SeasonScoreService _seasons;
    private readonly ISessionLog _sessionLog;

    public FortifyController(ApplicationDbContext context, FortifyService fortify, SeasonScoreService seasons, ISessionLog sessionLog)
    {
        _context = context;
        _fortify = fortify;
        _seasons = seasons;
        _sessionLog = sessionLog;
    }

    [HttpPost]
    public async Task<ActionResult<FortifyResponse>> Begin(Guid gameInstanceId, [FromBody] FortifyRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await _context.GameInstances.AnyAsync(g => g.Id == gameInstanceId &&
                (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId))))
            return Forbid();

        if (request.SharedContractVersion != SharedContract.Version)
            return Conflict(new { error = "Shared contract version mismatch.", expected = SharedContract.Version, received = request.SharedContractVersion });

        // Building after the bell would raise walls on a map that is about to be replaced.
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);

        var outcome = await _fortify.BeginAsync(gameInstanceId, userId, request.RegionId);
        if (outcome.Succeeded) return Ok(outcome.Response);

        _sessionLog.Log("FORTIFY-DENY", $"user={userId} region={request.RegionId} {outcome.Error}: {outcome.Message}");
        return outcome.Error is FortifyError.WorldNotFound or FortifyError.RegionNotFound
            ? NotFound(new { error = outcome.Message })
            : Conflict(new { error = outcome.Message });
    }
}
