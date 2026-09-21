using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// Where a player finished a season. Written once, when the season closes, and never changed.
///
/// <para><b>This is the carry-over.</b> The design's requirement is that winning has to mean
/// something beyond a line in a table — it should influence the realms you play afterwards — but
/// <i>what</i> a standing grants is a balance question this game is far too young to answer. So what
/// ships is the record: the season, the rank, the score and where it came from, persisted and
/// readable. Whatever is built on it later starts with its history already there rather than from
/// nothing.</para>
/// </summary>
public class SeasonResult
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    [Required]
    public string UserId { get; set; } = string.Empty;

    [ForeignKey(nameof(UserId))]
    public ApplicationUser User { get; set; } = null!;

    public int SeasonNumber { get; set; }

    /// <summary>1 for the winner. Ties share a rank, as they do in any table.</summary>
    public int Rank { get; set; }

    public double TotalPoints { get; set; }

    public double HoldingPoints { get; set; }
    public double ClearingPoints { get; set; }
    public double RaidingPoints { get; set; }

    /// <summary>Regions held when the season closed, for the story the standings tell.</summary>
    public int RegionsHeld { get; set; }

    public DateTime SeasonStartedAt { get; set; }
    public DateTime SeasonEndedAt { get; set; }
}
