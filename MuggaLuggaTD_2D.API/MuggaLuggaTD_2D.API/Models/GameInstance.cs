using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

public enum GameInstanceAccessType
{
    Public,
    FriendsAndInviteOnly,
    InviteOnly
}

public class GameInstance
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string OwnerId { get; set; } = string.Empty;

    [ForeignKey(nameof(OwnerId))]
    public ApplicationUser Owner { get; set; } = null!;

    public GameInstanceAccessType AccessType { get; set; } = GameInstanceAccessType.Public;

    [Range(1, int.MaxValue)]
    public int Capacity { get; set; } = 10;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // -----------------------------------------------------------------
    // The season
    // -----------------------------------------------------------------

    /// <summary>
    /// How long a season runs, in days, chosen by whoever created the realm.
    ///
    /// <para>A realm's pace is a property of the group playing it: a weekend group and a month-long
    /// group want different worlds. It is also what gives a realm an ending, which is what makes
    /// winning mean anything — see <c>docs/design/seasons-and-scoring.md</c>.</para>
    /// </summary>
    [Range(MinimumSeasonDays, MaximumSeasonDays)]
    public int SeasonLengthDays { get; set; } = DefaultSeasonDays;

    public const int MinimumSeasonDays = 1;
    public const int MaximumSeasonDays = 180;
    public const int DefaultSeasonDays = 30;

    /// <summary>
    /// When the current season began. Not the instance's creation date, because a realm resets into
    /// a new season rather than ending, and the scoreboard has to start again with it.
    /// </summary>
    public DateTime SeasonStartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Which season this is. 1 for a fresh realm, incremented on every reset.</summary>
    public int SeasonNumber { get; set; } = 1;

    /// <summary>When the current season closes. Nothing scores past this moment.</summary>
    public DateTime SeasonEndsAt => SeasonStartedAt.AddDays(SeasonLengthDays);

    /// <summary>True once the closing time has passed and the season has not yet been settled.</summary>
    public bool SeasonHasExpired(DateTime utcNow) => utcNow >= SeasonEndsAt;

    // Navigation properties
    public WorldViewGameData? WorldViewGameData { get; set; }
    public ICollection<PlayerGameData> PlayerGameData { get; set; } = new List<PlayerGameData>();
    public ICollection<Alliance> Alliances { get; set; } = new List<Alliance>();
    public ICollection<MarketplaceListing> MarketplaceListings { get; set; } = new List<MarketplaceListing>();
}
