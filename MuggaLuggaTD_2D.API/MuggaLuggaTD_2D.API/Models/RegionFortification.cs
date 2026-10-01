using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

public enum FortificationState
{
    UnderWay = 0,
    Done = 1,

    /// <summary>The region changed hands before the works finished; the goods are not returned.</summary>
    Cancelled = 2
}

/// <summary>
/// Works raising a region's entrenchment by one (Hiring Hall phase 3). The world blob carries the
/// same works for every player to see (<c>FortifyingTo</c>); this row is what the sweep finds when
/// they are due, without parsing every realm's world.
/// </summary>
public class RegionFortification
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string RegionId { get; set; } = string.Empty;

    public int FromLevel { get; set; }
    public int ToLevel { get; set; }

    /// <summary>What was spent, "Stone:150,Timber:150", for the log and a future refund rule.</summary>
    [MaxLength(200)]
    public string Spent { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }
    public DateTime CompletesAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    public FortificationState State { get; set; }
}
