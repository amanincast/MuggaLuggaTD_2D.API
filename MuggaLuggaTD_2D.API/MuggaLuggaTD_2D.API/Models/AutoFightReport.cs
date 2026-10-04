using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One fight a company in auto mode had (docs/design/auto-fight.md §5): what it won, what it ate, and
/// who fought. Its rows make the "While you were away" card.
///
/// <para><b>Banked until the client collects it.</b> Gold and materials are the server's and are paid
/// when the fight is settled. Experience and gear live in the player's save, which only the client
/// writes, so they wait here: the client adds them to its characters and inventory, saves, then
/// marks the rows collected. The gear is already in the item ledger, so the save that carries it is
/// accepted.</para>
/// </summary>
public class AutoFightReport
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [ForeignKey(nameof(UserId))]
    public ApplicationUser User { get; set; } = null!;

    public Guid PartyId { get; set; }

    [MaxLength(64)]
    public string PartyName { get; set; } = string.Empty;

    /// <summary>Where it was fought: a site, or for a patrol's skirmish the region's keep.</summary>
    [MaxLength(64)]
    public string SiteId { get; set; } = string.Empty;

    /// <summary>A site fight, or a patrol's skirmish.</summary>
    public bool Skirmish { get; set; }

    /// <summary>A skirmish forced on it by an ambush on the road (<see cref="SiteId"/> is where it was bound).</summary>
    public bool Ambush { get; set; }

    public int Level { get; set; }

    /// <summary>When the fight ended, in the company's replayed day.</summary>
    public DateTime At { get; set; }

    public bool Won { get; set; }

    /// <summary>Who fought, as a JSON array: the experience is theirs.</summary>
    [Required]
    public string FighterIdsJson { get; set; } = "[]";

    public long Experience { get; set; }
    public long Gold { get; set; }

    /// <summary>The gear won (ItemSaveData), as JSON.</summary>
    [Required]
    public string ItemsJson { get; set; } = "[]";

    /// <summary>The materials won and the goods eaten (MaterialGrant), as JSON.</summary>
    [Required]
    public string MaterialsJson { get; set; } = "[]";

    [Required]
    public string ProvisionsJson { get; set; } = "[]";

    /// <summary>When the client took the experience and gear into its save; null until then.</summary>
    public DateTime? CollectedAt { get; set; }
}
