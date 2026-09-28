using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One of a player's companies in one realm: up to four characters who travel and fight together.
/// <c>docs/design/parties-and-travel.md</c> (Unity repo).
///
/// <para><b>A server record, not a session list.</b> The party used to be a list the client kept for
/// itself (<c>ActiveCharacters</c>). A company that is on the road while its player is offline, or
/// that the server must refuse to send into a dungeon because half of it is in a siege, has to be
/// something the server holds.</para>
///
/// <para>A character is in at most one company. Being in a company does not stop a character being
/// garrisoned, marched on a raid or siege, or captured — those win, and take the character out of the
/// company (a garrison) or out of the fight (the others) — see <c>PartyService</c>.</para>
/// </summary>
public class PlayerParty
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

    [Required]
    [MaxLength(CompanyRules.MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The company's banner, "#rrggbb" (<see cref="CompanyRules.Banners"/>).</summary>
    [Required]
    [MaxLength(7)]
    public string Banner { get; set; } = CompanyRules.Banners[0];

    /// <summary>The members' character ids, as a JSON array; the first is the leader the player controls.</summary>
    [Required]
    public string CharacterIdsJson { get; set; } = "[]";

    /// <summary>Where the list shows it: companies are shown in the order they were formed.</summary>
    public int SortOrder { get; set; }

    /// <summary>What it is doing (<see cref="CompanyState"/>).</summary>
    public CompanyState State { get; set; } = CompanyState.Idle;

    /// <summary>The region it stands in, and the site within it; null when unknown (before it first moves).</summary>
    [MaxLength(64)]
    public string? RegionId { get; set; }

    [MaxLength(64)]
    public string? SiteId { get; set; }

    // ---- The journey, while Travelling (§3). SiteId is null on the road. ----

    /// <summary>Where the journey set out from, and where it is going.</summary>
    [MaxLength(64)]
    public string? FromSiteId { get; set; }

    [MaxLength(64)]
    public string? ToSiteId { get; set; }

    /// <summary>When it left and when it arrives. Where it is in between is worked out from the clock.</summary>
    public DateTime? DepartedAt { get; set; }
    public DateTime? ArrivesAt { get; set; }

    /// <summary>
    /// The route the server timed, as JSON (<c>JourneyRoute</c>): its cells and the seconds at which
    /// each is reached. Sent to the client as it is, so the region view walks the same road.
    /// </summary>
    public string? RouteJson { get; set; }

    // ---- Ambushes (§4, phase 3) ----

    /// <summary>
    /// The share of the journey's time at which it is ambushed, rolled when the order was accepted, or
    /// null for a quiet road. Never sent to the client: a player learns of an ambush when it strikes.
    /// </summary>
    public double? AmbushAt { get; set; }

    /// <summary>When the ambush halted the company (state Ambushed). The client stands it where it stopped.</summary>
    public DateTime? HaltedAt { get; set; }

    /// <summary>The run opened to fight the ambush, which its claim must name.</summary>
    public Guid? AmbushRunId { get; set; }

    /// <summary>When that run opened; a claim faster than a fight could be is refused.</summary>
    public DateTime? AmbushRunStartedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
