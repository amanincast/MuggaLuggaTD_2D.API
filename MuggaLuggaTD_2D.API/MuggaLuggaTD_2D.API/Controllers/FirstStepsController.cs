using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>First Steps (design 12c): the ledger's progress on this world, and its chest.</summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/first-steps")]
[Authorize]
public class FirstStepsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly FirstStepsService _steps;
    private readonly SeasonScoreService _seasons;

    public FirstStepsController(ApplicationDbContext context, FirstStepsService steps, SeasonScoreService seasons)
    {
        _context = context;
        _steps = steps;
        _seasons = seasons;
    }

    [HttpGet]
    public async Task<ActionResult<FirstStepsResponse>> Read(Guid gameInstanceId)
    {
        var userId = UserId();
        if (userId == null) return Unauthorized();
        if (!await HasAccess(gameInstanceId, userId)) return Forbid();
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);
        return Ok(await ProgressOf(gameInstanceId, userId));
    }

    /// <summary>Ticks a step the client may report: only those that grant nothing on their own.</summary>
    [HttpPost("mark")]
    public async Task<ActionResult<FirstStepsResponse>> Mark(Guid gameInstanceId, [FromBody] FirstStepsMarkRequest request)
    {
        var userId = UserId();
        if (userId == null) return Unauthorized();
        if (!await HasAccess(gameInstanceId, userId)) return Forbid();
        if (!FirstStepsRules.MayClientReport(request.Step))
            return BadRequest(new { error = "That step is recorded where it happens, not reported." });

        await _steps.RecordAsync(gameInstanceId, userId, request.Step);
        return Ok(await ProgressOf(gameInstanceId, userId));
    }

    [HttpPost("open-chest")]
    public async Task<ActionResult<FirstStepsChestResponse>> OpenChest(Guid gameInstanceId)
    {
        var userId = UserId();
        if (userId == null) return Unauthorized();
        if (!await HasAccess(gameInstanceId, userId)) return Forbid();
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);

        var (error, item) = await _steps.OpenChestAsync(gameInstanceId, userId);
        return error switch
        {
            FirstStepsError.None => Ok(new FirstStepsChestResponse(item!, (await ProgressOf(gameInstanceId, userId)).Done)),
            FirstStepsError.NotFinished => Conflict(new { error = "Finish all six steps first." }),
            FirstStepsError.AlreadyOpened => Conflict(new { error = "You have already opened this world's chest." }),
            _ => Conflict(new { error = "The chest is empty: there is no gear to give." })
        };
    }

    private async Task<FirstStepsResponse> ProgressOf(Guid gameInstanceId, string userId)
    {
        var progress = await _steps.ReadAsync(gameInstanceId, userId);
        return new FirstStepsResponse(progress?.DoneSteps.ToList() ?? new List<string>(), progress?.ChestOpenedAt != null);
    }

    private string? UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private async Task<bool> HasAccess(Guid gameInstanceId, string userId)
        => await _context.GameInstances.AnyAsync(g => g.Id == gameInstanceId &&
            (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
}
