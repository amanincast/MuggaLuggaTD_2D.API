using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One player's quest board on one world (<c>docs/design/quests.md</c> §3): which set it is on, and for the
/// hour it was last touched, which offers they have taken and which givers they have looked at.
///
/// <para>The offers themselves are never stored: <see cref="QuestRules.Board"/> works them out from the hour
/// and <see cref="Set"/>. The taken and seen lists belong to one hour (<see cref="Hour"/>); in a new hour
/// the board is new, so they start empty.</para>
/// </summary>
public class QuestBoardState
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

    /// <summary>Raised each time a player finishes every quest on the board, for a fresh set at once.</summary>
    public int Set { get; set; }

    /// <summary>The hour (<see cref="QuestRules.HourOf"/>) the lists below are for.</summary>
    public long Hour { get; set; }

    /// <summary>The offer ids taken this hour, comma-separated.</summary>
    [MaxLength(2000)]
    public string Taken { get; set; } = string.Empty;

    /// <summary>The giver ids looked at this hour, comma-separated: no gold "?" over them.</summary>
    [MaxLength(2000)]
    public string Seen { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public IReadOnlyList<string> TakenIds => Split(Taken);

    [NotMapped]
    public IReadOnlyList<string> SeenIds => Split(Seen);

    private static IReadOnlyList<string> Split(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
