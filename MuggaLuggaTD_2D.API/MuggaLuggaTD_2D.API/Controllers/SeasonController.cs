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
    private readonly IWebHostEnvironment _environment;

    public SeasonController(ApplicationDbContext context, SeasonScoreService seasons, IWebHostEnvironment environment)
    {
        _context = context;
        _seasons = seasons;
        _environment = environment;
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

    /// <summary>
    /// The season-end page to show on the player's return (season-end.md §5): their latest closed season
    /// they have not seen yet. 204 when there is none, which is nearly always.
    /// </summary>
    [HttpGet("ended")]
    public async Task<ActionResult<SeasonEndedResponse>> Ended(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        // Arriving after the bell is one of the ways a season gets closed.
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);

        var ended = await _seasons.EndedForAsync(gameInstanceId, userId, unseenOnly: true);
        return ended == null ? NoContent() : Ok(ended);
    }

    /// <summary>
    /// A season's end page again, seen or not (LAST SEASON on the standings). 204 when the player had no
    /// finish in it, as for <see cref="Ended"/>: the client asks without knowing.
    /// </summary>
    [HttpGet("ended/{seasonNumber:int}")]
    public async Task<ActionResult<SeasonEndedResponse>> EndedSeason(Guid gameInstanceId, int seasonNumber)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var ended = await _seasons.EndedForAsync(gameInstanceId, userId, unseenOnly: false, seasonNumber);
        return ended == null ? NoContent() : Ok(ended);
    }

    /// <summary>The player has seen the page; it is not shown on their return again.</summary>
    [HttpPost("results/{seasonNumber:int}/seen")]
    public async Task<IActionResult> Seen(Guid gameInstanceId, int seasonNumber)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        return await _seasons.MarkSeenAsync(gameInstanceId, userId, seasonNumber)
            ? NoContent()
            : NotFound(new { message = "You have no finish in that season." });
    }

    /// <summary>Opens the chest a finish earned. The server rolls and grants the piece.</summary>
    [HttpPost("results/{seasonNumber:int}/chest")]
    public async Task<ActionResult<SeasonChestResponse>> OpenChest(Guid gameInstanceId, int seasonNumber)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (error, chest) = await _seasons.OpenChestAsync(gameInstanceId, userId, seasonNumber);
        return error switch
        {
            SeasonScoreService.ChestError.None => Ok(chest),
            SeasonScoreService.ChestError.NoSuchSeason => NotFound(new { message = "You have no finish in that season." }),
            SeasonScoreService.ChestError.NoChest => BadRequest(new { message = "That finish earned no chest." }),
            SeasonScoreService.ChestError.AlreadyOpened => Conflict(new { message = "That chest is already open." }),
            _ => StatusCode(500, new { message = "The chest could not be opened." })
        };
    }

    /// <summary>
    /// Debug, Development only: rings the season's bell now, and closes it exactly as a real bell does. The
    /// Combat Debug window's "End the season now".
    /// </summary>
    [HttpPost("debug/bell")]
    public async Task<IActionResult> DebugBell(Guid gameInstanceId)
    {
        if (!_environment.IsDevelopment()) return NotFound();

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        bool closed = await _seasons.DebugRingBellAsync(gameInstanceId);
        return Ok(new { closed });
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
