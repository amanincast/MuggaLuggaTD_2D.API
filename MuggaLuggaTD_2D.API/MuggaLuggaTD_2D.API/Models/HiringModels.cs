using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// A local looking for work on one player's Hiring Hall board (design 12e): six seats per player per
/// realm, like the Tavern's. Rolled by the server; hiring frees the seat.
/// </summary>
public class HiringCandidate
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

    public int Slot { get; set; }

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    public ResourceTrade Trade { get; set; }
    public WorkerTier Tier { get; set; }

    /// <summary>The <see cref="WorkerTrait"/>s, as their numbers, comma-separated.</summary>
    [MaxLength(64)]
    public string Traits { get; set; } = string.Empty;

    public ResourceTrade? SecondTrade { get; set; }
    public BiomeType HomeBiome { get; set; }
    public int Look { get; set; }
    public DateTime RolledAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A worker a player employs in one realm. Unassigned they sit in the Hall; assigned they work one
/// resource site in a region their employer holds, and gather <see cref="RatePerHour"/> goods an
/// hour since <see cref="LastSettledAt"/>.
///
/// <para>Nothing ticks: <see cref="RatePerHour"/> is set when the site's workforce changes (a
/// Foreman lifts everyone else there), and output is settled lazily into the material wallet, the
/// whole units paid and the fraction kept in <see cref="Carry"/>. <b>A season reset removes every
/// worker</b> (Mike, 2026-10-01): players hire again on the new map.</para>
/// </summary>
public class HiredWorker
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

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    public ResourceTrade Trade { get; set; }
    public WorkerTier Tier { get; set; }

    [MaxLength(64)]
    public string Traits { get; set; } = string.Empty;

    public ResourceTrade? SecondTrade { get; set; }
    public BiomeType HomeBiome { get; set; }
    public int Look { get; set; }
    public DateTime HiredAt { get; set; } = DateTime.UtcNow;

    /// <summary>The resource site they work; null while they are at the Hall.</summary>
    [MaxLength(64)]
    public string? SiteId { get; set; }

    public DateTime? AssignedAt { get; set; }

    /// <summary>Goods an hour where they stand, every trait applied. Zero at the Hall.</summary>
    public double RatePerHour { get; set; }

    public DateTime LastSettledAt { get; set; } = DateTime.UtcNow;

    /// <summary>The fraction of a unit gathered and not yet paid.</summary>
    public double Carry { get; set; }

    [NotMapped]
    public List<WorkerTrait> TraitList => HiringTraits.Parse(Traits);
}

/// <summary>One player's Hiring Hall in one realm, beyond the board: the refresh price and the clock.</summary>
public class HiringState
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

    /// <summary>Paid refreshes since the last cleared dungeon or portal; prices the next.</summary>
    public int RefreshesSinceClear { get; set; }

    /// <summary>When the last local arrived; the next is due <see cref="HiringRules.ArrivalInterval"/> after.</summary>
    public DateTime LastArrivalAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Traits are stored as their numbers, comma-separated.</summary>
public static class HiringTraits
{
    public static List<WorkerTrait> Parse(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => short.TryParse(t, out var n) ? (WorkerTrait?)n : null)
            .Where(t => t.HasValue && Enum.IsDefined(typeof(WorkerTrait), t.Value))
            .Select(t => t!.Value)
            .ToList();

    public static string Write(IEnumerable<WorkerTrait> traits) => string.Join(',', traits.Select(t => ((short)t).ToString()));
}
