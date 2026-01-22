using Microsoft.AspNetCore.Identity;

namespace MuggaLuggaTD_2D.API.Models;

public class ApplicationUser : IdentityUser
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    // Navigation property for game saves
    public ICollection<GameSave> GameSaves { get; set; } = new List<GameSave>();
}
