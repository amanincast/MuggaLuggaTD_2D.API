using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// The scoreboard: where everyone stands in the running season, and how past ones finished.
///
/// <para>Read-only. Points are only ever written by the actions that earn them, which is what keeps
/// the win condition out of a client's reach - a score cannot be posted, only played for.</para>
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/season")]
[Authorize]
public class SeasonController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly SeasonScoreService _seasons;

    public SeasonController(ApplicationDbContext context, SeasonScoreService seasons)
    {
        _context = context;
        _seasons = seasons;
    }

    /// <summary>The running table, projected to this moment.</summary>
    [HttpGet("standings")]
    public async Task<ActionResult<SeasonStandingsResponse>> Standings(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        // Looking at the board after the bell is one of the ways a season gets closed, since there
        // is no scheduler. The reply is then the new season's empty table, and the final one
        // arrives as the SeasonEnded broadcast.
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);

        return Ok(await _seasons.StandingsAsync(gameInstanceId));
    }

    /// <summary>The final table of a season that has already closed.</summary>
    [HttpGet("results/{seasonNumber:int}")]
    public async Task<ActionResult<IReadOnlyList<SeasonResultEntry>>> Results(Guid gameInstanceId, int seasonNumber)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var results = await _seasons.ResultsAsync(gameInstanceId, seasonNumber);
        if (results.Count == 0) return NotFound(new { message = "No season has closed with that number." });

        return Ok(results);
    }

    private async Task<bool> HasAccessToGameInstance(Guid gameInstanceId, string userId)
    {
        return await _context.GameInstances
            .AnyAsync(g => g.Id == gameInstanceId &&
                (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
    }
}

/// <summary>
/// What this player has won, across every realm they have played. Not scoped to an instance, which
/// is the whole point: a standing is meant to follow the player, not the world it happened in.
/// </summary>
[ApiController]
[Route("api/seasons")]
[Authorize]
public class SeasonHistoryController : ControllerBase
{
    private readonly SeasonScoreService _seasons;

    public SeasonHistoryController(SeasonScoreService seasons)
    {
        _seasons = seasons;
    }

    [HttpGet("me")]
    public async Task<ActionResult<IReadOnlyList<SeasonResultEntry>>> MyHistory()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        return Ok(await _seasons.HistoryForUserAsync(userId));
    }
}
