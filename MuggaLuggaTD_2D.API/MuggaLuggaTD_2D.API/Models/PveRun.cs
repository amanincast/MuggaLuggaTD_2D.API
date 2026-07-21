using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// A player's attempt at a PvE location, opened when they enter combat and closed when they claim
/// the conquest.
///
/// The server cannot verify a real-time bullet-hell fight it does not simulate, so it cannot decide
/// whether the player won. What it can do is refuse to apply a conquest that has no corresponding
/// attempt: the claim must reference a run this player opened, at this location, that is still open
/// and not implausibly fast. That removes conquest-without-playing and conquest-replay, which were
/// both trivial when the client simply wrote the world blob itself.
/// </summary>
public class PveRun
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

    /// <summary>The world location this run was opened against.</summary>
    [Required]
    public string LocationId { get; set; } = string.Empty;

    /// <summary>
    /// The location's type when the run started. The claim re-reads the live world rather than
    /// trusting this, but it is kept for auditing what the player actually entered.
    /// </summary>
    public int LocationType { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Null while the run is open. Set once a conquest has been claimed against it.</summary>
    public DateTime? ClaimedAt { get; set; }
}
