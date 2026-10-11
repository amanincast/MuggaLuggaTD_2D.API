using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

public record ForgeRequest(string? ItemId, string? SecondId);

/// <summary>The Forge (Forge spec): Temper, Ascend and Salvage, and the debug hooks.</summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/forge")]
[Authorize]
public class ForgeController : ControllerBase
{
    private readonly ForgeService _forge;
    private readonly RealmMembershipService _membership;
    private readonly IWebHostEnvironment _environment;

    public ForgeController(ForgeService forge, RealmMembershipService membership, IWebHostEnvironment environment)
    {
        _forge = forge;
        _membership = membership;
        _environment = environment;
    }

    [HttpPost("temper")]
    public async Task<ActionResult<ForgeResponse>> Temper(Guid gameInstanceId, [FromBody] ForgeRequest request)
    {
        var userId = await MemberAsync(gameInstanceId);
        return userId == null ? Forbid() : Answer(await _forge.TemperAsync(gameInstanceId, userId, request.ItemId));
    }

    [HttpPost("ascend")]
    public async Task<ActionResult<ForgeResponse>> Ascend(Guid gameInstanceId, [FromBody] ForgeRequest request)
    {
        var userId = await MemberAsync(gameInstanceId);
        return userId == null ? Forbid() : Answer(await _forge.AscendAsync(gameInstanceId, userId, request.ItemId, request.SecondId));
    }

    [HttpPost("salvage")]
    public async Task<ActionResult<ForgeResponse>> Salvage(Guid gameInstanceId, [FromBody] ForgeRequest request)
    {
        var userId = await MemberAsync(gameInstanceId);
        return userId == null ? Forbid() : Answer(await _forge.SalvageAsync(gameInstanceId, userId, request.ItemId));
    }

    /// <summary>Development only (Combat Debug): essences, shards, Minor crystals and 5,000 gold.</summary>
    [HttpPost("debug/stock")]
    public async Task<IActionResult> DebugStock(Guid gameInstanceId)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();
        await _forge.DebugStockAsync(gameInstanceId, userId);
        return Ok(new { message = "The forge is stocked." });
    }

    /// <summary>Development only (Combat Debug): two Common tier-1 pieces of one kind.</summary>
    [HttpPost("debug/pieces")]
    public async Task<IActionResult> DebugPieces(Guid gameInstanceId)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = await MemberAsync(gameInstanceId);
        if (userId == null) return Forbid();
        return Ok(await _forge.DebugPiecesAsync(gameInstanceId, userId));
    }

    private ActionResult<ForgeResponse> Answer(ForgeOutcome outcome) => outcome.Error switch
    {
        ForgeError.None => Ok(outcome.Response),
        ForgeError.NoSuchItem => NotFound(new { message = outcome.Message }),
        ForgeError.NotEnough => BadRequest(new { message = outcome.Message }),
        _ => Conflict(new { message = outcome.Message }),
    };

    private async Task<string?> MemberAsync(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null || !await _membership.IsMemberAsync(gameInstanceId, userId)) return null;
        return userId;
    }
}
