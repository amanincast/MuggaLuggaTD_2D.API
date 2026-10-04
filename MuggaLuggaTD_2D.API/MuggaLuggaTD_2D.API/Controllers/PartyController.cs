using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Hubs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// A player's companies in a realm: list, form, rename and re-man, disband
/// (<c>docs/design/parties-and-travel.md</c>, Unity repo). The world is only read, never written.
/// Every change a player makes to their companies is broadcast as <c>PartyMoved</c>, carrying only
/// who moved: other players then ask <c>GET others</c>, which shows each of them only what they can see.
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/parties")]
[Authorize]
public class PartyController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly PartyService _parties;
    private readonly ISessionLog _sessionLog;
    private readonly IHubContext<GameHub> _hub;
    private readonly AutoFightService _auto;

    public PartyController(ApplicationDbContext context, PartyService parties, ISessionLog sessionLog, IHubContext<GameHub> hub,
        AutoFightService auto)
    {
        _context = context;
        _parties = parties;
        _auto = auto;
        _sessionLog = sessionLog;
        _hub = hub;
    }

    /// <summary>Other players' companies in the regions this player can see (phase 5).</summary>
    [HttpGet("others")]
    public async Task<ActionResult<RivalCompaniesResponse>> Others(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response) = await _parties.OthersAsync(gameInstanceId, userId);
        if (outcome.Succeeded && response != null) return Ok(response);
        return Refuse(outcome, userId, "others");
    }

    [HttpGet]
    public async Task<ActionResult<PartiesResponse>> List(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response) = await _parties.ListAsync(gameInstanceId, userId);
        return Respond(outcome, response, userId, "list");
    }

    [HttpPost]
    public async Task<ActionResult<PartiesResponse>> Create(Guid gameInstanceId, [FromBody] PartyCreateRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response) = await _parties.CreateAsync(gameInstanceId, userId, request);
        return await MovedAsync(gameInstanceId, outcome, response, userId, "form");
    }

    [HttpPut("{partyId:guid}")]
    public async Task<ActionResult<PartiesResponse>> Update(Guid gameInstanceId, Guid partyId, [FromBody] PartyUpdateRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response) = await _parties.UpdateAsync(gameInstanceId, userId, partyId, request);
        return await MovedAsync(gameInstanceId, outcome, response, userId, $"set {partyId}");
    }

    [HttpPost("{partyId:guid}/travel")]
    public async Task<ActionResult<PartiesResponse>> Travel(Guid gameInstanceId, Guid partyId, [FromBody] PartyTravelRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response) = await _parties.TravelAsync(gameInstanceId, userId, partyId, request);
        return await MovedAsync(gameInstanceId, outcome, response, userId, $"travel {partyId} -> {request.SiteId}");
    }

    /// <summary>Fights the warband that has a company halted: opens the run its claim will name.</summary>
    [HttpPost("{partyId:guid}/ambush/fight")]
    public async Task<ActionResult<AmbushFightResponse>> FightAmbush(Guid gameInstanceId, Guid partyId, [FromBody] AmbushOrderRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response) = await _parties.FightAmbushAsync(gameInstanceId, userId, partyId, request);
        if (outcome.Succeeded && response != null) return Ok(response);
        return Refuse(outcome, userId, $"ambush-fight {partyId}");
    }

    /// <summary>Turns a halted company back the way it came.</summary>
    [HttpPost("{partyId:guid}/ambush/flee")]
    public async Task<ActionResult<PartiesResponse>> FleeAmbush(Guid gameInstanceId, Guid partyId, [FromBody] AmbushOrderRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response) = await _parties.FleeAmbushAsync(gameInstanceId, userId, partyId, request);
        return await MovedAsync(gameInstanceId, outcome, response, userId, $"ambush-flee {partyId}");
    }

    /// <summary>Settles an ambush fight, won or lost.</summary>
    [HttpPost("{partyId:guid}/ambush/claim")]
    public async Task<ActionResult<AmbushClaimResponse>> ClaimAmbush(Guid gameInstanceId, Guid partyId, [FromBody] AmbushClaimRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response) = await _parties.ClaimAmbushAsync(gameInstanceId, userId, partyId, request);
        if (outcome.Succeeded && response != null)
        {
            // Won, it marches on; lost, it turns back. Either way it has moved.
            await BroadcastMovedAsync(gameInstanceId, userId);
            return Ok(response);
        }
        return Refuse(outcome, userId, $"ambush-claim {partyId} won={request.Won}");
    }

    [HttpDelete("{partyId:guid}")]
    public async Task<ActionResult<PartiesResponse>> Disband(Guid gameInstanceId, Guid partyId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response) = await _parties.DisbandAsync(gameInstanceId, userId, partyId);
        return await MovedAsync(gameInstanceId, outcome, response, userId, $"disband {partyId}");
    }

    // ---- Auto mode (docs/design/auto-fight.md, phase 2) ----

    /// <summary>Puts a company into auto mode, or takes it out. Answers with the companies.</summary>
    [HttpPost("{partyId:guid}/auto")]
    public async Task<ActionResult<PartiesResponse>> AutoMode(Guid gameInstanceId, Guid partyId, [FromBody] AutoModeRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var outcome = await _auto.SetModeAsync(gameInstanceId, userId, partyId, request);
        if (!outcome.Succeeded) return RefuseAuto(outcome, userId, $"auto {partyId} on={request.On}");
        var (listed, response) = await _parties.ListAsync(gameInstanceId, userId);
        return await MovedAsync(gameInstanceId, listed, response, userId, $"auto {partyId}");
    }

    /// <summary>Orders a company in auto mode to roam or patrol a region its player holds. Answers with the companies.</summary>
    [HttpPost("{partyId:guid}/auto/order")]
    public async Task<ActionResult<PartiesResponse>> AutoOrder(Guid gameInstanceId, Guid partyId, [FromBody] AutoOrderRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var outcome = await _auto.OrderAsync(gameInstanceId, userId, partyId, request);
        if (!outcome.Succeeded) return RefuseAuto(outcome, userId, $"auto-order {partyId} {request.Order} {request.RegionId}");
        var (listed, response) = await _parties.ListAsync(gameInstanceId, userId);
        return await MovedAsync(gameInstanceId, listed, response, userId, $"auto-order {partyId}");
    }

    /// <summary>What companies in auto mode have done that the client has not yet taken into its save.</summary>
    [HttpGet("auto/reports")]
    public async Task<ActionResult<AutoReportsResponse>> AutoReports(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        return Ok(await _auto.ReportsAsync(gameInstanceId, userId));
    }

    /// <summary>Marks reports collected, once their experience and gear are in the client's save.</summary>
    [HttpPost("auto/reports/collect")]
    public async Task<ActionResult> CollectAutoReports(Guid gameInstanceId, [FromBody] AutoCollectRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        int collected = await _auto.CollectAsync(gameInstanceId, userId, request);
        return Ok(new { collected });
    }

    private ObjectResult RefuseAuto(AutoOutcome outcome, string userId, string what)
    {
        _sessionLog.Log("AUTO-DENY", $"user={userId} {what} {outcome.Error}: {outcome.Message}");
        return outcome.Error switch
        {
            AutoError.WorldNotFound or AutoError.PartyNotFound => NotFound(new { message = outcome.Message }),
            AutoError.ContractMismatch or AutoError.Busy or AutoError.Empty or AutoError.NotInAutoMode
                or AutoError.RegionNotHeld or AutoError.TooStrong => Conflict(new { message = outcome.Message }),
            _ => BadRequest(new { message = outcome.Message })
        };
    }

    private async Task<ActionResult<PartiesResponse>> MovedAsync(Guid gameInstanceId, PartyOutcome outcome,
        PartiesResponse? response, string userId, string what)
    {
        if (outcome.Succeeded && response != null) await BroadcastMovedAsync(gameInstanceId, userId);
        return Respond(outcome, response, userId, what);
    }

    /// <summary>Tells the realm that this player's companies changed; each client asks again what it can see.</summary>
    private Task BroadcastMovedAsync(Guid gameInstanceId, string userId) =>
        _hub.Clients.Group(gameInstanceId.ToString())
            .SendAsync("PartyMoved", new PartyMovedNotification(gameInstanceId, userId));

    private ActionResult<PartiesResponse> Respond(PartyOutcome outcome, PartiesResponse? response, string userId, string what)
    {
        if (outcome.Succeeded && response != null) return Ok(response);
        return Refuse(outcome, userId, what);
    }

    private ObjectResult Refuse(PartyOutcome outcome, string userId, string what)
    {
        _sessionLog.Log("PARTY-DENY", $"user={userId} {what} {outcome.Error}: {outcome.Message}");
        return outcome.Error switch
        {
            PartyError.WorldNotFound or PartyError.PartyNotFound or PartyError.SiteNotFound => NotFound(new { message = outcome.Message }),
            PartyError.ContractMismatch or PartyError.TooManyCompanies or PartyError.CharacterCommitted
                or PartyError.InAnotherCompany or PartyError.LastCompany or PartyError.Busy
                or PartyError.AlreadyThere or PartyError.NoRoute or PartyError.NotInThisRegion or PartyError.Empty
                or PartyError.NotAmbushed or PartyError.RunNotFound or PartyError.RunTooFast
                => Conflict(new { message = outcome.Message }),
            _ => BadRequest(new { message = outcome.Message })
        };
    }

    private async Task<bool> HasAccessToGameInstance(Guid gameInstanceId, string userId)
    {
        return await _context.GameInstances
            .AnyAsync(g => g.Id == gameInstanceId &&
                (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
    }
}
