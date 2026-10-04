using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// A character hurt in a lost auto-fight (<c>BloodiedRules</c>; docs/design/auto-fight.md §4), barred
/// from every fight until <see cref="RecoversAt"/>. Kept on the character rather than the company,
/// so taking them out of the company does not heal them. One row per character, updated in place;
/// a row in the past is simply recovered.
/// </summary>
public class BloodiedCharacter
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

    [Required]
    [MaxLength(128)]
    public string CharacterId { get; set; } = string.Empty;

    public DateTime RecoversAt { get; set; }
}
