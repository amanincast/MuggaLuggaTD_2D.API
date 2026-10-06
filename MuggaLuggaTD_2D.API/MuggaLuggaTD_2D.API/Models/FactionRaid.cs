using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One raid by an NPC faction (<c>docs/design/npc-factions.md</c> phase 2). A faction is not a user,
/// so it cannot sit in <see cref="RegionRaid"/>; this is that table's twin. It holds the same 4h
/// cooldown per attacker per region (<c>RaidResolver.Cooldown</c>) and the record of who hit whom.
/// </summary>
public class FactionRaid
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    /// <summary>The raiding faction.</summary>
    public FactionId Faction { get; set; }

    [Required]
    [MaxLength(32)]
    public string RegionId { get; set; } = string.Empty;

    /// <summary>The player holding the region when it was raided, if a player did.</summary>
    [MaxLength(450)]
    public string? DefenderUserId { get; set; }

    /// <summary>The faction holding it, if a faction did.</summary>
    public FactionId DefenderFaction { get; set; }

    public DateTime RaidedAt { get; set; }

    public bool AttackerWon { get; set; }

    public double March { get; set; }

    public long Hold { get; set; }

    public int ResolveDamage { get; set; }

    public int ResolveAfter { get; set; }
}
