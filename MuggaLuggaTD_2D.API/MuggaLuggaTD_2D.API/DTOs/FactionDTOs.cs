namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>
/// How one faction stands in a realm. <c>Word</c> is a <c>FactionReadiness</c> name; strength and cap
/// are whole numbers on the hold's scale.
/// </summary>
public record FactionStrengthResponse(
    string Faction,
    long Strength,
    long Cap,
    double Readiness,
    string Word,
    int RegionsHeld,
    DateTime? BloodiedUntil,
    string Lean,
    /// <summary>The region its siege is mustering against, while one is (phase 3).</summary>
    string? MusteringAgainst = null
);

/// <summary>
/// Every faction in a realm. <c>Report</c> is the debug controls' account of what the factions did,
/// one line each; null on an ordinary read.
/// </summary>
public record FactionsResponse(List<FactionStrengthResponse> Factions, DateTime ServerNow, List<string>? Report = null);

/// <summary>
/// The Combat Debug window's faction controls (Development only). Each field that is set is applied,
/// in this order: <c>SimulateHours</c> (the factions take their turns through those hours),
/// <c>Readiness</c>, <c>Bloody</c>, <c>ClearBloodied</c>, <c>CloseMuster</c> (settle its live siege
/// now), then <c>ForceAct</c> (act now, whatever readiness and lean say: <c>Action</c> "Siege", or a
/// raid).
/// </summary>
public record FactionDebugRequest(
    string Faction,
    double? SimulateHours = null,
    double? Readiness = null,
    bool Bloody = false,
    bool ClearBloodied = false,
    bool ForceAct = false,
    string? Action = null,
    bool CloseMuster = false
);

/// <summary>
/// The defender sallying out to break a faction's siege: the champions who go. The server prices them
/// from the saved roster, as it does any army.
/// </summary>
public record FactionSortieRequest(
    [System.ComponentModel.DataAnnotations.Required] List<string> ArmyCharacterIds,
    [System.ComponentModel.DataAnnotations.Required] string SharedContractVersion
);
