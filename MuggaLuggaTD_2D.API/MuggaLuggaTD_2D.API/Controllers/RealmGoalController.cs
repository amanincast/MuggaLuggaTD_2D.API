using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>The realm's goal of the day (Active Content B): what it is, how far along, and my share.</summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/realm-goal")]
[Authorize]
public class RealmGoalController : ControllerBase
{
    private readonly RealmGoalService _goals;
    private readonly RealmMembershipService _membership;
    private readonly IWebHostEnvironment _environment;

    public RealmGoalController(RealmGoalService goals, RealmMembershipService membership, IWebHostEnvironment environment)
    {
        _goals = goals;
        _membership = membership;
        _environment = environment;
    }

    [HttpGet]
    public async Task<ActionResult<RealmGoalResponse>> Today(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await _membership.IsMemberAsync(gameInstanceId, userId)) return Forbid();

        var view = await _goals.ViewAsync(gameInstanceId, userId);
        return view == null ? NotFound(new { message = "This realm has no world yet." }) : Ok(view);
    }

    /// <summary>Development only (Combat Debug): moves today's goal to a fraction of its target, as this player's deeds.</summary>
    [HttpPost("debug/fill")]
    public async Task<ActionResult<RealmGoalResponse>> Fill(Guid gameInstanceId, [FromQuery] double fraction = 0.99)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await _membership.IsMemberAsync(gameInstanceId, userId)) return Forbid();

        await _goals.FillAsync(gameInstanceId, userId, fraction);
        return Ok(await _goals.ViewAsync(gameInstanceId, userId));
    }
}
