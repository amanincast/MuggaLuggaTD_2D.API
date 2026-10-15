using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// The Weekly Trial (Active Content E): the week, a try begun and finished, and the debug hooks. Cross-realm; a realm is
/// named only so a finished week's chest can be paid into it, and only one the player is in. The board is
/// <c>GET api/leaderboards/trial</c>.
/// </summary>
[ApiController]
[Route("api/trial")]
[Authorize]
public class TrialController : ControllerBase
{
    private readonly TrialService _trial;
    private readonly RealmMembershipService _membership;
    private readonly IWebHostEnvironment _environment;

    public TrialController(TrialService trial, RealmMembershipService membership, IWebHostEnvironment environment)
    {
        _trial = trial;
        _membership = membership;
        _environment = environment;
    }

    private string Me => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    /// <summary>This week, the day's tries, the top, and any finished week's chest paid into <paramref name="realm"/>.</summary>
    [HttpGet]
    public async Task<ActionResult<TrialResponse>> View([FromQuery] Guid? realm = null)
    {
        if (string.IsNullOrEmpty(Me)) return Forbid();
        return Ok(await _trial.ViewAsync(Me, await RealmAsync(realm)));
    }

    [HttpPost("begin")]
    public async Task<ActionResult<TrialBeginResponse>> Begin([FromBody] TrialBeginRequest request, [FromQuery] Guid? realm = null)
    {
        if (string.IsNullOrEmpty(Me)) return Forbid();
        try
        {
            return Ok(await _trial.BeginAsync(Me, request.Hero, await RealmAsync(realm)));
        }
        catch (TrialRefusal refusal)
        {
            return Conflict(new { message = refusal.Message });
        }
    }

    [HttpPost("finish")]
    public async Task<ActionResult<TrialFinishResponse>> Finish([FromBody] TrialFinishRequest request)
    {
        if (string.IsNullOrEmpty(Me)) return Forbid();
        try
        {
            return Ok(await _trial.FinishAsync(Me, request));
        }
        catch (TrialRefusal refusal)
        {
            return Conflict(new { message = refusal.Message });
        }
    }

    // -----------------------------------------------------------------
    // Debug (Development only; Combat Debug → Weekly Trial)
    // -----------------------------------------------------------------

    [HttpPost("debug/reset-tries")]
    public async Task<IActionResult> DebugResetTries()
    {
        if (!_environment.IsDevelopment()) return NotFound();
        await _trial.DebugResetTriesAsync(Me);
        return Ok(new { message = "Today's tries are back." });
    }

    [HttpPost("debug/end-week")]
    public async Task<IActionResult> DebugEndWeek()
    {
        if (!_environment.IsDevelopment()) return NotFound();
        return Ok(new { message = await _trial.DebugEndWeekAsync(Me) });
    }

    [HttpPost("debug/forget")]
    public async Task<IActionResult> DebugForget()
    {
        if (!_environment.IsDevelopment()) return NotFound();
        await _trial.DebugForgetAsync(Me);
        return Ok(new { message = "Your trial is forgotten." });
    }

    /// <summary>The realm, if the player is in it; otherwise none (nothing is paid).</summary>
    private async Task<Guid?> RealmAsync(Guid? realm) =>
        realm != null && await _membership.IsMemberAsync(realm.Value, Me) ? realm : null;
}
