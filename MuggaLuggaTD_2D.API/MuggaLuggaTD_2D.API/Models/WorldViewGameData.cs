using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

public class WorldViewGameData
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    [Column(TypeName = "jsonb")]
    public string GameData { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
