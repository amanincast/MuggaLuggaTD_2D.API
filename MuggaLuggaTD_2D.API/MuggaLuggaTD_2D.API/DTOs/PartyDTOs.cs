using System.ComponentModel.DataAnnotations;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>One company as the Guild Hall shows it.</summary>
public record PartyDto(
    Guid Id,
    string Name,
    string Banner,
    List<string> CharacterIds,
    int SortOrder,
    CompanyState State,
    string? RegionId,
    /// <summary>The site it stands at; null while on the road.</summary>
    string? SiteId,
    /// <summary>Its journey while travelling, returning or ambushed, else null.</summary>
    JourneyDto? Journey = null,
    /// <summary>What has it halted, while ambushed.</summary>
    AmbushDto? Ambush = null
);

/// <summary>
/// A company on the road: where from and to, when it left and arrives, and the route the server timed -
/// its region cells as [x, y] pairs and the seconds after departure at which each is reached. The
/// client walks the company along it by the clock (<see cref="TravelRules.Progress"/>).
/// </summary>
public record JourneyDto(
    string FromSiteId,
    string ToSiteId,
    DateTime DepartedAt,
    DateTime ArrivesAt,
    List<int[]> Cells,
    List<double> Seconds,
    /// <summary>When an ambush stopped it on this road, else null. It stands where it was at that moment.</summary>
    DateTime? HaltedAt = null
);

/// <summary>
/// The warband that has a company halted, and the fight it offers: a tier-1 clearing's waves at the
/// level of the land (<see cref="AmbushRules"/>). Sent when the company is ambushed, so the prompt can
/// say what it faces before the player chooses.
/// </summary>
public record AmbushDto(
    string SiteId,
    int Level,
    int Tier,
    int Waves
);

/// <summary>An ambush run opened: the id its claim names, and the fight to load.</summary>
public record AmbushFightResponse(
    Guid RunId,
    AmbushDto Ambush,
    PartiesResponse Parties
);

public record AmbushClaimRequest(
    [Required] Guid RunId,
    bool Won,
    [Required] string SharedContractVersion
);

/// <summary>What fighting an ambush came to. A loss pays nothing and turns the company back.</summary>
public record AmbushClaimResponse(
    bool Won,
    long Experience,
    List<StateManagement.Models.ItemSaveData> Items,
    List<MaterialGrant> Materials,
    long Gold,
    long GoldBalance,
    PartiesResponse Parties
);

public record AmbushOrderRequest(
    [Required] string SharedContractVersion
);

/// <summary>Sends a company to a site in the region it stands in.</summary>
public record PartyTravelRequest(
    [Required] string SiteId,
    [Required] string SharedContractVersion
);

/// <summary>
/// Why a character cannot join a company or fight: "garrisoned", "held" or "siege", and where. A
/// character in another of the player's companies is not listed here - the companies themselves say so.
/// </summary>
public record CharacterCommitmentDto(string CharacterId, string Reason, string? SiteId);

/// <summary>A player's companies in a realm, and what bounds them.</summary>
public record PartiesResponse(
    List<PartyDto> Parties,
    /// <summary>How many companies the player may hold now: roster cap / 4 (<see cref="CompanyRules.MaxCompanies"/>).</summary>
    int MaxParties,
    int MaxSize,
    int RosterCap,
    List<CharacterCommitmentDto> Commitments,
    /// <summary>The server's clock when this was written, so a client can walk journeys by server time.</summary>
    DateTime ServerNow = default
);

/// <summary>
/// Forms a company. The name and banner are optional; a new company is named and coloured by its place
/// in the list until its player chooses.
/// </summary>
public record PartyCreateRequest(
    string? Name,
    string? Banner,
    List<string>? CharacterIds,
    [Required] string SharedContractVersion
);

/// <summary>Renames, re-colours or re-members a company. A null field is left as it is.</summary>
public record PartyUpdateRequest(
    string? Name,
    string? Banner,
    List<string>? CharacterIds,
    [Required] string SharedContractVersion
);
