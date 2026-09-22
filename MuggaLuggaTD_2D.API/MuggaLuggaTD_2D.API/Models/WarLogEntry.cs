using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>What happened, in the war log's vocabulary. Stored by name, so reordering is harmless.</summary>
public enum WarLogKind
{
    RaidLanded,
    RaidRepelled,
    SiegeDeclared,

    /// <summary>The defender closed the muster early.</summary>
    SiegeReady,

    /// <summary>Muster closed and the hold froze: the attacker's window to assault is open.</summary>
    SiegeMusterClosed,

    SiegeAssaultBegun,
    SiegeWon,
    SiegeRepelled,
    SiegeLapsed,
    SiegeCancelled
}

/// <summary>
/// One line of a realm's war log (<c>docs/design/siege.md</c> §9).
///
/// <para>Contest in this game happens while the other side is asleep, by design: a defender never has
/// to be online for a raid or a siege. So a player coming back needs to be <i>told</i> what happened
/// while they were away - who raided them, who laid siege, what fell - rather than inferring it from
/// a number that moved or a border that changed colour.</para>
///
/// <para>Names are copied in at the time, so the log still reads correctly after someone renames
/// themselves or a region changes hands. Entries outlive the season they were written in; the read
/// path filters by season.</para>
/// </summary>
public class WarLogEntry
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    public int SeasonNumber { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// Real-clock ticks when the line was written. Breaks ties between events that share an
    /// <see cref="OccurredAt"/> - a defender declaring ready closes the muster in the same instant -
    /// so the log reads in the order things were done.
    /// </summary>
    public long RecordedTicks { get; set; }

    [Required]
    [MaxLength(32)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Who did it: the raider, the besieger, or the defender who closed the muster.</summary>
    public string? ActorUserId { get; set; }

    [MaxLength(64)]
    public string? ActorName { get; set; }

    /// <summary>Who it was done to.</summary>
    public string? SubjectUserId { get; set; }

    [MaxLength(64)]
    public string? SubjectName { get; set; }

    [MaxLength(32)]
    public string? RegionId { get; set; }

    /// <summary>A short, already-worded detail - "resolve 60 → 48", "2 champions taken".</summary>
    [MaxLength(200)]
    public string? Detail { get; set; }
}
