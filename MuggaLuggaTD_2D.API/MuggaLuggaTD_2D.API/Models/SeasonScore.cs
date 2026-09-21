using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One player's running score in one season of one realm.
///
/// <para><b>Nothing ticks.</b> A score is stored as what has been settled, the rate the player was
/// earning at when it was settled, and when that was — so the points they have <i>now</i> are
/// <c>SettledPoints + elapsed x PointsPerHour</c>, computed on the spot. Whenever something changes
/// what a player holds, they are settled up at the old rate first and the new rate is written.</para>
///
/// <para>That is the whole of the accrual. There is no scheduler and no per-tick cost, the arithmetic
/// is identical whether the server was busy or idle, and a player who is offline for a week earns
/// exactly what they should.</para>
/// </summary>
public class SeasonScore
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

    /// <summary>Which season this score belongs to. A reset starts everyone at zero in a new one.</summary>
    public int SeasonNumber { get; set; } = 1;

    /// <summary>Points banked up to <see cref="LastSettledAt"/>, from both holdings and deeds.</summary>
    public double SettledPoints { get; set; }

    /// <summary>What their holdings were earning per hour as of <see cref="LastSettledAt"/>.</summary>
    public double PointsPerHour { get; set; }

    /// <summary>When the score was last brought up to date.</summary>
    public DateTime LastSettledAt { get; set; } = DateTime.UtcNow;

    // Kept apart from the total so the standings can show where a season was won, and so a rate
    // change can never retroactively alter what a deed paid.

    /// <summary>Points earned from holding ground, across the season.</summary>
    public double HoldingPoints { get; set; }

    /// <summary>Points earned from clearing sites.</summary>
    public double ClearingPoints { get; set; }

    /// <summary>Points earned from raiding and from repelling raids.</summary>
    public double RaidingPoints { get; set; }
}
