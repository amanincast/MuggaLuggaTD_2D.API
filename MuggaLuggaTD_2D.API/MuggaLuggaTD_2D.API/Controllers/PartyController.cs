using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// A player's companies in a realm: list, form, rename and re-man, disband
/// (<c>docs/design/parties-and-travel.md</c>, Unity repo). Companies are the player's own business, so
/// nothing here is broadcast; the world is only read, never written.
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/parties")]
[Authorize]
public class PartyController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly PartyService _parties;
    private readonly ISessionLog _sessionLog;

    public PartyController(ApplicationDbContext context, PartyService parties, ISessionLog sessionLog)
    {
        _context = context;
        _parties = parties;
        _sessionLog = sessionLog;
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
        return Respond(outcome, response, userId, "form");
    }

    [HttpPut("{partyId:guid}")]
    public async Task<ActionResult<PartiesResponse>> Update(Guid gameInstanceId, Guid partyId, [FromBody] PartyUpdateRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response) = await _parties.UpdateAsync(gameInstanceId, userId, partyId, request);
        return Respond(outcome, response, userId, $"set {partyId}");
    }

    [HttpPost("{partyId:guid}/travel")]
    public async Task<ActionResult<PartiesResponse>> Travel(Guid gameInstanceId, Guid partyId, [FromBody] PartyTravelRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response) = await _parties.TravelAsync(gameInstanceId, userId, partyId, request);
        return Respond(outcome, response, userId, $"travel {partyId} -> {request.SiteId}");
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
        return Respond(outcome, response, userId, $"ambush-flee {partyId}");
    }

    /// <summary>Settles an ambush fight, won or lost.</summary>
    [HttpPost("{partyId:guid}/ambush/claim")]
    public async Task<ActionResult<AmbushClaimResponse>> ClaimAmbush(Guid gameInstanceId, Guid partyId, [FromBody] AmbushClaimRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response) = await _parties.ClaimAmbushAsync(gameInstanceId, userId, partyId, request);
        if (outcome.Succeeded && response != null) return Ok(response);
        return Refuse(outcome, userId, $"ambush-claim {partyId} won={request.Won}");
    }

    [HttpDelete("{partyId:guid}")]
    public async Task<ActionResult<PartiesResponse>> Disband(Guid gameInstanceId, Guid partyId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var (outcome, response) = await _parties.DisbandAsync(gameInstanceId, userId, partyId);
        return Respond(outcome, response, userId, $"disband {partyId}");
    }

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
