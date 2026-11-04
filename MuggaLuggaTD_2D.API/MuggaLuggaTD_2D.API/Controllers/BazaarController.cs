using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// The Crossroads Bazaar (design 12d). The route names the realm the player is <b>standing in</b>: it
/// is the purse a purchase is paid from and the one a sale is collected into. What is for sale comes
/// from every realm.
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/bazaar")]
[Authorize]
public class BazaarController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly BazaarService _bazaar;
    private readonly GoldService _gold;
    private readonly SeasonScoreService _seasons;
    private readonly IGameContentProvider _content;
    private readonly IWebHostEnvironment _environment;

    public BazaarController(ApplicationDbContext context, BazaarService bazaar, GoldService gold, SeasonScoreService seasons,
        IGameContentProvider content, IWebHostEnvironment environment)
    {
        _content = content;
        _environment = environment;
        _context = context;
        _bazaar = bazaar;
        _gold = gold;
        _seasons = seasons;
    }

    [HttpGet]
    public async Task<ActionResult<BazaarBoardResponse>> Board(Guid gameInstanceId)
    {
        var userId = UserId();
        if (userId == null) return Unauthorized();
        if (!await HasAccess(gameInstanceId, userId)) return Forbid();
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);
        await _bazaar.SweepAsync();

        var (materialDemand, gearDemand) = await _bazaar.DemandAsync();
        return Ok(new BazaarBoardResponse(
            await _bazaar.MaterialsAsync(userId),
            await _bazaar.EquipmentAsync(userId),
            await _bazaar.OwedAsync(gameInstanceId, userId),
            await _bazaar.ListedCountAsync(gameInstanceId, userId),
            BazaarAssay.Fee,
            await _bazaar.WantedAsync(userId),
            materialDemand,
            gearDemand,
            await _bazaar.OpenOrderCountAsync(gameInstanceId, userId),
            BazaarAssay.MaxListings,
            BazaarAssay.MaxOrders));
    }

    [HttpGet("mine")]
    public async Task<ActionResult<BazaarMineResponse>> Mine(Guid gameInstanceId)
    {
        var userId = UserId();
        if (userId == null) return Unauthorized();
        if (!await HasAccess(gameInstanceId, userId)) return Forbid();
        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);
        await _bazaar.SweepAsync();

        return Ok(new BazaarMineResponse(
            await _bazaar.MineAsync(gameInstanceId, userId),
            await _bazaar.OwedAsync(gameInstanceId, userId),
            await _bazaar.MyOrdersAsync(gameInstanceId, userId),
            await _bazaar.SalesLogAsync(gameInstanceId, userId)));
    }

    [HttpPost("list-item")]
    public async Task<ActionResult<BazaarTradeResponse>> ListItem(Guid gameInstanceId, [FromBody] BazaarListItemRequest request)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, request.SharedContractVersion);
        if (refusal != null) return refusal;

        var (outcome, listing) = await _bazaar.ListEquipmentAsync(gameInstanceId, userId!, request.ItemId);
        if (!outcome.Succeeded) return Refuse(outcome);
        return Ok(new BazaarTradeResponse(await _gold.BalanceAsync(gameInstanceId, userId!), ListingId: listing!.Id, Quantity: 1));
    }

    [HttpPost("list-material")]
    public async Task<ActionResult<BazaarTradeResponse>> ListMaterial(Guid gameInstanceId, [FromBody] BazaarListMaterialRequest request)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, request.SharedContractVersion);
        if (refusal != null) return refusal;

        var listed = await _bazaar.ListMaterialAsync(gameInstanceId, userId!, request.MaterialName, request.Quantity);
        if (!listed.Outcome.Succeeded) return Refuse(listed.Outcome);
        return Ok(new BazaarTradeResponse(await _gold.BalanceAsync(gameInstanceId, userId!), ListingId: listed.Listing!.Id,
            Quantity: listed.Listing.Quantity, Filled: listed.SoldToOrders));
    }

    [HttpPost("buy-item")]
    public async Task<ActionResult<BazaarTradeResponse>> BuyItem(Guid gameInstanceId, [FromBody] BazaarBuyItemRequest request)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, request.SharedContractVersion);
        if (refusal != null) return refusal;

        var bought = await _bazaar.BuyEquipmentAsync(gameInstanceId, userId!, request.ListingId, request.QuotedPrice);
        if (!bought.Outcome.Succeeded) return Refuse(bought.Outcome);
        return Ok(new BazaarTradeResponse(await _gold.BalanceAsync(gameInstanceId, userId!), bought.Spent, 1, bought.Item,
            request.ListingId));
    }

    [HttpPost("buy-material")]
    public async Task<ActionResult<BazaarTradeResponse>> BuyMaterial(Guid gameInstanceId, [FromBody] BazaarBuyMaterialRequest request)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, request.SharedContractVersion);
        if (refusal != null) return refusal;

        var bought = await _bazaar.BuyMaterialAsync(gameInstanceId, userId!, request.MaterialName, request.Quantity,
            request.QuotedUnitPrice);
        if (!bought.Outcome.Succeeded) return Refuse(bought.Outcome);
        return Ok(new BazaarTradeResponse(await _gold.BalanceAsync(gameInstanceId, userId!), bought.Spent, bought.Quantity));
    }

    [HttpPost("orders")]
    public async Task<ActionResult<BazaarTradeResponse>> PostOrder(Guid gameInstanceId, [FromBody] BazaarPostOrderRequest request)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, request.SharedContractVersion);
        if (refusal != null) return refusal;

        // The buyer saw a price; an order is refused rather than held at another one.
        var material = _content.Materials.FirstOrDefault(m => m.MaterialName == request.MaterialName);
        if (material != null && request.QuotedUnitPrice.HasValue && request.QuotedUnitPrice.Value != BazaarAssay.PriceOf(material))
            return Conflict(new { error = $"The Assay now prices it at {BazaarAssay.PriceOf(material)}g." });

        var ordered = await _bazaar.PostOrderAsync(gameInstanceId, userId!, request.MaterialName, request.Quantity);
        if (!ordered.Outcome.Succeeded) return Refuse(ordered.Outcome);
        return Ok(new BazaarTradeResponse(await _gold.BalanceAsync(gameInstanceId, userId!), ordered.Order!.UnitPrice * ordered.Order.Quantity,
            ordered.Order.Quantity, Filled: ordered.Bought, OrderId: ordered.Order.Id));
    }

    [HttpPost("orders/{orderId:guid}/cancel")]
    public async Task<ActionResult<BazaarTradeResponse>> CancelOrder(Guid gameInstanceId, Guid orderId)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, SharedContract.Version);
        if (refusal != null) return refusal;

        var (outcome, refunded) = await _bazaar.CancelOrderAsync(gameInstanceId, userId!, orderId);
        if (!outcome.Succeeded) return Refuse(outcome);
        return Ok(new BazaarTradeResponse(await _gold.BalanceAsync(gameInstanceId, userId!), OrderId: orderId, Refunded: refunded));
    }

    [HttpPost("{listingId:guid}/pull-back")]
    public async Task<ActionResult<BazaarTradeResponse>> PullBack(Guid gameInstanceId, Guid listingId)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, SharedContract.Version);
        if (refusal != null) return refusal;

        var (outcome, item, materials) = await _bazaar.PullBackAsync(gameInstanceId, userId!, listingId);
        if (!outcome.Succeeded) return Refuse(outcome);
        return Ok(new BazaarTradeResponse(await _gold.BalanceAsync(gameInstanceId, userId!), Item: item, ListingId: listingId,
            MaterialsReturned: materials));
    }

    [HttpPost("collect")]
    public async Task<ActionResult<BazaarTradeResponse>> Collect(Guid gameInstanceId)
    {
        var (userId, refusal) = await GateAsync(gameInstanceId, SharedContract.Version);
        if (refusal != null) return refusal;

        var (collected, balance) = await _bazaar.CollectAsync(gameInstanceId, userId!);
        return Ok(new BazaarTradeResponse(balance, Collected: collected));
    }

    // -----------------------------------------------------------------
    // Debug hooks (Development only; Combat Debug → Bazaar)
    // -----------------------------------------------------------------

    [HttpPost("debug/seed")]
    public Task<IActionResult> DebugSeed(Guid gameInstanceId) =>
        DebugAsync(gameInstanceId, (realm, _) => _bazaar.DebugSeedAsync(realm));

    [HttpPost("debug/expire-mine")]
    public Task<IActionResult> DebugExpireMine(Guid gameInstanceId) =>
        DebugAsync(gameInstanceId, (realm, user) => _bazaar.DebugExpireMineAsync(realm, user));

    [HttpGet("debug/escrow")]
    public Task<IActionResult> DebugEscrow(Guid gameInstanceId) =>
        DebugAsync(gameInstanceId, (realm, user) => _bazaar.DebugEscrowAsync(realm, user));

    private async Task<IActionResult> DebugAsync(Guid gameInstanceId, Func<Guid, string, Task<string>> act)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = UserId();
        if (userId == null) return Unauthorized();
        if (!await HasAccess(gameInstanceId, userId)) return Forbid();
        return Ok(new { message = await act(gameInstanceId, userId) });
    }

    // -----------------------------------------------------------------

    /// <summary>
    /// Who is asking, whether they may trade from this realm, and whether they price goods by the
    /// same Assay. A season that has run out is closed first, so nobody trades into a dead world.
    /// </summary>
    private async Task<(string? UserId, ActionResult? Refusal)> GateAsync(Guid gameInstanceId, string? contractVersion)
    {
        var userId = UserId();
        if (userId == null) return (null, Unauthorized());
        if (!await HasAccess(gameInstanceId, userId)) return (null, Forbid());

        if (contractVersion != SharedContract.Version)
            return (null, Conflict(new
            {
                error = "Shared contract version mismatch.",
                expected = SharedContract.Version,
                received = contractVersion
            }));

        await _seasons.EnsureSeasonCurrentAsync(gameInstanceId);
        return (userId, null);
    }

    private ActionResult Refuse(BazaarOutcome outcome) => outcome.Error switch
    {
        BazaarError.NotYours => NotFound(new { error = outcome.Message }),
        BazaarError.NothingNamed or BazaarError.UnknownGoods or BazaarError.Equipped => BadRequest(new { error = outcome.Message }),
        _ => Conflict(new { error = outcome.Message })
    };

    private string? UserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private async Task<bool> HasAccess(Guid gameInstanceId, string userId)
        => await _context.GameInstances.AnyAsync(g => g.Id == gameInstanceId &&
            (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
}
