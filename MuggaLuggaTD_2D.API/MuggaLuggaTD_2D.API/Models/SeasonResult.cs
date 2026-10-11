using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// Where a player finished a season. Written once, when the season closes; afterwards only whether
/// the player has seen it and opened its chest change.
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

    /// <summary>
    /// The realm. Null once the realm is deleted: a result outlives its realm, so nobody's crowns go with it
    /// (Leaderboards row 8). <see cref="RealmName"/> keeps what it was called.
    /// </summary>
    public Guid? GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance? GameInstance { get; set; }

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

    /// <summary>
    /// The chest this finish earned (<c>SeasonEndRules.ChestFor</c>), decided at the close so it never
    /// changes. Null: the player scored nothing.
    /// </summary>
    public Enums.ItemRarityTypes? ChestRarity { get; set; }

    /// <summary>When the chest was opened; set before the grant, so it opens once.</summary>
    public DateTime? ChestOpenedAt { get; set; }

    /// <summary>What the chest held, for the page shown again later.</summary>
    [MaxLength(128)]
    public string? ChestItemName { get; set; }

    /// <summary>When the player first saw the season-end page. Null: show it on their next return.</summary>
    public DateTime? SeenAt { get; set; }

    // ---- Written at the close for the leaderboards (Leaderboards spec §4.1) ----

    /// <summary>The realm's name at the close, kept if the realm is deleted.</summary>
    [MaxLength(64)]
    public string? RealmName { get; set; }

    /// <summary>The player's name at the close, for the record. Boards show the current name.</summary>
    [MaxLength(64)]
    public string? PlayerName { get; set; }

    public int SeasonLengthDays { get; set; }

    /// <summary>How many were ranked that season, factions included ("3rd of 7").</summary>
    public int PlayersInSeason { get; set; }

    /// <summary>Points per hour over the season (<c>SeasonEndRules.AverageRate</c>): what the rate boards rank.</summary>
    public double AverageRate { get; set; }

    /// <summary>First in a contested season: a crown. Stored at the close rather than worked out per row.</summary>
    public bool Crowned { get; set; }

    /// <summary>How long the player was in the season (<c>LeaderboardRules.HoursIn</c>).</summary>
    public double HoursInSeason { get; set; }

    /// <summary>Whether the season counts on the rate boards (<c>LeaderboardRules.Qualifies</c>).</summary>
    public bool Qualifies { get; set; }

    /// <summary>The month it counts in, yyyymm (<c>LeaderboardRules.MonthOf</c>).</summary>
    public int EndedMonth { get; set; }
}
