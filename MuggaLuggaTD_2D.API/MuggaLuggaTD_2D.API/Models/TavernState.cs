using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One player's standing at the Tavern in one realm, beyond the board itself.
///
/// <para>Today that is one number: how many refreshes they have bought since they last cleared a
/// dungeon. It exists because a flat refresh price is no gate at all for a rich player — enough gold
/// buys enough rolls to fish for an exact character, which would make lures pointless. Each refresh
/// costs double the last, and <b>a dungeon puts it back to nothing</b>.</para>
///
/// <para>That reset is the design rather than a convenience: the escalation is not a punishment for
/// refreshing, it is a pull back toward playing. A cheap board is always available to a player
/// willing to go and clear something for it, which is what the Tavern has been attached to since it
/// was built.</para>
/// </summary>
public class TavernState
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

    /// <summary>
    /// Paid refreshes bought since the last claimed dungeon or portal. Prices the next one, and is
    /// set back to zero by a clear.
    /// </summary>
    public int RefreshesSinceClear { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
