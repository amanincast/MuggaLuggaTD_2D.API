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

    // --- Veterancy (Workers spec) ---------------------------------------

    /// <summary>Hours of experience: time assigned to a site, faster with Quick Study or a Mentor beside them.</summary>
    public double HoursWorked { get; set; }

    /// <summary>Every roll made this season, in order (<see cref="WorkerRollLog"/>). Perks are read from it.</summary>
    [MaxLength(512)]
    public string Rolls { get; set; } = string.Empty;

    /// <summary>How many of <see cref="Rolls"/> the player has seen revealed.</summary>
    public int RollsSeen { get; set; }

    /// <summary>The highest level whose roll has been made, so a level never rolls twice.</summary>
    public int LevelRolledTo { get; set; } = 1;

    /// <summary>★ KEEP: one of the (at most two) workers who go with the player into the next season.</summary>
    public bool Keep { get; set; }

    /// <summary>Whole goods this worker has gathered, ever.</summary>
    public int LifetimeOutput { get; set; }

    /// <summary>Season ends this worker has come through as a veteran.</summary>
    public int SeasonsServed { get; set; }

    [NotMapped]
    public List<WorkerTrait> TraitList => HiringTraits.Parse(Traits);

    [NotMapped]
    public int Level => WorkerLevelRules.LevelFor(HoursWorked);

    [NotMapped]
    public List<WorkerRollResult> RollList => WorkerRollLog.Parse(Rolls);

    [NotMapped]
    public List<WorkerPerk> PerkList => RollList.Where(r => r.Perk.HasValue).Select(r => r.Perk!.Value).Distinct().ToList();

    /// <summary>What their output depends on, at their current level.</summary>
    public WorkerSheet Sheet() => new()
    {
        Tier = Tier,
        Trade = Trade,
        SecondTrade = SecondTrade,
        Traits = TraitList,
        Perks = PerkList,
        HomeBiome = HomeBiome,
        Level = Level
    };
}

/// <summary>
/// A worker's rolls, stored as <c>level:kind:perk:tier:trait:second</c> entries joined by ';' (an
/// empty field is "none"). Twelve perks and five roll levels keep it far under its 512 characters.
/// </summary>
public static class WorkerRollLog
{
    public static List<WorkerRollResult> Parse(string? text)
    {
        var rolls = new List<WorkerRollResult>();
        foreach (var entry in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = entry.Split(':');
            if (f.Length < 6 || !int.TryParse(f[0], out int level) || !short.TryParse(f[1], out short kind)) continue;
            rolls.Add(new WorkerRollResult
            {
                Level = level,
                Kind = (WorkerRollKind)kind,
                Perk = short.TryParse(f[2], out var p) ? (WorkerPerk)p : null,
                NewTier = short.TryParse(f[3], out var t) ? (WorkerTier)t : null,
                NewTrait = short.TryParse(f[4], out var tr) ? (WorkerTrait)tr : null,
                NewSecondTrade = short.TryParse(f[5], out var s) ? (ResourceTrade)s : null
            });
        }
        return rolls;
    }

    public static string Write(IEnumerable<WorkerRollResult> rolls) => string.Join(';', rolls.Select(r => string.Join(':',
        r.Level, (short)r.Kind, N(r.Perk), N(r.NewTier), N(r.NewTrait), N(r.NewSecondTrade))));

    private static string N<T>(T? value) where T : struct, Enum => value.HasValue ? Convert.ToInt16(value.Value).ToString() : "";
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
