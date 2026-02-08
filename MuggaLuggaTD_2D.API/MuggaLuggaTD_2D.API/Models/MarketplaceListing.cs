using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

public enum ListingStatus
{
    Active,
    Sold,
    Cancelled,
    Expired
}

public class MarketplaceListing
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    [Required]
    public string SellerId { get; set; } = string.Empty;

    [ForeignKey(nameof(SellerId))]
    public ApplicationUser Seller { get; set; } = null!;

    public string? BuyerId { get; set; }

    [ForeignKey(nameof(BuyerId))]
    public ApplicationUser? Buyer { get; set; }

    [Column(TypeName = "jsonb")]
    public string ItemData { get; set; } = "{}";

    [Column(TypeName = "jsonb")]
    public string PurchaseConditions { get; set; } = "{}";

    public ListingStatus Status { get; set; } = ListingStatus.Active;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
