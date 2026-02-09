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

    // Navigation properties
    public WorldViewGameData? WorldViewGameData { get; set; }
    public ICollection<PlayerGameData> PlayerGameData { get; set; } = new List<PlayerGameData>();
    public ICollection<Alliance> Alliances { get; set; } = new List<Alliance>();
    public ICollection<MarketplaceListing> MarketplaceListings { get; set; } = new List<MarketplaceListing>();
}
