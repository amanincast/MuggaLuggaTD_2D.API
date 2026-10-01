using System.ComponentModel.DataAnnotations;
using MuggaLuggaTD_2D.API.Services;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>The Buy tab: every material queue and every piece of equipment, from every world.</summary>
public record BazaarBoardResponse(
    List<BazaarMaterialRow> Materials,
    List<BazaarEquipmentRow> Equipment,
    /// <summary>Gold this player's listings from this realm have earned and they have not collected.</summary>
    long Owed,
    /// <summary>How many of this player's listings from this realm are still for sale.</summary>
    int Listed,
    /// <summary>The house's share of a sale, so the client quotes it rather than assuming it.</summary>
    double Fee);

public record BazaarMineResponse(List<BazaarMyListing> Listings, long Owed);

public record BazaarListItemRequest([Required] string ItemId, [Required] string SharedContractVersion);

public record BazaarListMaterialRequest(
    [Required] string MaterialName, int Quantity, [Required] string SharedContractVersion);

/// <summary>A purchase names the price the buyer was shown, so a repriced item is refused, not charged.</summary>
public record BazaarBuyItemRequest(Guid ListingId, long? QuotedPrice, [Required] string SharedContractVersion);

public record BazaarBuyMaterialRequest(
    [Required] string MaterialName, int Quantity, long? QuotedUnitPrice, [Required] string SharedContractVersion);

/// <summary>
/// What any Bazaar action did. <c>Gold</c> is the purse in this realm afterwards, so the client never
/// adds a figure to a balance it read before the trade.
/// </summary>
public record BazaarTradeResponse(
    long Gold,
    long Spent = 0,
    int Quantity = 0,
    /// <summary>Equipment bought, or pulled back: the client adds it to its inventory.</summary>
    ItemSaveData? Item = null,
    Guid? ListingId = null,
    int MaterialsReturned = 0,
    long Collected = 0);
