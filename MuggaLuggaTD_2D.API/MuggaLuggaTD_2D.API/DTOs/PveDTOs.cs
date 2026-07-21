using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>Opens a run against a PvE location, as the player enters combat.</summary>
public record PveBeginRequest(
    [Required] string LocationId,
    [Required] string SharedContractVersion
);

public record PveBeginResponse(Guid RunId);

/// <summary>
/// Claims the conquest for a completed run. Carries no outcome — the server derives that from the
/// location's type, and refuses the claim entirely if the run does not check out.
/// </summary>
public record PveClaimRequest(
    [Required] Guid RunId,
    [Required] string SharedContractVersion
);

public record PveClaimResponse(
    string LocationId,
    /// <summary>"CaptureForPlayer" or "RemoveLocation", as decided by the server.</summary>
    string ConquestOutcome
);
