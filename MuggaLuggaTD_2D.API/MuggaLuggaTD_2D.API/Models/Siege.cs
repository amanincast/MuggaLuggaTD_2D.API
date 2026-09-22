using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>Where a siege is in its life. Only <see cref="Mustering"/> and <see cref="Assault"/> are live.</summary>
public enum SiegeState
{
    /// <summary>Declared. The defender's window to reinforce, up to eight hours.</summary>
    Mustering = 0,

    /// <summary>Muster has closed and the region's hold is frozen. The attacker must now show.</summary>
    Assault = 1,

    /// <summary>The attacker took the region.</summary>
    Won = 2,

    /// <summary>The defender held.</summary>
    Repelled = 3,

    /// <summary>The attacker never came. The defender holds, and the attacker may not re-declare for a while.</summary>
    Lapsed = 4,

    /// <summary>
    /// The siege stopped meaning anything - the region changed hands under it, or the season ended.
    /// Not the attacker's fault, so it costs them no cooldown.
    /// </summary>
    Cancelled = 5
}

/// <summary>
/// One player's siege of one rival region (see <c>docs/design/siege.md</c> §4).
///
/// <para>Declaring locks the attacking army for as long as the siege is live: those champions
/// cannot raid, garrison or run dungeons until it ends. That is the cost of declaring, and it is
/// also what enforces "one siege per attacker" - you have one army.</para>
/// </summary>
public class Siege
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    /// <summary>Which season it was declared in. A reset clears every siege with the world it referred to.</summary>
    public int SeasonNumber { get; set; }

    [Required]
    public string AttackerUserId { get; set; } = string.Empty;

    [ForeignKey(nameof(AttackerUserId))]
    public ApplicationUser Attacker { get; set; } = null!;

    /// <summary>Who held the region when the siege was declared. If that changes, the siege is cancelled.</summary>
    [Required]
    public string DefenderUserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string RegionId { get; set; } = string.Empty;

    /// <summary>
    /// The locked army, as a JSON array of character ids. Snapshotted at declaration: the army that
    /// cleared the gate is the army that is committed.
    /// </summary>
    [Required]
    public string ArmyCharacterIdsJson { get; set; } = "[]";

    /// <summary>The army's power when it was declared, computed by the server from the saved roster.</summary>
    public double MarchingPower { get; set; }

    public SiegeState State { get; set; } = SiegeState.Mustering;

    public DateTime DeclaredAt { get; set; } = DateTime.UtcNow;

    /// <summary>When muster closes. Brought forward if the defender declares ready.</summary>
    public DateTime MusterEndsAt { get; set; }

    /// <summary>When the attacker's window to assault closes. Moves with <see cref="MusterEndsAt"/>.</summary>
    public DateTime AssaultEndsAt { get; set; }

    /// <summary>
    /// The region's hold as it stood when muster closed. Null until then.
    ///
    /// <para>Frozen so the attacker choosing their moment costs the defender nothing mechanical, and
    /// so the defender cannot pile on reinforcements after the window that was theirs has shut.</para>
    /// </summary>
    public long? FrozenHold { get; set; }

    /// <summary>When the siege reached a final state.</summary>
    public DateTime? ResolvedAt { get; set; }

    [NotMapped]
    public bool IsLive => State == SiegeState.Mustering || State == SiegeState.Assault;
}
