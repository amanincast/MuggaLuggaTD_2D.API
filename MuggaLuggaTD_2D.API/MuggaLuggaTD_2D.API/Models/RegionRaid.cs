using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One raid, by one player, against one region.
///
/// <para>Kept for two reasons. The first is the cooldown: an attacker may raid a given region once
/// every few hours, and <b>that rate limit is the anti-cheat</b>. The server cannot referee the
/// fight, so instead it bounds what winning one is worth — a forged raid buys a few points of the
/// defender's resolve and then the attacker has to wait, in full view of the person they are
/// wearing down.</para>
///
/// <para>The second is the record. A defender who was asleep should be able to find out who has
/// been at their border and when, rather than inferring it from a number that moved.</para>
/// </summary>
public class RegionRaid
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    /// <summary>The attacker.</summary>
    [Required]
    public string UserId { get; set; } = string.Empty;

    [ForeignKey(nameof(UserId))]
    public ApplicationUser User { get; set; } = null!;

    [Required]
    public string RegionId { get; set; } = string.Empty;

    /// <summary>Who held the region when it was raided, so the log still makes sense after it changes hands.</summary>
    public string? DefenderUserId { get; set; }

    public DateTime RaidedAt { get; set; } = DateTime.UtcNow;

    /// <summary>False when the raid was repelled — it still costs the attacker their cooldown.</summary>
    public bool AttackerWon { get; set; }

    /// <summary>Resolve actually taken. Zero on a repelled raid, or when resolve was already at the floor.</summary>
    public int ResolveDamage { get; set; }

    /// <summary>The region's resolve after this raid, for reading the log back as a sequence.</summary>
    public int ResolveAfter { get; set; }
}
