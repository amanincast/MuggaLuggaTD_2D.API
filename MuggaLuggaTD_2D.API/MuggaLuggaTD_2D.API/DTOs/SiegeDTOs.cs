using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>
/// A request to besiege a rival region.
///
/// <para>Like a raid, it carries no power and no hold: the server prices the army from the saved
/// roster and the region from the live world. The client names the region and the army.</para>
/// </summary>
public record SiegeDeclareRequest(
    [Required] string RegionId,
    [Required] List<string> ArmyCharacterIds,
    /// <summary>The client's MuggaLuggaTD.Shared version. A mismatch is refused.</summary>
    [Required] string SharedContractVersion
);

/// <summary>
/// A siege as a player sees it.
///
/// <para>Both sides see the same siege, with one difference: only the attacker is told which
/// champions are in the army. The defender sees what it is worth, which is what they are mustering
/// against; the roster is the attacker's business.</para>
/// </summary>
public record SiegeResponse(
    Guid Id,
    string RegionId,
    string AttackerUserId,
    string AttackerDisplayName,
    string DefenderUserId,
    /// <summary>Mustering, Assault, Won, Repelled, Lapsed or Cancelled.</summary>
    string State,
    DateTime DeclaredAt,
    DateTime MusterEndsAt,
    DateTime AssaultEndsAt,
    double MarchingPower,
    /// <summary>The region's hold as frozen when muster closed. Null while still mustering.</summary>
    long? FrozenHold,
    DateTime? ResolvedAt,
    List<string> ArmyCharacterIds
);

/// <summary>Every live siege in a realm.</summary>
public record SiegeListResponse(List<SiegeResponse> Sieges);
