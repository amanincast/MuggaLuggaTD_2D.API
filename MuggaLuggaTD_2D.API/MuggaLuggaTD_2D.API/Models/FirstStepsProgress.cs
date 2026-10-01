using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One player's First Steps on one world (design 12c): which of the six they have done, and whether
/// they have opened the Rare chest for doing all of them.
///
/// <para>Per world, not per account (Mike, 2026-10-01): a veteran starting a new world earns the
/// same chest a newcomer does. The steps are recorded where they happen (a hire, a march, a cleared
/// dungeon, a garrison), so the chest cannot be talked out of the server.</para>
/// </summary>
public class FirstStepsProgress
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

    /// <summary>The <see cref="FirstStepsRules.Steps"/> done, comma-separated.</summary>
    [MaxLength(200)]
    public string Done { get; set; } = string.Empty;

    /// <summary>When the chest was opened; null until then. One chest per player per world.</summary>
    public DateTime? ChestOpenedAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public IReadOnlyList<string> DoneSteps =>
        Done.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
