using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>Quests (<c>docs/design/quests.md</c>): the board, taking, abandoning and handing in.</summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/quests")]
[Authorize]
public class QuestController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly QuestService _quests;
    private readonly SeasonScoreService _seasons;
    private readonly IWebHostEnvironment _environment;

    public QuestController(ApplicationDbContext context, QuestService quests, SeasonScoreService seasons, IWebHostEnvironment environment)
    {
        _context = context;
        _quests = quests;
        _seasons = seasons;
        _environment = environment;
    }

    [HttpGet]
    public async Task<ActionResult<QuestBoardResponse>> Board(Guid gameInstanceId)
    {
        var userId = UserId();
        if (userId == null) return Unauthorized();
        if (!await HasAccess(gameInstanceId, userId)) return Forbid();
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);
        return Answer(await _quests.BoardAsync(gameInstanceId, userId));
    }

    [HttpPost("accept")]
    public async Task<ActionResult<QuestBoardResponse>> Accept(Guid gameInstanceId, [FromBody] QuestAcceptRequest request)
    {
        var userId = UserId();
        if (userId == null) return Unauthorized();
        if (!await HasAccess(gameInstanceId, userId)) return Forbid();
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);
        return Answer(await _quests.AcceptAsync(gameInstanceId, userId, request.OfferId));
    }

    [HttpPost("{questId:guid}/abandon")]
    public async Task<ActionResult<QuestBoardResponse>> Abandon(Guid gameInstanceId, Guid questId)
    {
        var userId = UserId();
        if (userId == null) return Unauthorized();
        if (!await HasAccess(gameInstanceId, userId)) return Forbid();
        return Answer(await _quests.AbandonAsync(gameInstanceId, userId, questId));
    }

    [HttpPost("{questId:guid}/handin")]
    public async Task<ActionResult<QuestHandInResponse>> HandIn(Guid gameInstanceId, Guid questId)
    {
        var userId = UserId();
        if (userId == null) return Unauthorized();
        if (!await HasAccess(gameInstanceId, userId)) return Forbid();
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);
        return Answer(await _quests.HandInAsync(gameInstanceId, userId, questId));
    }

    [HttpPost("seen")]
    public async Task<ActionResult<QuestBoardResponse>> Seen(Guid gameInstanceId, [FromBody] QuestSeenRequest request)
    {
        var userId = UserId();
        if (userId == null) return Unauthorized();
        if (!await HasAccess(gameInstanceId, userId)) return Forbid();
        return Answer(await _quests.SeenAsync(gameInstanceId, userId, request.GiverIds));
    }

    /// <summary>Debug, Development only: a fresh set of offers now. The Combat Debug window's "New quests".</summary>
    [HttpPost("debug/fresh")]
    public async Task<IActionResult> DebugFresh(Guid gameInstanceId)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = UserId();
        if (userId == null) return Unauthorized();
        if (!await HasAccess(gameInstanceId, userId)) return Forbid();
        await _quests.DebugFreshSetAsync(gameInstanceId, userId);
        return Ok(new { fresh = true });
    }

    /// <summary>Debug, Development only: every quest taken is done. The Combat Debug window's "Finish my quests".</summary>
    [HttpPost("debug/finish")]
    public async Task<IActionResult> DebugFinish(Guid gameInstanceId)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = UserId();
        if (userId == null) return Unauthorized();
        if (!await HasAccess(gameInstanceId, userId)) return Forbid();
        return Ok(new { finished = await _quests.DebugFinishAsync(gameInstanceId, userId) });
    }

    private ActionResult<T> Answer<T>((QuestOutcome Outcome, T? Value) result) where T : class
    {
        var (outcome, value) = result;
        return outcome.Error switch
        {
            QuestError.None => Ok(value),
            QuestError.WorldNotFound or QuestError.QuestNotFound => NotFound(new { error = outcome.Message }),
            _ => Conflict(new { error = outcome.Message })
        };
    }

    private string? UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private async Task<bool> HasAccess(Guid gameInstanceId, string userId)
        => await _context.GameInstances.AnyAsync(g => g.Id == gameInstanceId &&
            (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
}
