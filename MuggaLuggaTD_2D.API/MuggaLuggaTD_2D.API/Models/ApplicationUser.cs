using Microsoft.AspNetCore.Identity;

namespace MuggaLuggaTD_2D.API.Models;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    // Navigation property for game saves
    public ICollection<GameSave> GameSaves { get; set; } = new List<GameSave>();

    // Navigation properties for game instances
    public ICollection<GameInstance> OwnedGameInstances { get; set; } = new List<GameInstance>();
    public ICollection<PlayerGameData> PlayerGameData { get; set; } = new List<PlayerGameData>();
    public ICollection<MarketplaceListing> MarketplaceListingsAsSeller { get; set; } = new List<MarketplaceListing>();
    public ICollection<MarketplaceListing> MarketplaceListingsAsBuyer { get; set; } = new List<MarketplaceListing>();

    // Navigation properties for friendships
    public ICollection<Friendship> SentFriendRequests { get; set; } = new List<Friendship>();
    public ICollection<Friendship> ReceivedFriendRequests { get; set; } = new List<Friendship>();

    // Navigation properties for blocks
    public ICollection<UserBlock> BlockedUsers { get; set; } = new List<UserBlock>();
    public ICollection<UserBlock> BlockedByUsers { get; set; } = new List<UserBlock>();
}
