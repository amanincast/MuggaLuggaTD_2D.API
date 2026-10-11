using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// How often one player was refused on one route on one day (Hardening 8). Kept so the limits can be reviewed
/// after a few weeks of play: many refusals of one player means a flood; a few each of many players means the
/// limit is too tight.
/// </summary>
public class RateLimitRefusal
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The UTC day, yyyymmdd.</summary>
    public int Day { get; set; }

    /// <summary>"general" or "trade": which kind of request was refused.</summary>
    [MaxLength(16)]
    public string Policy { get; set; } = string.Empty;

    /// <summary>The path with its ids taken out.</summary>
    [MaxLength(200)]
    public string Route { get; set; } = string.Empty;

    /// <summary>The player's user id, or "ip:{address}" before login.</summary>
    [MaxLength(460)]
    public string Who { get; set; } = string.Empty;

    public int Count { get; set; }

    public DateTime LastAt { get; set; }
}
