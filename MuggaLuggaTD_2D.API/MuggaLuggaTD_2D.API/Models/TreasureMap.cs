using System.ComponentModel.DataAnnotations;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One treasure map a player found (Active Content D, <see cref="TreasureMapRules"/>). It points to a cache in
/// <see cref="RegionId"/>, beside <see cref="LandmarkSiteId"/>; both are chosen from the map's id when it is found and
/// stored, so the map never moves. It is held until its cache is opened (<see cref="OpenedAt"/>); a season's maps end
/// with the season.
/// </summary>
public class TreasureMap : IRevisioned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GameInstanceId { get; set; }

    public int SeasonNumber { get; set; }

    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string RegionId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string LandmarkSiteId { get; set; } = string.Empty;

    public DateTime FoundAt { get; set; }

    /// <summary>Where it was found: a site id, a cache's (a chain), or "debug".</summary>
    [MaxLength(64)]
    public string FoundAtSiteId { get; set; } = string.Empty;

    /// <summary>The map whose cache held this one, for a chain.</summary>
    public Guid? ChainedFromId { get; set; }

    public DateTime? OpenedAt { get; set; }

    [ConcurrencyCheck]
    public long Revision { get; set; }
}
