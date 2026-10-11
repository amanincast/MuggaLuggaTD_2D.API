using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// A living board as it stood when last built (Leaderboards L1): its rows, in order. Rebuilt when someone asks
/// and it is over an hour old, so nothing ticks and everyone that hour sees the same board.
/// </summary>
public class LeaderboardSnapshot
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(32)]
    public string Board { get; set; } = string.Empty;

    /// <summary>The bracket (Short / Standard / Long) or the Slayers window (Week / AllTime).</summary>
    [Required, MaxLength(32)]
    public string Bracket { get; set; } = string.Empty;

    public DateTime BuiltAt { get; set; }

    /// <summary>Every row, ranked: the board shows the top and finds the asker's own row here.</summary>
    public string RowsJson { get; set; } = "[]";
}
