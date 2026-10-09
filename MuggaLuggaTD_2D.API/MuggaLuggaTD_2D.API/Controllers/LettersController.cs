using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// A player's letters in one realm (the Inbox spec): what happened to them, each with an action. Only
/// the realm the player is in (Mike, 2026-10-08: no cross-realm view).
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/letters")]
[Authorize]
public class LettersController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly LetterService _letters;
    private readonly WorldSiegeService _sieges;
    private readonly PartyService _parties;
    private readonly SeasonScoreService _seasons;

    public LettersController(ApplicationDbContext context, LetterService letters, WorldSiegeService sieges,
        PartyService parties, SeasonScoreService seasons)
    {
        _context = context;
        _letters = letters;
        _sieges = sieges;
        _parties = parties;
        _seasons = seasons;
    }

    /// <summary>
    /// Lazy events become facts only when something reads them, so a player who was away is caught up
    /// first: the season, due sieges, their companies (arrivals, ambushes, auto mode), heroes walking home.
    /// </summary>
    private async Task CatchUpAsync(Guid gameInstanceId, string userId)
    {
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);
        await _sieges.AdvanceAsync(gameInstanceId);
        await _parties.ListAsync(gameInstanceId, userId);
        await _letters.CatchUpAsync(gameInstanceId, userId);
    }

    /// <summary>A page of letters, newest first, after the catch-up.</summary>
    [HttpGet]
    public async Task<ActionResult<LettersResponse>> Page(Guid gameInstanceId, [FromQuery] DateTime? before = null, [FromQuery] int take = 50)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await IsMemberAsync(gameInstanceId, userId)) return Forbid();

        await CatchUpAsync(gameInstanceId, userId);
        return Ok(await _letters.PageAsync(gameInstanceId, userId, before, take));
    }

    /// <summary>The unread count and whether any needs the player: the Hall's badge.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<LetterSummaryResponse>> Summary(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await IsMemberAsync(gameInstanceId, userId)) return Forbid();

        await CatchUpAsync(gameInstanceId, userId);
        return Ok(await _letters.SummaryAsync(gameInstanceId, userId));
    }

    [HttpPost("read")]
    public async Task<ActionResult<LetterSummaryResponse>> Read(Guid gameInstanceId, [FromBody] MarkLettersReadRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await IsMemberAsync(gameInstanceId, userId)) return Forbid();

        await _letters.MarkReadAsync(gameInstanceId, userId, request.Ids, request.All);
        return Ok(await _letters.SummaryAsync(gameInstanceId, userId));
    }

    private Task<bool> IsMemberAsync(Guid gameInstanceId, string userId) =>
        _context.GameInstances.AnyAsync(g => g.Id == gameInstanceId &&
            (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
}
