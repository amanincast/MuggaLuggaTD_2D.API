using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
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
    private readonly IWebHostEnvironment _environment;

    public HiringController(ApplicationDbContext context, HiringService hiring, GoldService gold, SeasonScoreService seasons,
        IWebHostEnvironment environment)
    {
        _context = context;
        _hiring = hiring;
        _gold = gold;
        _seasons = seasons;
        _environment = environment;
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

    /// <summary>★ KEEP: one of the (at most two) workers who go into the next season.</summary>
    [HttpPost("keep")]
    public async Task<ActionResult<HiringHallResponse>> Keep(Guid gameInstanceId, [FromBody] HiringKeepRequest request)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, request.SharedContractVersion);
        if (refusal != null) return refusal;
        var outcome = await _hiring.KeepAsync(gameInstanceId, userId!, request.WorkerId, request.Keep);
        return outcome.Succeeded ? Ok(await HallAsync(gameInstanceId, userId!)) : Refuse(outcome);
    }

    /// <summary>DISMISS: the worker leaves for good, freeing their bed. No refund.</summary>
    [HttpPost("dismiss")]
    public async Task<ActionResult<HiringHallResponse>> Dismiss(Guid gameInstanceId, [FromBody] HiringWorkerRequest request)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, request.SharedContractVersion);
        if (refusal != null) return refusal;
        var outcome = await _hiring.DismissAsync(gameInstanceId, userId!, request.WorkerId);
        return outcome.Succeeded ? Ok(await HallAsync(gameInstanceId, userId!)) : Refuse(outcome);
    }

    /// <summary>The reveals have played.</summary>
    [HttpPost("rolls/seen")]
    public async Task<ActionResult<HiringHallResponse>> RollsSeen(Guid gameInstanceId, [FromBody] HiringSeenRequest request)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, request.SharedContractVersion);
        if (refusal != null) return refusal;
        await _hiring.MarkRollsSeenAsync(gameInstanceId, userId!);
        return Ok(await HallAsync(gameInstanceId, userId!));
    }

    /// <summary>Who would go with the player into the next season (the season-end preview).</summary>
    [HttpGet("carryover")]
    public async Task<ActionResult<List<WorkerCarryDto>>> CarryOver(Guid gameInstanceId)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, SharedContract.Version);
        if (refusal != null) return refusal;
        await _hiring.SettlePlayerAsync(gameInstanceId, userId!);
        var carry = await _hiring.CarryOverPreviewAsync(gameInstanceId, userId!);
        return Ok(carry.Select(c => new WorkerCarryDto(c.Worker.Id, c.Worker.Name, c.Worker.Level, c.Level, c.Worker.Keep)).ToList());
    }

    /// <summary>
    /// Debug, Development only (Combat Debug, Hiring): add hours to every worker, set one worker's
    /// level, force the next promotion roll, or replay the reveals.
    /// </summary>
    [HttpPost("debug/{shortcut}")]
    public async Task<ActionResult<HiringHallResponse>> Debug(Guid gameInstanceId, string shortcut, [FromBody] HiringDebugRequest request)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var (userId, refusal) = await GateAsync(gameInstanceId, SharedContract.Version);
        if (refusal != null) return refusal;
        switch (shortcut)
        {
            case "hours":
                await _hiring.DebugAddHoursAsync(gameInstanceId, userId!, request.Hours ?? 24);
                break;
            case "level":
                if (request.WorkerId == null || request.Level == null) return BadRequest(new { error = "workerId and level" });
                var outcome = await _hiring.DebugSetLevelAsync(gameInstanceId, userId!, request.WorkerId.Value, request.Level.Value);
                if (!outcome.Succeeded) return Refuse(outcome);
                break;
            case "promotion":
                HiringService.DebugForcePromotion(userId!, request.ForcePromotion);
                break;
            case "replay":
                await _hiring.DebugReplayRevealsAsync(gameInstanceId, userId!);
                break;
            default:
                return NotFound();
        }
        return Ok(await HallAsync(gameInstanceId, userId!));
    }

    private static HiredWorkerDto ToDto(HiredWorker w)
    {
        int level = w.Level;
        var rolls = w.RollList;
        return new HiredWorkerDto(w.Id, w.Name, w.Trade, w.Tier, w.TraitList, w.SecondTrade,
            w.HomeBiome, w.Look, w.SiteId, w.RatePerHour,
            level, w.HoursWorked, WorkerLevelRules.NextLevelAt(level), w.PerkList, w.Keep,
            level >= 5 ? Naming.WorkerByName(w.Trade, w.TraitList, Naming.Hash(w.Id.ToString())) : null,
            w.LifetimeOutput, w.SeasonsServed,
            rolls.Skip(Math.Max(0, w.RollsSeen)).Select(r => new WorkerRevealDto(r.Level, r.Kind, r.Perk,
                r.Perk.HasValue ? WorkerLevelRules.GradeOf(r.Perk.Value) : null, r.NewTier, r.NewTrait, r.NewSecondTrade)).ToList());
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
            workers.Select(ToDto).ToList(),
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
