using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

public class UserBlock
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public string BlockerId { get; set; } = string.Empty;

    [ForeignKey(nameof(BlockerId))]
    public ApplicationUser Blocker { get; set; } = null!;

    [Required]
    public string BlockedUserId { get; set; } = string.Empty;

    [ForeignKey(nameof(BlockedUserId))]
    public ApplicationUser BlockedUser { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
