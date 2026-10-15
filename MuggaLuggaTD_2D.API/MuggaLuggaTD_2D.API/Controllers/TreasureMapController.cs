using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>Treasure maps (Active Content D): the holder's maps, and the debug hooks. The cache fights go through pve begin/claim.</summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/treasure-maps")]
[Authorize]
public class TreasureMapController : ControllerBase
{
    private readonly TreasureMapService _maps;
    private readonly RealmMembershipService _membership;
    private readonly IWebHostEnvironment _environment;

    public TreasureMapController(TreasureMapService maps, RealmMembershipService membership, IWebHostEnvironment environment)
    {
        _maps = maps;
        _membership = membership;
        _environment = environment;
    }

    [HttpGet]
    public async Task<ActionResult<TreasureMapsResponse>> List(Guid gameInstanceId)
    {
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();
        return Ok(await _maps.ViewAsync(gameInstanceId, userId));
    }

    // -----------------------------------------------------------------
    // Debug (Development only; Combat Debug → Treasure maps)
    // -----------------------------------------------------------------

    /// <summary>Gives this player a map; to <paramref name="regionId"/> when given, else wherever its id points.</summary>
    [HttpPost("debug/grant")]
    public async Task<ActionResult<TreasureMapsResponse>> DebugGrant(Guid gameInstanceId, [FromQuery] string? regionId = null)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();

        var map = await _maps.GrantAsync(gameInstanceId, userId, "debug", null, regionId: string.IsNullOrEmpty(regionId) ? null : regionId);
        if (map == null) return BadRequest(new { message = "No map made: you hold the most already, or that region has no site to hide one by." });
        return Ok(await _maps.ViewAsync(gameInstanceId, userId));
    }

    /// <summary>Forgets this player's maps.</summary>
    [HttpPost("debug/forget")]
    public async Task<ActionResult<TreasureMapsResponse>> DebugForget(Guid gameInstanceId)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();

        await _maps.DebugForgetAsync(gameInstanceId, userId);
        return Ok(await _maps.ViewAsync(gameInstanceId, userId));
    }

    private async Task<string?> MemberAsync(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null || !await _membership.IsMemberAsync(gameInstanceId, userId)) return null;
        return userId;
    }
}
