using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// The realm's war log: raids, sieges and what came of them, this season. The whole realm reads the
/// same log - sieges are public already, and a border that everyone can see change is not a secret.
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/warlog")]
[Authorize]
public class WarLogController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly WarLogService _warLog;
    private readonly WorldSiegeService _sieges;

    public WarLogController(ApplicationDbContext context, WarLogService warLog, WorldSiegeService sieges)
    {
        _context = context;
        _warLog = warLog;
        _sieges = sieges;
    }

    [HttpGet]
    public async Task<ActionResult<WarLogResponse>> Read(Guid gameInstanceId, [FromQuery] int limit = 50)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();

        bool member = await _context.GameInstances.AnyAsync(g => g.Id == gameInstanceId &&
            (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
        if (!member) return Forbid();

        // Anything due is written to the log before it is read, so a player catching up sees it.
        await _sieges.AdvanceAsync(gameInstanceId);

        return Ok(new WarLogResponse(await _warLog.ReadAsync(gameInstanceId, limit)));
    }
}
