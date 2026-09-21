using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>
/// A request to raid a rival-held region.
///
/// <para>Deliberately carries no power, no dice roll and no damage — the server derives all of them
/// from the persisted world and rosters. The client names the region and which of its champions are
/// marching, and nothing else.</para>
/// </summary>
public record RegionRaidRequest(
    [Required] string RegionId,
    [Required] List<string> AttackerCharacterIds,
    /// <summary>
    /// The client's MuggaLuggaTD.Shared version. The server rejects a mismatch rather than resolving
    /// a raid under rules the client disagrees with — it showed the player a bar and an expected
    /// cost, and those have to be the ones applied.
    /// </summary>
    [Required] string SharedContractVersion
);

/// <summary>The resolved raid, as the server computed it. The client renders this; it does not recompute.</summary>
public record RegionRaidResponse(
    string RegionId,
    bool AttackerWins,
    int D20Roll,
    int Modifier,
    int Total,
    double MarchingPower,
    long Hold,
    long RaidBar,
    /// <summary>Resolve the region actually lost. Zero when the raid was repelled.</summary>
    int ResolveDamage,
    int ResolveBefore,
    int ResolveAfter,
    /// <summary>When this attacker may raid this region again. Applies win or lose.</summary>
    DateTime CooldownEndsAt
);

/// <summary>
/// How long before this player may raid a region again. An object rather than a bare timestamp so
/// the client deserialises it the same way as everything else.
/// </summary>
public record RaidCooldownResponse(
    string RegionId,
    DateTime CooldownEndsAt,
    bool CanRaidNow
);
