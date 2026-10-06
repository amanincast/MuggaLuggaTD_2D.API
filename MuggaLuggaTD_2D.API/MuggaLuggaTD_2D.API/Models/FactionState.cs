using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// A faction's manpower in one realm (<c>docs/design/npc-factions.md</c> §3). One row per faction
/// per realm, made on first read at full strength and settled lazily from
/// <see cref="SettledAtUtc"/> by <c>FactionStrengthRules</c>; nothing ticks it. A new season's map
/// makes the factions anew.
/// </summary>
public class FactionState
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    public FactionId Faction { get; set; }

    /// <summary>Strength as of <see cref="SettledAtUtc"/>.</summary>
    public double Strength { get; set; }

    public DateTime SettledAtUtc { get; set; }

    /// <summary>Set by a lost attack; a time in the past is simply recovered.</summary>
    public DateTime? BloodiedUntilUtc { get; set; }

    /// <summary>When it last marched or built (phase 2 onwards).</summary>
    public DateTime? LastActedAtUtc { get; set; }
}
