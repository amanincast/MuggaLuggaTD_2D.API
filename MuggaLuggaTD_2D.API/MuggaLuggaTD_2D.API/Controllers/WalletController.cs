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
    private readonly GoldService _gold;
    private readonly ISessionLog _sessionLog;

    public WalletController(
        ApplicationDbContext context, MaterialWalletService wallet, GoldService gold,
        ISessionLog sessionLog)
    {
        _context = context;
        _wallet = wallet;
        _gold = gold;
        _sessionLog = sessionLog;
    }

    [HttpGet]
    public async Task<ActionResult<WalletResponse>> Read(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await HasAccessToGameInstance(gameInstanceId, userId)) return Forbid();

        return Ok(await BuildAsync(gameInstanceId, userId));
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

        return Ok(await BuildAsync(gameInstanceId, userId));
    }

    /// <summary>
    /// Materials and gold together, because the client wants them at the same moments — entering a
    /// realm, and after a claim — and a second round trip would only let the two drift apart on
    /// screen.
    /// </summary>
    private async Task<WalletResponse> BuildAsync(Guid gameInstanceId, string userId)
    {
        var rows = await _wallet.ReadAsync(gameInstanceId, userId);
        var purse = await _gold.ReadAsync(gameInstanceId, userId);

        long gold = await _gold.BalanceAsync(gameInstanceId, userId);

        return new WalletResponse(
            rows.Select(r => new MaterialBalance(r.MaterialName, r.Quantity)).ToList(),
            gold,
            purse?.GoldPerHour ?? 0);
    }

    private async Task<bool> HasAccessToGameInstance(Guid gameInstanceId, string userId)
        => await _context.GameInstances.AnyAsync(g => g.Id == gameInstanceId &&
            (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
}
