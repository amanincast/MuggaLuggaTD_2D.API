using System.ComponentModel.DataAnnotations;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.DTOs;

public record CreateMarketplaceListingRequest(
    [Required] object ItemData,
    object? PurchaseConditions = null
);

public record UpdateMarketplaceListingRequest(
    object? ItemData = null,
    object? PurchaseConditions = null
);

public record SearchMarketplaceRequest(
    ListingStatus? Status = null,
    string? SellerId = null,
    int Page = 1,
    int PageSize = 20
);

public record MarketplaceListingResponse(
    Guid Id,
    Guid GameInstanceId,
    string SellerId,
    string? BuyerId,
    object ItemData,
    object PurchaseConditions,
    ListingStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record MarketplaceListingListResponse(
    IEnumerable<MarketplaceListingSummary> Listings
);

public record MarketplaceListingSummary(
    Guid Id,
    Guid GameInstanceId,
    string SellerId,
    string? BuyerId,
    ListingStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
