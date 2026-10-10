using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// What the ledger says a player holds (Hardening 6). A bought or rewarded item is granted on the
/// server and added to the client's inventory, which saves later; a client that crashed between
/// the two had a grant with no item, and saves only ever drop what the ledger lacks, never add.
/// The Hall reads this on entry and puts back whatever is missing.
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/items")]
[Authorize]
public class ItemsController : ControllerBase
{
    private readonly ItemLedgerService _items;
    private readonly RealmMembershipService _membership;

    public ItemsController(ItemLedgerService items, RealmMembershipService membership)
    {
        _items = items;
        _membership = membership;
    }

    [HttpGet("held")]
    public async Task<IActionResult> Held(Guid gameInstanceId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized();
        if (!await _membership.IsMemberAsync(gameInstanceId, userId)) return Forbid();

        await _items.EnsureAdoptedAsync(gameInstanceId, userId);
        var held = await _items.HeldAsync(gameInstanceId, userId);
        return Ok(new { items = held.Select(g => JsonSerializer.Deserialize<JsonElement>(g.ItemJson)).ToList() });
    }
}
