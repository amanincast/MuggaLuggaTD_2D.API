using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Enums;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One of the six recruits currently drinking at a player's Tavern, in one realm.
///
/// <para>The board is rolled by the server and stored, never sent by the client. A client that
/// could roll its own board would roll until it liked one, and a character is the most valuable
/// thing in the game. Design doc 05 §5.</para>
///
/// <para>A restock replaces every row for that player. A hired row is kept with
/// <see cref="HiredAt"/> set rather than deleted, so the slot reads as taken until the next
/// dungeon is cleared - the board is what the night offered, not a list of what is left.</para>
/// </summary>
public class TavernRecruit
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

    /// <summary>0..5. The card's place on the screen, and what a hire request names.</summary>
    [Required]
    public int Slot { get; set; }

    [Required]
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The ally template's LinkName. Decides the art, and nothing else.</summary>
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

    /// <summary>When the board this recruit belongs to was rolled.</summary>
    public DateTime RolledAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set once hired. A slot is hired at most once, whatever the client asks for twice.</summary>
    public DateTime? HiredAt { get; set; }
}
