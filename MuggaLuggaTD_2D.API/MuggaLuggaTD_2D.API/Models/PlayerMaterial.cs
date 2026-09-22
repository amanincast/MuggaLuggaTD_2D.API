using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// How much of one material a player holds, in one realm.
///
/// <para><b>Why this is not in the save.</b> Everything else a player owns is written by the client
/// into its own save blob and taken at face value. That was tolerable while materials only fed item
/// merging, but design doc 05 makes them the Tavern's currency: they buy characters, and a character
/// is the most valuable thing in the game. A balance the client writes is a balance the client can
/// mint, so this one lives here, granted by run claims and spent through endpoints.</para>
///
/// <para>Per realm rather than per account, because a season resets the world and its economy with
/// it — and because a realm is the unit everything else contested is scoped to.</para>
/// </summary>
public class PlayerMaterial
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

    /// <summary>
    /// The material's content name, e.g. "Perfect Fire Crystal". Named rather than typed because
    /// materials are content: new ones appear in MaterialData.json without a schema change.
    /// </summary>
    [Required]
    [MaxLength(128)]
    public string MaterialName { get; set; } = string.Empty;

    /// <summary>Never negative. Spending checks the balance first and refuses rather than going under.</summary>
    [Required]
    public int Quantity { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
