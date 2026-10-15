using System.ComponentModel.DataAnnotations;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// What is stored about one wanted champion (Active Content A): who claimed it, and a debug pin. Who it
/// is and where it walks are not stored: <see cref="BountyRules"/> derives them from the realm and the
/// slot. A row exists only once a champion is claimed or pinned; one per realm, season and slot.
/// </summary>
public class Bounty : IRevisioned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GameInstanceId { get; set; }

    public int SeasonNumber { get; set; }

    /// <summary>The champion's slot (<see cref="BountyRules.SlotOf"/>).</summary>
    public long Slot { get; set; }

    /// <summary>Debug only (Combat Debug → Bounties): the champion stands here instead of on its path.</summary>
    [MaxLength(32)]
    public string? PinnedRegionId { get; set; }

    [MaxLength(450)]
    public string? ClaimedByUserId { get; set; }

    public DateTime? ClaimedAt { get; set; }

    /// <summary>Where it was brought down.</summary>
    [MaxLength(32)]
    public string? ClaimedInRegionId { get; set; }

    [ConcurrencyCheck]
    public long Revision { get; set; }
}
