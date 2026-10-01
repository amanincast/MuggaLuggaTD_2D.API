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

/// <summary>The Hiring Hall (design 12e): the board, hiring, and sending workers to sites.</summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/hiring")]
[Authorize]
public class HiringController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly HiringService _hiring;
    private readonly GoldService _gold;
    private readonly SeasonScoreService _seasons;

    public HiringController(ApplicationDbContext context, HiringService hiring, GoldService gold, SeasonScoreService seasons)
    {
        _context = context;
        _hiring = hiring;
        _gold = gold;
        _seasons = seasons;
    }

    [HttpGet]
    public async Task<ActionResult<HiringHallResponse>> Read(Guid gameInstanceId)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, SharedContract.Version);
        if (refusal != null) return refusal;
        await _hiring.SettlePlayerAsync(gameInstanceId, userId!);
        return Ok(await HallAsync(gameInstanceId, userId!));
    }

    [HttpPost("hire")]
    public async Task<ActionResult<HiringHallResponse>> Hire(Guid gameInstanceId, [FromBody] HiringHireRequest request)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, request.SharedContractVersion);
        if (refusal != null) return refusal;
        var (outcome, _) = await _hiring.HireAsync(gameInstanceId, userId!, request.Slot);
        return outcome.Succeeded ? Ok(await HallAsync(gameInstanceId, userId!)) : Refuse(outcome);
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<HiringHallResponse>> Refresh(Guid gameInstanceId, [FromBody] HiringRefreshRequest request)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, request.SharedContractVersion);
        if (refusal != null) return refusal;
        var outcome = await _hiring.RefreshAsync(gameInstanceId, userId!);
        return outcome.Succeeded ? Ok(await HallAsync(gameInstanceId, userId!)) : Refuse(outcome);
    }

    [HttpPost("assign")]
    public async Task<ActionResult<HiringHallResponse>> Assign(Guid gameInstanceId, [FromBody] HiringAssignRequest request)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, request.SharedContractVersion);
        if (refusal != null) return refusal;
        var outcome = await _hiring.AssignAsync(gameInstanceId, userId!, request.WorkerId, request.SiteId);
        return outcome.Succeeded ? Ok(await HallAsync(gameInstanceId, userId!)) : Refuse(outcome);
    }

    private async Task<HiringHallResponse> HallAsync(Guid gameInstanceId, string userId)
    {
        var board = await _hiring.ReadBoardAsync(gameInstanceId, userId);
        var workers = await _hiring.WorkersAsync(gameInstanceId, userId);
        var sites = await _hiring.SitesAsync(gameInstanceId, userId);
        var (beds, used) = await _hiring.BedsAsync(gameInstanceId, userId);

        return new HiringHallResponse(
            board.Select(c => new HiringCandidateCard(c.Slot, c.Name, c.Trade, c.Tier, HiringTraits.Parse(c.Traits),
                c.SecondTrade, c.HomeBiome, c.Look, HiringRules.CostOf(c.Tier), HiringRules.BaseRate(c.Tier))).ToList(),
            workers.Select(w => new HiredWorkerDto(w.Id, w.Name, w.Trade, w.Tier, w.TraitList, w.SecondTrade,
                w.HomeBiome, w.Look, w.SiteId, w.RatePerHour)).ToList(),
            sites.Select(s => new HiringSiteDto(s.SiteId, s.RegionId, s.Trade, s.Tier, s.Biome, s.Slots, s.Used)).ToList(),
            beds, used,
            await _hiring.RefreshCostAsync(gameInstanceId, userId),
            await _hiring.NextArrivalAsync(gameInstanceId, userId),
            await _gold.BalanceAsync(gameInstanceId, userId));
    }

    private async Task<(string? UserId, ActionResult? Refusal)> GateAsync(Guid gameInstanceId, string? contractVersion)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return (null, Unauthorized());
        if (!await HasAccess(gameInstanceId, userId)) return (null, Forbid());
        if (contractVersion != SharedContract.Version)
            return (null, Conflict(new { error = "Shared contract version mismatch.", expected = SharedContract.Version, received = contractVersion }));

        // A closed season resets the realm first, so nobody hires into a dead world.
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);
        return (userId, null);
    }

    private ActionResult Refuse(HiringOutcome outcome) => outcome.Error switch
    {
        HiringError.NoSuchSlot or HiringError.NoSuchWorker or HiringError.NoSuchSite => NotFound(new { error = outcome.Message }),
        _ => Conflict(new { error = outcome.Message })
    };

    private async Task<bool> HasAccess(Guid gameInstanceId, string userId)
        => await _context.GameInstances.AnyAsync(g => g.Id == gameInstanceId &&
            (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
}
