using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// The realm's NPC factions and how strong each stands (<c>docs/design/npc-factions.md</c>). Public
/// to every member: a warband's readiness is what the Powers panel and the dossier show.
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/factions")]
[Authorize]
public class FactionController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly FactionService _factions;
    private readonly IWebHostEnvironment _environment;

    public FactionController(ApplicationDbContext context, FactionService factions, IWebHostEnvironment environment)
    {
        _context = context;
        _factions = factions;
        _environment = environment;
    }

    [HttpGet]
    public async Task<ActionResult<FactionsResponse>> Read(Guid gameInstanceId)
    {
        var refusal = await RefuseNonMemberAsync(gameInstanceId);
        if (refusal != null) return refusal;

        var response = await _factions.ReadAsync(gameInstanceId);
        return response == null ? NotFound("This realm has no world yet.") : Ok(response);
    }

    /// <summary>The Combat Debug window's faction controls. Development only: elsewhere it does not exist.</summary>
    [HttpPost("debug")]
    public async Task<ActionResult<FactionsResponse>> Debug(Guid gameInstanceId, [FromBody] FactionDebugRequest request)
    {
        if (!_environment.IsDevelopment()) return NotFound();

        var refusal = await RefuseNonMemberAsync(gameInstanceId);
        if (refusal != null) return refusal;

        var response = await _factions.DebugAsync(gameInstanceId, request);
        return response == null ? NotFound("This realm has no world yet.") : Ok(response);
    }

    private async Task<ActionResult?> RefuseNonMemberAsync(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        bool member = await _context.GameInstances.AnyAsync(g => g.Id == gameInstanceId &&
            (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
        return member ? null : Forbid();
    }
}
