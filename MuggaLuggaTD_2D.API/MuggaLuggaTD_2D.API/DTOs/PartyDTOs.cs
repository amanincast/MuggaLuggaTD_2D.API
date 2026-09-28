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
    string? SiteId
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
    List<CharacterCommitmentDto> Commitments
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
