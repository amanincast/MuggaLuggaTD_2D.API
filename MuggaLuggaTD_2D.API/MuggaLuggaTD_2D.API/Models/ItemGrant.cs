using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One piece of equipment the server handed out, and who holds it now.
///
/// <para><b>Why a ledger.</b> The inventory lives in the client's save, and until this every item in
/// it was taken at face value: a save could add a Legendary sword, or raise a common one's stats, and
/// the server would price PvP from the result. The server already rolls every drop at claim time, so
/// it has the truth; this records it. A save is then reconciled against the ledger
/// (<c>ItemLedgerService.ReconcileSave</c>): an item the ledger knows is written back to what was
/// granted, and an item it does not know is dropped - the same treatment materials got, one step on.</para>
///
/// <para><see cref="ItemJson"/> is the item exactly as granted (an <c>ItemSaveData</c>), less the
/// fields that are the player's own business: which character wears it. The item's
/// <see cref="ItemId"/> is the id the save refers to it by, chosen by the server when it rolled it.</para>
/// </summary>
public class ItemGrant
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    /// <summary>Who holds it. Moves on a marketplace sale.</summary>
    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The item's own id, as the save knows it. Unique in a realm.</summary>
    [Required]
    [MaxLength(64)]
    public string ItemId { get; set; } = string.Empty;

    /// <summary>The item as granted, serialized <c>ItemSaveData</c>.</summary>
    [Required]
    public string ItemJson { get; set; } = "{}";

    /// <summary>Where it came from: "pve-claim run=…", "ambush-claim …", "adopted", "marketplace …".</summary>
    [MaxLength(200)]
    public string Source { get; set; } = string.Empty;

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Set while the item is up for sale. It is out of the holder's inventory then - a listed item
    /// that stayed wearable could be sold and still worn.
    /// </summary>
    public Guid? ListingId { get; set; }
}

/// <summary>
/// When a player's items came under the ledger. Before the ledger existed nothing was recorded, so
/// the first time it meets a player it <b>adopts</b> what their stored save already holds - the save
/// the server last accepted, never the one arriving - and from then on only grants count.
/// </summary>
public class ItemLedgerState
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    public DateTime AdoptedAt { get; set; } = DateTime.UtcNow;

    /// <summary>How many items the adoption took in, for the log and for curiosity.</summary>
    public int AdoptedCount { get; set; }
}
