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
    List<string> ArmyCharacterIds,
    /// <summary>True once the attacker has begun the siege's one assault. Visible to both sides.</summary>
    bool AssaultBegun = false
);

/// <summary>Every live siege in a realm.</summary>
public record SiegeListResponse(List<SiegeResponse> Sieges);

/// <summary>
/// The assault the server has opened for the attacker: the run to claim against, and the fight
/// they are to be handed. The client builds the combat from these numbers and may not choose its own.
/// </summary>
public record SiegeAssaultResponse(
    Guid SiegeId,
    Guid RunId,
    string RegionId,
    int EnemyLevel,
    int Waves,
    int EliteCount,
    long FrozenHold,
    double MarchingPower,
    DateTime AssaultEndsAt,
    /// <summary>The locked army. These are the champions who fight it.</summary>
    List<string> ArmyCharacterIds
);

/// <summary>The attacker asking to begin the assault.</summary>
public record SiegeAssaultBeginRequest([Required] string SharedContractVersion);

/// <summary>How the assault went, as the attacker's client reports it.</summary>
public record SiegeAssaultClaimRequest(
    [Required] Guid RunId,
    /// <summary>
    /// False when the attacker lost. A loss is reported rather than left to expire so the defender
    /// learns straight away; one never reported is treated as a loss when the grace period ends.
    /// </summary>
    bool Won,
    [Required] string SharedContractVersion
);

/// <summary>What the assault decided.</summary>
public record SiegeAssaultResult(
    Guid SiegeId,
    string RegionId,
    /// <summary>Won or Repelled.</summary>
    string Outcome,
    /// <summary>Points the season paid for it - to the attacker on a win, the defender on a repel.</summary>
    double PointsAwarded,
    /// <summary>Champions of the defender taken prisoner. Zero on a repel.</summary>
    int CapturedCount
);
