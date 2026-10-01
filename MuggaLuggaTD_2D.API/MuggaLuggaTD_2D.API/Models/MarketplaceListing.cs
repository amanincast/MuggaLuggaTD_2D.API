using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

public enum ListingStatus
{
    /// <summary>On the Bazaar, some or all of it still for sale.</summary>
    Active,
    /// <summary>Every unit sold. Kept while the seller still has gold to collect.</summary>
    Sold,
    /// <summary>Pulled back by the seller; what was unsold went back to them.</summary>
    Cancelled,
    /// <summary>Its world reset while it was listed; what was unsold is gone (Mike, 2026-10-01).</summary>
    Expired
}

/// <summary>What is being sold: one piece of equipment, or a stack of a material.</summary>
public enum ListingKind
{
    Equipment,
    Material
}

/// <summary>
/// One seller's goods on the Crossroads Bazaar (design 12d).
///
/// <para><b>The Bazaar is cross-world</b>: a listing is seen and bought from every realm, and
/// <see cref="GameInstanceId"/> is the realm it came <i>from</i> — the one its seller is paid in, and
/// the one whose reset destroys it. The buyer pays from the realm they are standing in.</para>
///
/// <para><b>No price is stored.</b> The Assay (<c>BazaarAssay</c>) prices goods from what they are,
/// so the price is worked out whenever it is needed and one item's queue never holds two prices.</para>
///
/// <para><b>Materials share a queue.</b> A buyer asks for a quantity of a material and is sold it
/// from the oldest listings first, across every seller and world. Equipment is unique, so each
/// listing is its own row and is bought whole.</para>
/// </summary>
public class MarketplaceListing
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The realm the goods came from: the seller is paid here, and its reset destroys them.</summary>
    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    [Required]
    public string SellerId { get; set; } = string.Empty;

    [ForeignKey(nameof(SellerId))]
    public ApplicationUser Seller { get; set; } = null!;

    /// <summary>Who bought it, for equipment. A material's buyers are many and are not recorded here.</summary>
    public string? BuyerId { get; set; }

    [ForeignKey(nameof(BuyerId))]
    public ApplicationUser? Buyer { get; set; }

    /// <summary>The realm the last buyer paid from, so the seller can be told where it went.</summary>
    public Guid? BuyerGameInstanceId { get; set; }

    public ListingKind Kind { get; set; }

    /// <summary>The queue it stands in: a material's name, or a piece of equipment's item id.</summary>
    [Required]
    [MaxLength(200)]
    public string GoodsKey { get; set; } = string.Empty;

    /// <summary>What a player calls it, for search and for the row.</summary>
    [Required]
    [MaxLength(200)]
    public string GoodsName { get; set; } = string.Empty;

    /// <summary>Equipment: the ledger's copy of the item, exactly as granted. A material: "{}".</summary>
    [Column(TypeName = "jsonb")]
    public string ItemData { get; set; } = "{}";

    /// <summary>How many were listed. Always 1 for equipment.</summary>
    public int Quantity { get; set; } = 1;

    public int QuantitySold { get; set; }

    /// <summary>What the seller has earned so far, after the house's tenth.</summary>
    public long EarnedGold { get; set; }

    /// <summary>How much of <see cref="EarnedGold"/> the seller has taken into their purse.</summary>
    public long CollectedGold { get; set; }

    public ListingStatus Status { get; set; } = ListingStatus.Active;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public int QuantityLeft => Math.Max(0, Quantity - QuantitySold);

    [NotMapped]
    public long GoldOwed => Math.Max(0, EarnedGold - CollectedGold);
}
