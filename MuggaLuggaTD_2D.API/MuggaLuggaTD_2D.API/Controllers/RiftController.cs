using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>Rifts from other realms (Invasions spec): the realm's rifts, a sealed rift's chest, and the debug hooks.</summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/rifts")]
[Authorize]
public class RiftController : ControllerBase
{
    private readonly RiftService _rifts;
    private readonly RealmMembershipService _membership;
    private readonly IWebHostEnvironment _environment;

    public RiftController(RiftService rifts, RealmMembershipService membership, IWebHostEnvironment environment)
    {
        _rifts = rifts;
        _membership = membership;
        _environment = environment;
    }

    [HttpGet]
    public async Task<ActionResult<RiftsResponse>> List(Guid gameInstanceId)
    {
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();
        return Ok(await _rifts.ViewAsync(gameInstanceId, userId));
    }

    [HttpPost("{riftId:guid}/chest")]
    public async Task<ActionResult<RiftChestResponse>> OpenChest(Guid gameInstanceId, Guid riftId)
    {
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();

        var (error, chest) = await _rifts.OpenChestAsync(gameInstanceId, userId, riftId);
        return error switch
        {
            RiftChestError.None => Ok(chest),
            RiftChestError.NoSuchRift => NotFound(new { message = "No such rift." }),
            RiftChestError.NoChest => BadRequest(new { message = "You have no chest from this rift." }),
            RiftChestError.AlreadyOpened => Conflict(new { message = "You have already opened this chest." }),
            _ => StatusCode(500, new { message = "The chest held nothing." }),
        };
    }

    // -----------------------------------------------------------------
    // Debug (Development only; Combat Debug → Rifts)
    // -----------------------------------------------------------------

    /// <summary>Opens a rift now, with no omen: Lesser in one of my regions, Great where the rules put it, or in <paramref name="regionId"/>.</summary>
    [HttpPost("debug/open")]
    public async Task<ActionResult<RiftsResponse>> DebugOpen(Guid gameInstanceId, [FromQuery] RiftKind kind, [FromQuery] string? regionId = null)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();

        var (_, error) = await _rifts.DebugOpenAsync(gameInstanceId, userId, kind, regionId);
        if (error != null) return BadRequest(new { message = error });
        return Ok(await _rifts.ViewAsync(gameInstanceId, userId));
    }

    /// <summary>Runs the newest live rift of a kind out of time: a Great Rift erupts, a Lesser Rift invades.</summary>
    [HttpPost("debug/timeout")]
    public async Task<ActionResult<RiftsResponse>> DebugTimeOut(Guid gameInstanceId, [FromQuery] RiftKind kind)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();

        var rift = await _rifts.DebugNewestAsync(gameInstanceId, userId, kind);
        if (rift == null) return BadRequest(new { message = $"No live {kind} Rift." });
        await _rifts.DebugTimeOutAsync(rift);
        return Ok(await _rifts.ViewAsync(gameInstanceId, userId));
    }

    /// <summary>Seals the newest live rift of a kind, as my win: a Great Rift's whole remaining Breach is mine.</summary>
    [HttpPost("debug/seal")]
    public async Task<ActionResult<RiftsResponse>> DebugSeal(Guid gameInstanceId, [FromQuery] RiftKind kind)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();

        var rift = await _rifts.DebugNewestAsync(gameInstanceId, userId, kind);
        if (rift == null) return BadRequest(new { message = $"No live {kind} Rift." });
        if (!RiftRules.CanSeal(rift.Stage)) return BadRequest(new { message = "The rift has not opened yet." });
        await _rifts.RecordWinAsync(rift.Id, userId, rift.Breach);
        return Ok(await _rifts.ViewAsync(gameInstanceId, userId));
    }

    /// <summary>Sets the live Great Rift's Breach to a fraction of its whole.</summary>
    [HttpPost("debug/breach")]
    public async Task<ActionResult<RiftsResponse>> DebugBreach(Guid gameInstanceId, [FromQuery] double fraction = 0.1)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();

        var rift = await _rifts.DebugNewestAsync(gameInstanceId, userId, RiftKind.Great);
        if (rift == null) return BadRequest(new { message = "No live Great Rift." });
        await _rifts.DebugSetBreachAsync(rift, fraction);
        return Ok(await _rifts.ViewAsync(gameInstanceId, userId));
    }

    private async Task<string?> MemberAsync(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null || !await _membership.IsMemberAsync(gameInstanceId, userId)) return null;
        return userId;
    }
}
