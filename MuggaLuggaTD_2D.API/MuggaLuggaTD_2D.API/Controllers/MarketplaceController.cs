using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Controllers;

[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/marketplace")]
[Authorize]
public class MarketplaceController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public MarketplaceController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<MarketplaceListingListResponse>> GetAllListings(Guid gameInstanceId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!await HasAccessToGameInstance(gameInstanceId, userId))
        {
            return Forbid();
        }

        var listings = await _context.MarketplaceListings
            .Where(l => l.GameInstanceId == gameInstanceId && l.Status == ListingStatus.Active)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new MarketplaceListingSummary(
                l.Id, l.GameInstanceId, l.SellerId, l.BuyerId, l.Status, l.CreatedAt, l.UpdatedAt))
            .ToListAsync();

        return Ok(new MarketplaceListingListResponse(listings));
    }

    [HttpGet("search")]
    public async Task<ActionResult<MarketplaceListingListResponse>> SearchListings(
        Guid gameInstanceId,
        [FromQuery] ListingStatus? status,
        [FromQuery] string? sellerId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!await HasAccessToGameInstance(gameInstanceId, userId))
        {
            return Forbid();
        }

        var query = _context.MarketplaceListings
            .Where(l => l.GameInstanceId == gameInstanceId);

        if (status.HasValue)
        {
            query = query.Where(l => l.Status == status.Value);
        }

        if (!string.IsNullOrEmpty(sellerId))
        {
            query = query.Where(l => l.SellerId == sellerId);
        }

        var listings = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new MarketplaceListingSummary(
                l.Id, l.GameInstanceId, l.SellerId, l.BuyerId, l.Status, l.CreatedAt, l.UpdatedAt))
            .ToListAsync();

        return Ok(new MarketplaceListingListResponse(listings));
    }

    [HttpGet("{listingId:guid}")]
    public async Task<ActionResult<MarketplaceListingResponse>> GetListing(Guid gameInstanceId, Guid listingId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!await HasAccessToGameInstance(gameInstanceId, userId))
        {
            return Forbid();
        }

        var listing = await _context.MarketplaceListings
            .FirstOrDefaultAsync(l => l.GameInstanceId == gameInstanceId && l.Id == listingId);

        if (listing == null)
        {
            return NotFound(new { message = "Listing not found" });
        }

        var itemData = JsonSerializer.Deserialize<object>(listing.ItemData) ?? new { };
        var purchaseConditions = JsonSerializer.Deserialize<object>(listing.PurchaseConditions) ?? new { };

        return Ok(new MarketplaceListingResponse(
            listing.Id,
            listing.GameInstanceId,
            listing.SellerId,
            listing.BuyerId,
            itemData,
            purchaseConditions,
            listing.Status,
            listing.CreatedAt,
            listing.UpdatedAt));
    }

    [HttpGet("my-listings")]
    public async Task<ActionResult<MarketplaceListingListResponse>> GetMyListings(Guid gameInstanceId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!await HasAccessToGameInstance(gameInstanceId, userId))
        {
            return Forbid();
        }

        var listings = await _context.MarketplaceListings
            .Where(l => l.GameInstanceId == gameInstanceId && l.SellerId == userId)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new MarketplaceListingSummary(
                l.Id, l.GameInstanceId, l.SellerId, l.BuyerId, l.Status, l.CreatedAt, l.UpdatedAt))
            .ToListAsync();

        return Ok(new MarketplaceListingListResponse(listings));
    }

    [HttpPost]
    public async Task<ActionResult<MarketplaceListingResponse>> CreateListing(
        Guid gameInstanceId,
        [FromBody] CreateMarketplaceListingRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!await HasAccessToGameInstance(gameInstanceId, userId))
        {
            return Forbid();
        }

        var itemDataJson = JsonSerializer.Serialize(request.ItemData);
        var purchaseConditionsJson = request.PurchaseConditions != null
            ? JsonSerializer.Serialize(request.PurchaseConditions)
            : "{}";

        var listing = new MarketplaceListing
        {
            GameInstanceId = gameInstanceId,
            SellerId = userId,
            ItemData = itemDataJson,
            PurchaseConditions = purchaseConditionsJson,
            Status = ListingStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.MarketplaceListings.Add(listing);
        await _context.SaveChangesAsync();

        var responseItemData = request.ItemData;
        var responsePurchaseConditions = request.PurchaseConditions ?? new { };

        return CreatedAtAction(
            nameof(GetListing),
            new { gameInstanceId, listingId = listing.Id },
            new MarketplaceListingResponse(
                listing.Id,
                listing.GameInstanceId,
                listing.SellerId,
                listing.BuyerId,
                responseItemData,
                responsePurchaseConditions,
                listing.Status,
                listing.CreatedAt,
                listing.UpdatedAt));
    }

    [HttpPut("{listingId:guid}")]
    public async Task<ActionResult<MarketplaceListingResponse>> UpdateListing(
        Guid gameInstanceId,
        Guid listingId,
        [FromBody] UpdateMarketplaceListingRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var listing = await _context.MarketplaceListings
            .FirstOrDefaultAsync(l => l.GameInstanceId == gameInstanceId && l.Id == listingId);

        if (listing == null)
        {
            return NotFound(new { message = "Listing not found" });
        }

        // Only seller can update their listing
        if (listing.SellerId != userId)
        {
            return Forbid();
        }

        // Can only update active listings
        if (listing.Status != ListingStatus.Active)
        {
            return BadRequest(new { message = "Can only update active listings" });
        }

        if (request.ItemData != null)
        {
            listing.ItemData = JsonSerializer.Serialize(request.ItemData);
        }

        if (request.PurchaseConditions != null)
        {
            listing.PurchaseConditions = JsonSerializer.Serialize(request.PurchaseConditions);
        }

        listing.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var itemData = JsonSerializer.Deserialize<object>(listing.ItemData) ?? new { };
        var purchaseConditions = JsonSerializer.Deserialize<object>(listing.PurchaseConditions) ?? new { };

        return Ok(new MarketplaceListingResponse(
            listing.Id,
            listing.GameInstanceId,
            listing.SellerId,
            listing.BuyerId,
            itemData,
            purchaseConditions,
            listing.Status,
            listing.CreatedAt,
            listing.UpdatedAt));
    }

    [HttpPost("{listingId:guid}/cancel")]
    public async Task<ActionResult<MarketplaceListingResponse>> CancelListing(Guid gameInstanceId, Guid listingId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var listing = await _context.MarketplaceListings
            .FirstOrDefaultAsync(l => l.GameInstanceId == gameInstanceId && l.Id == listingId);

        if (listing == null)
        {
            return NotFound(new { message = "Listing not found" });
        }

        // Only seller can cancel their listing
        if (listing.SellerId != userId)
        {
            return Forbid();
        }

        if (listing.Status != ListingStatus.Active)
        {
            return BadRequest(new { message = "Can only cancel active listings" });
        }

        listing.Status = ListingStatus.Cancelled;
        listing.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var itemData = JsonSerializer.Deserialize<object>(listing.ItemData) ?? new { };
        var purchaseConditions = JsonSerializer.Deserialize<object>(listing.PurchaseConditions) ?? new { };

        return Ok(new MarketplaceListingResponse(
            listing.Id,
            listing.GameInstanceId,
            listing.SellerId,
            listing.BuyerId,
            itemData,
            purchaseConditions,
            listing.Status,
            listing.CreatedAt,
            listing.UpdatedAt));
    }

    [HttpPost("{listingId:guid}/purchase")]
    public async Task<ActionResult<MarketplaceListingResponse>> PurchaseListing(Guid gameInstanceId, Guid listingId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (!await HasAccessToGameInstance(gameInstanceId, userId))
        {
            return Forbid();
        }

        var listing = await _context.MarketplaceListings
            .FirstOrDefaultAsync(l => l.GameInstanceId == gameInstanceId && l.Id == listingId);

        if (listing == null)
        {
            return NotFound(new { message = "Listing not found" });
        }

        if (listing.Status != ListingStatus.Active)
        {
            return BadRequest(new { message = "Listing is not available for purchase" });
        }

        if (listing.SellerId == userId)
        {
            return BadRequest(new { message = "Cannot purchase your own listing" });
        }

        listing.BuyerId = userId;
        listing.Status = ListingStatus.Sold;
        listing.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var itemData = JsonSerializer.Deserialize<object>(listing.ItemData) ?? new { };
        var purchaseConditions = JsonSerializer.Deserialize<object>(listing.PurchaseConditions) ?? new { };

        return Ok(new MarketplaceListingResponse(
            listing.Id,
            listing.GameInstanceId,
            listing.SellerId,
            listing.BuyerId,
            itemData,
            purchaseConditions,
            listing.Status,
            listing.CreatedAt,
            listing.UpdatedAt));
    }

    private string? GetUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    private async Task<bool> HasAccessToGameInstance(Guid gameInstanceId, string userId)
    {
        return await _context.GameInstances
            .AnyAsync(g => g.Id == gameInstanceId &&
                (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));
    }
}
