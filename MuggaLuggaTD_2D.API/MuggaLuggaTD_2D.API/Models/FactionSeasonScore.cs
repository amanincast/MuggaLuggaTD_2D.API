using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// A faction's place on a season's scoreboard (<c>docs/design/season-end.md</c> §2). It earns from its
/// land at the players' rates (<c>SeasonEndRules.RateForFaction</c>), settled lazily beside the players
/// by <c>SeasonScoreService.SettleAllAsync</c>, and nothing for deeds. At the close its rank and land are
/// written here too: a faction has no account, so it has no <see cref="SeasonResult"/>.
/// </summary>
public class FactionSeasonScore
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    public int SeasonNumber { get; set; } = 1;

    public FactionId Faction { get; set; }

    /// <summary>Points banked as of <see cref="LastSettledAt"/>. All of it is holding.</summary>
    public double SettledPoints { get; set; }

    /// <summary>What its land earned an hour when last settled.</summary>
    public double PointsPerHour { get; set; }

    public DateTime LastSettledAt { get; set; }

    /// <summary>Where it finished, ranked with the players. Null until the season closes.</summary>
    public int? Rank { get; set; }

    /// <summary>Regions it held at the close.</summary>
    public int RegionsHeld { get; set; }
}
