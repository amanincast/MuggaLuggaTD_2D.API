using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// The Trainer (design 6c): permanent talents, learnt a rank at a time and unlearnt all at once for
/// gold. The only writer of a character's talents; see <see cref="TalentService"/>.
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/talents")]
[Authorize]
public class TalentController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly TalentService _talents;

    public TalentController(ApplicationDbContext context, TalentService talents)
    {
        _context = context;
        _talents = talents;
    }

    [HttpPost("learn")]
    public async Task<ActionResult<TalentsResponse>> Learn(Guid gameInstanceId, [FromBody] LearnTalentRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();
        if (request.SharedContractVersion != SharedContract.Version) return Mismatch(request.SharedContractVersion);

        return Answer(await _talents.LearnAsync(gameInstanceId, userId, request.CharacterId, request.NodeId));
    }

    [HttpPost("respec")]
    public async Task<ActionResult<TalentsResponse>> Respec(Guid gameInstanceId, [FromBody] RespecTalentsRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();
        if (request.SharedContractVersion != SharedContract.Version) return Mismatch(request.SharedContractVersion);

        return Answer(await _talents.RespecAsync(gameInstanceId, userId, request.CharacterId));
    }

    private ActionResult<TalentsResponse> Answer(TalentOutcome outcome)
    {
        if (outcome.Succeeded)
        {
            return Ok(new TalentsResponse(outcome.CharacterId, outcome.Talents ?? new Dictionary<string, int>(),
                outcome.Points, outcome.Unspent, outcome.RespecCost, outcome.GoldBalance));
        }

        return outcome.Error switch
        {
            TalentError.NoSave or TalentError.NoSuchCharacter => NotFound(new { error = outcome.Message }),
            _ => Conflict(new { error = outcome.Message })
        };
    }

    private ConflictObjectResult Mismatch(string received) => Conflict(new
    {
        error = "Shared contract version mismatch.",
        expected = SharedContract.Version,
        received
    });

    private async Task<bool> HasAccessToGameInstance(Guid gameInstanceId, string userId)
        => await _context.GameInstances.AnyAsync(g => g.Id == gameInstanceId &&
            (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
}
