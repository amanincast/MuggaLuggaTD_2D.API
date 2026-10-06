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
    DateTime? BloodiedUntil
);

public record FactionsResponse(List<FactionStrengthResponse> Factions, DateTime ServerNow);

/// <summary>
/// The Combat Debug window's faction controls (Development only). Each field that is set is applied,
/// in this order: <c>SimulateHours</c>, <c>Readiness</c>, <c>Bloody</c>, <c>ClearBloodied</c>.
/// </summary>
public record FactionDebugRequest(
    string Faction,
    double? SimulateHours = null,
    double? Readiness = null,
    bool Bloody = false,
    bool ClearBloodied = false
);
