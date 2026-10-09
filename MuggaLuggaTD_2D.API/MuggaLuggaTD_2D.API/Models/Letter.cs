using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One letter in a player's inbox for one realm (the Inbox spec, Unity repo
/// <c>Specifications/.../Inbox - Letters while you were away.md</c>): something that happened to
/// <i>this player</i> while they may have been away, with an action. The war log is the realm's news;
/// this is personal.
///
/// <para>The server stores data, the client writes the words (<c>LetterWording</c>), as with the war
/// log and quests. Names are frozen at write (<see cref="ActorName"/>), so a letter still reads right
/// after a rename. Letters are <b>not</b> wiped by a season reset; they age out
/// (<c>LetterRules.KeepFor</c>), so the season's own letter survives it.</para>
/// </summary>
public class Letter
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    [Required]
    public string UserId { get; set; } = string.Empty;

    /// <summary>A <c>LetterKind</c>, by name.</summary>
    [Required]
    [MaxLength(32)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>When the event happened, not when it was settled: a march that arrived at 02:10 reads 02:10.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>One letter per event per player: a lazy settle that runs twice cannot write two.</summary>
    [Required]
    [MaxLength(120)]
    public string DedupKey { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? RegionId { get; set; }

    /// <summary>What the action needs: a siege, party, quest or report id.</summary>
    [MaxLength(64)]
    public string? SubjectId { get; set; }

    /// <summary>Who did it (a rival or a faction's name), frozen at write.</summary>
    [MaxLength(64)]
    public string? ActorName { get; set; }

    /// <summary>A short detail for the wording - "80 → 55", "312:2" - as the war log keeps it.</summary>
    [MaxLength(200)]
    public string? Detail { get; set; }

    /// <summary>How many events this letter stands for: raids on one region within the hour fold together.</summary>
    public int Count { get; set; } = 1;

    /// <summary>Null while unread.</summary>
    public DateTime? ReadAt { get; set; }
}
