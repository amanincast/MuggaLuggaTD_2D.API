using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Enums;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// The server's record of a character a player hired: who it is, and that they are entitled to it.
///
/// <para><b>This is what makes the roll mean anything.</b> A character's signature, affinity and
/// rarity decide its whole kit, and the save is written by the client - so without a record on this
/// side, a player could simply type "Legendary" into their save and the server would compute PvP
/// power from it. Every save is reconciled against these rows: a character with a record is
/// rewritten to match it, and one claiming a roll with no record loses that roll. Design doc
/// 05 §5.2.</para>
///
/// <para><see cref="CharacterId"/> is generated <b>here</b>, at hire, and handed to the client to
/// use as the character's id. An id the client chose would be an id the client could change.</para>
/// </summary>
public class HiredCharacter
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The id this character carries in the player's save. Server-generated.</summary>
    [Required]
    [MaxLength(64)]
    public string CharacterId { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string Sheet { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string CharacterClass { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string SignatureId { get; set; } = string.Empty;

    [Required]
    public AffinityTypes Affinity { get; set; }

    [Required]
    public CharacterRarity Rarity { get; set; }

    public DateTime HiredAt { get; set; } = DateTime.UtcNow;
}
