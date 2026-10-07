using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One siege by an NPC faction (<c>docs/design/npc-factions.md</c> phase 3). The twin of
/// <see cref="Siege"/>, which a faction cannot sit in because its attacker is a user.
///
/// <para>Only <see cref="SiegeState.Mustering"/> is live: nobody fights a faction's assault, so the
/// server settles it the moment the muster closes, to <see cref="SiegeState.Won"/> or
/// <see cref="SiegeState.Repelled"/>. <see cref="SiegeState.Cancelled"/> means the region changed
/// hands under it.</para>
/// </summary>
public class FactionSiege
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    public int SeasonNumber { get; set; }

    /// <summary>The besieging faction.</summary>
    public FactionId Faction { get; set; }

    [Required]
    [MaxLength(32)]
    public string RegionId { get; set; } = string.Empty;

    /// <summary>
    /// Who held the region when it was declared: a player's id, or "faction:{name}" when it was
    /// another faction's land (phase 4), as the war log names a faction. If that changes, it is cancelled.
    /// </summary>
    [Required]
    [MaxLength(450)]
    public string DefenderUserId { get; set; } = string.Empty;

    /// <summary>What the faction marched with, frozen at declaration.</summary>
    public double March { get; set; }

    public SiegeState State { get; set; } = SiegeState.Mustering;

    public DateTime DeclaredAt { get; set; }

    public DateTime MusterEndsAt { get; set; }

    /// <summary>The region's hold when the muster closed. Null until then.</summary>
    public long? FrozenHold { get; set; }

    /// <summary>The server's d20 at muster close; 0 when the hold had passed the gate and no roll was made.</summary>
    public int D20Roll { get; set; }

    /// <summary>Champions taken when the region fell.</summary>
    public int Captured { get; set; }

    public DateTime? ResolvedAt { get; set; }

    // -----------------------------------------------------------------
    // Break the siege: the defender's one sortie
    // -----------------------------------------------------------------

    /// <summary>The sortie's run, once the defender has begun it. One per siege.</summary>
    public Guid? SortieRunId { get; set; }

    public DateTime? SortieStartedAt { get; set; }

    /// <summary>The party that sallied out, as a JSON array of character ids.</summary>
    [Required]
    public string SortieArmyJson { get; set; } = "[]";

    public double SortiePower { get; set; }

    public int SortieEnemyLevel { get; set; }

    public int SortieWaves { get; set; }

    /// <summary>Null until the sortie is reported; then whether it broke the siege.</summary>
    public bool? SortieWon { get; set; }

    [NotMapped]
    public bool IsLive => State == SiegeState.Mustering;

    /// <summary>The defending faction, when the land was another faction's; <see cref="FactionId.None"/> for a player.</summary>
    [NotMapped]
    public FactionId DefenderFaction =>
        DefenderUserId.StartsWith(FactionPrefix, StringComparison.Ordinal)
        && Enum.TryParse<FactionId>(DefenderUserId.Substring(FactionPrefix.Length), out var faction)
            ? faction
            : FactionId.None;

    /// <summary>The prefix a faction's id wears where a user id would be (as <c>FactionService.WarLogPrefix</c>).</summary>
    private const string FactionPrefix = "faction:";

    /// <summary>Broken by the defender's sortie, rather than turned away at the walls.</summary>
    [NotMapped]
    public bool Broken => State == SiegeState.Repelled && SortieWon == true;
}
