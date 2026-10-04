using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One player's last clear of one dungeon or portal (<c>SiteRotationRules</c>).
///
/// <para>A clear used to be written into the shared world, which spent the site for everybody in
/// the realm for eight hours. It is now the clearer's alone: this row locks them out of the site for
/// ten minutes and rations what the clear does to the realm (resolve, a recruit, refresh resets,
/// season points) to once per eight hours. Nobody else's map changes.</para>
///
/// <para>One row per player per site, updated in place. A season reset deletes them: the next
/// season's world is new, and its site ids would otherwise inherit these.</para>
/// </summary>
public class PlayerSiteClear
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
    public string SiteId { get; set; } = string.Empty;

    /// <summary>The player's last clear here: the lockout runs from it.</summary>
    public DateTime LastClearedAt { get; set; }

    /// <summary>When a clear here last shaped the realm for this player; null if it never has.</summary>
    public DateTime? WorldRewardsAt { get; set; }
}
