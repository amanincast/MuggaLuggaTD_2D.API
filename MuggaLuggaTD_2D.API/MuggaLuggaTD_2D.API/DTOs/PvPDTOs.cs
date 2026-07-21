using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>
/// A request to attack a rival-owned world location.
///
/// Deliberately carries no power, no dice roll, and no outcome — the server derives all of those.
/// The client only names the target and which of its characters are attacking.
/// </summary>
public record PvPAttackRequest(
    [Required] string LocationId,
    [Required] List<string> AttackerCharacterIds,
    /// <summary>
    /// The client's MuggaLuggaTD.Shared version. The server rejects a mismatch rather than resolving
    /// the fight under rules the client disagrees with.
    /// </summary>
    [Required] string SharedContractVersion
);

/// <summary>The resolved fight, as computed by the server. The client renders this; it does not recompute.</summary>
public record PvPAttackResponse(
    bool AttackerWins,
    int D20Roll,
    int Modifier,
    int Total,
    float AttackerPower,
    float DefenderPower,
    string LocationId,
    /// <summary>Set on a win: what happened to the location (Captured or Removed).</summary>
    string? ConquestOutcome,
    /// <summary>Set on a loss: the D4 consequence the attacker suffered.</summary>
    string? DefeatOutcome,
    /// <summary>Character ids the defeat consequence applied to, for the client to reflect locally.</summary>
    List<string> AffectedCharacterIds
);
