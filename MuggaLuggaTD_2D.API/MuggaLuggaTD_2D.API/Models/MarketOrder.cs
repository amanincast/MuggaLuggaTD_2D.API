using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MuggaLuggaTD_2D.API.Data;

namespace MuggaLuggaTD_2D.API.Models;

public enum MarketOrderStatus
{
    /// <summary>Waiting for sellers; its gold is held.</summary>
    Open,
    /// <summary>Every unit bought.</summary>
    Filled,
    /// <summary>Called off by the buyer; the gold left came back.</summary>
    Cancelled,
    /// <summary>Its 72 hours ran out, or its world reset; the gold left came back.</summary>
    Expired
}

/// <summary>
/// A buy order on the Crossroads Bazaar (Marketplace spec row 2, Mike: "a definite need"): "Wanted: 40
/// Timber". Materials and goods only. The gold for every unit is held when it is posted, at the assay
/// price of that moment, and it fills, oldest order first, as sellers list. Filled units go straight into
/// the buyer's wallet in <see cref="GameInstanceId"/>, the realm they posted from.
/// </summary>
public class MarketOrder : IRevisioned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The buyer's realm: the gold is held from it and the goods arrive in it.</summary>
    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    [Required]
    public string BuyerId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string MaterialName { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public int QuantityFilled { get; set; }

    /// <summary>The price of one unit, fixed when it was posted (M3: an order keeps its price).</summary>
    public long UnitPrice { get; set; }

    /// <summary>Gold still held for the units not yet bought.</summary>
    public long HeldGold { get; set; }

    public MarketOrderStatus Status { get; set; } = MarketOrderStatus.Open;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(MuggaLuggaTD.Shared.Gameplay.BazaarAssay.ListingHours);

    [NotMapped]
    public int QuantityLeft => Math.Max(0, Quantity - QuantityFilled);

    [ConcurrencyCheck]
    public long Revision { get; set; }
}

/// <summary>
/// One sale on the Bazaar, kept for the demand line (Marketplace M1: "38 sold this week"). Prices are the
/// assay's, so what is worth recording is how much moved, not what it went for.
/// </summary>
public class MarketSale
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public ListingKind Kind { get; set; }

    /// <summary>A material's name; for gear, empty (gear is counted by its type and rarity).</summary>
    [MaxLength(200)]
    public string GoodsKey { get; set; } = string.Empty;

    /// <summary>Gear: its <c>ItemTypes</c>, as an int.</summary>
    public int? ItemType { get; set; }

    /// <summary>Gear: its <c>ItemRarityTypes</c>, as an int.</summary>
    public int? Rarity { get; set; }

    public int Quantity { get; set; }

    public long UnitPrice { get; set; }

    public DateTime SoldAt { get; set; } = DateTime.UtcNow;

    /// <summary>What it was called, for the sales log ("Cinder Blade", "Lesser Essence").</summary>
    [MaxLength(200)]
    public string GoodsName { get; set; } = string.Empty;

    /// <summary>The seller and the realm the listing came from: the sales log's "sold".</summary>
    public string? SellerId { get; set; }
    public Guid? SellerRealm { get; set; }

    /// <summary>The buyer and the realm they paid from: the sales log's "bought".</summary>
    public string? BuyerId { get; set; }
    public Guid? BuyerRealm { get; set; }
}
