using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>Wanted champions (Active Content A): the realm's board, and the debug hooks. The fights go through pve begin/claim.</summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/bounties")]
[Authorize]
public class BountyController : ControllerBase
{
    private readonly BountyService _bounties;
    private readonly RealmMembershipService _membership;
    private readonly IWebHostEnvironment _environment;

    public BountyController(BountyService bounties, RealmMembershipService membership, IWebHostEnvironment environment)
    {
        _bounties = bounties;
        _membership = membership;
        _environment = environment;
    }

    [HttpGet]
    public async Task<ActionResult<BountiesResponse>> List(Guid gameInstanceId)
    {
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();
        return Ok(await _bounties.ViewAsync(gameInstanceId, userId));
    }

    // -----------------------------------------------------------------
    // Debug (Development only; Combat Debug → Bounties)
    // -----------------------------------------------------------------

    /// <summary>Stands the newest unclaimed champion in <paramref name="regionId"/>.</summary>
    [HttpPost("debug/here")]
    public async Task<ActionResult<BountiesResponse>> DebugHere(Guid gameInstanceId, [FromQuery] string regionId)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();

        var error = await _bounties.DebugPinAsync(gameInstanceId, regionId);
        if (error != null) return BadRequest(new { message = error });
        return Ok(await _bounties.ViewAsync(gameInstanceId, userId));
    }

    /// <summary>Forgets this season's claims and pins, and my attempts.</summary>
    [HttpPost("debug/release")]
    public async Task<ActionResult<BountiesResponse>> DebugRelease(Guid gameInstanceId)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();

        await _bounties.DebugReleaseAsync(gameInstanceId, userId);
        return Ok(await _bounties.ViewAsync(gameInstanceId, userId));
    }

    private async Task<string?> MemberAsync(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null || !await _membership.IsMemberAsync(gameInstanceId, userId)) return null;
        return userId;
    }
}
