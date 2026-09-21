using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>
/// Opens a run against a site, as the player enters combat.
///
/// <para><c>SiteId</c> is "&lt;regionId&gt;:&lt;index&gt;". The server does not take the client's
/// word for what that site is: it finds the region, rebuilds the interior from the region's seed,
/// and looks the site up in what it generated.</para>
/// </summary>
public record PveBeginRequest(
    [Required] string SiteId,
    [Required] string SharedContractVersion
);

public record PveBeginResponse(Guid RunId);

/// <summary>
/// Claims the conquest for a completed run. Carries no outcome — the server derives that from the
/// site's type, and refuses the claim entirely if the run does not check out.
/// </summary>
public record PveClaimRequest(
    [Required] Guid RunId,
    [Required] string SharedContractVersion
);

public record PveClaimResponse(
    string SiteId,
    /// <summary>"CaptureForPlayer" or "RemoveLocation", as decided by the server.</summary>
    string ConquestOutcome,
    /// <summary>
    /// Experience earned for the clear, rolled by the server from the site's wave budget.
    /// The client persists this rather than a total of its own.
    /// </summary>
    long Experience,
    /// <summary>Items earned for the clear, rolled by the server. Already fully specified.</summary>
    List<StateManagement.Models.ItemSaveData> Items,
    /// <summary>
    /// Resolve the region regained, when the cleared site was a hostile one inside a region the
    /// player holds. Zero otherwise — clearing unclaimed land steadies nothing you own.
    /// </summary>
    int ResolveRestored = 0
);
