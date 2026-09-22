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
/// A player's material balances in one realm.
///
/// <para>Materials are granted by run claims and spent here. They are deliberately <b>not</b> part of
/// the save blob: design doc 05 makes them the Tavern's currency, and a balance the client writes is
/// a balance the client can mint.</para>
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/wallet")]
[Authorize]
public class WalletController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly MaterialWalletService _wallet;
    private readonly ISessionLog _sessionLog;

    public WalletController(
        ApplicationDbContext context, MaterialWalletService wallet, ISessionLog sessionLog)
    {
        _context = context;
        _wallet = wallet;
        _sessionLog = sessionLog;
    }

    [HttpGet]
    public async Task<ActionResult<WalletResponse>> Read(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        var rows = await _wallet.ReadAsync(gameInstanceId, userId);

        return Ok(new WalletResponse(
            rows.Select(r => new MaterialBalance(r.MaterialName, r.Quantity)).ToList()));
    }

    [HttpPost("spend")]
    public async Task<ActionResult<WalletResponse>> Spend(
        Guid gameInstanceId, [FromBody] WalletSpendRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        if (request.SharedContractVersion != SharedContract.Version)
        {
            return Conflict(new
            {
                error = "Shared contract version mismatch.",
                expected = SharedContract.Version,
                received = request.SharedContractVersion
            });
        }

        var outcome = await _wallet.SpendAsync(
            gameInstanceId, userId, request.Materials, request.Reason ?? "unspecified");

        if (!outcome.Succeeded)
        {
            return outcome.Error switch
            {
                WalletError.InsufficientMaterials => Conflict(new { error = outcome.Message }),
                _ => BadRequest(new { error = outcome.Message })
            };
        }

        var rows = await _wallet.ReadAsync(gameInstanceId, userId);
        return Ok(new WalletResponse(
            rows.Select(r => new MaterialBalance(r.MaterialName, r.Quantity)).ToList()));
    }

    private async Task<bool> HasAccessToGameInstance(Guid gameInstanceId, string userId)
        => await _context.GameInstances.AnyAsync(g => g.Id == gameInstanceId &&
            (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
}
