using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// How much gold a player holds in one realm, and what their land is paying them per hour.
///
/// <para><b>Why this is not a count like a material.</b> Materials only ever change when something
/// happens — a run is claimed, a recruit is hired. Gold also accrues from ground held, which means
/// the balance is a function of time as well as of events. So it is stored the way a season score
/// is: what has been settled, the rate at settlement, and when that was. What the player has
/// <i>now</i> is <c>SettledGold + elapsed x GoldPerHour</c>, computed on the spot.</para>
///
/// <para><b>Nothing ticks.</b> There is no scheduler and no per-hour job. Whenever something changes
/// what a player holds, everyone in the realm is settled at their old rate and re-rated — the same
/// machinery, and in fact the same call, that keeps season scores correct.</para>
///
/// <para>Per realm rather than per account, like materials, because a realm is the unit everything
/// contested is scoped to. Unlike a season score it <b>survives a season reset</b>: the scoreboard
/// belongs to the season, but the purse belongs to the player.</para>
/// </summary>
public class PlayerGold
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

    /// <summary>Gold banked up to <see cref="LastSettledAt"/>, from both clears and holdings.</summary>
    public double SettledGold { get; set; }

    /// <summary>What their holdings were paying per hour as of <see cref="LastSettledAt"/>.</summary>
    public double GoldPerHour { get; set; }

    /// <summary>When the purse was last brought up to date.</summary>
    public DateTime LastSettledAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Total gold ever earned from holding ground, kept apart from the balance so spending can
    /// never make it look as though the land paid less than it did.
    /// </summary>
    public double LifetimeFromHoldings { get; set; }

    /// <summary>Total gold ever earned by clearing sites, kept apart for the same reason.</summary>
    public double LifetimeFromClears { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
