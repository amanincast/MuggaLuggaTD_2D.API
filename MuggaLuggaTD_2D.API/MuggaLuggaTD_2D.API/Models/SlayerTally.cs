using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// Enemies one player slew on one UTC day, in every realm together (the Slayers board). By the day, because
/// seasons don't line up across realms: the board's live window is the last 7 days, and all-time is the sum.
/// </summary>
public class SlayerTally
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The UTC day, at midnight.</summary>
    public DateTime Day { get; set; }

    public long Kills { get; set; }
}
