using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Enums;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One player's standing on one affinity at the Tavern: the crystal they have offered against it,
/// and how many lured boards have come back without it.
///
/// <para><b>A lure is placed before you go out, not clicked at the board.</b> There is no refresh
/// button and no timer — a dungeon is the only thing that brings new faces in — so a lure cannot be
/// "spend a crystal and re-roll". It is a standing offer, paid for when it is placed and spent by
/// the next restock. That turns out to be the better mechanic anyway: it is a commitment made before
/// the run rather than a button pressed after it.</para>
///
/// <para><b>Why the pity lives here rather than in its own table.</b> Pity is per affinity — a
/// player who lures Fire, misses, then lures Water is owed something on both — and it has to outlive
/// the lure that earned it. Keeping one row per affinity puts a player's standing on that affinity
/// in one place: what they have offered, and what they are owed.</para>
/// </summary>
public class TavernLure
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

    /// <summary>The affinity this row is about.</summary>
    public AffinityTypes Affinity { get; set; }

    /// <summary>
    /// The crystal currently offered, or <see cref="TavernRules.LureStrength.None"/> when none is
    /// standing. At most one row per player may be non-None; the service enforces that, because a
    /// restock spends one lure and two standing offers would leave one silently unspent.
    /// </summary>
    public TavernRules.LureStrength PendingStrength { get; set; } = TavernRules.LureStrength.None;

    /// <summary>When the standing lure was placed. Null when none is standing.</summary>
    public DateTime? PlacedAt { get; set; }

    /// <summary>
    /// How many <b>lured</b> restocks in a row have shown none of this affinity. Only lured boards
    /// count — a player who never spends a crystal accrues nothing, which is coherent: pity is what
    /// a lure buys when it does not pay off, not a reward for playing.
    /// </summary>
    public int MissedRestocks { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
