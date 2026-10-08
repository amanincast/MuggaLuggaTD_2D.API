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
    [Required] string SharedContractVersion,
    /// <summary>
    /// The characters going in (1.32.0). The server refuses the run if any is not the player's, or is
    /// garrisoned, held prisoner or marching with a siege army.
    /// </summary>
    List<string>? CharacterIds = null,
    /// <summary>
    /// The company going in (1.33.0). It must be standing at the site - you fight where you stand - and
    /// its members are the fighters; <see cref="CharacterIds"/> is then ignored.
    /// </summary>
    Guid? PartyId = null
);

public record PveBeginResponse(Guid RunId);

/// <summary>
/// Claims the conquest for a completed run. Carries no outcome — the server derives that from the
/// site's type, and refuses the claim entirely if the run does not check out.
/// </summary>
public record PveClaimRequest(
    [Required] Guid RunId,
    [Required] string SharedContractVersion,
    // Enemies slain by people ("Goblin": 9), for Slay quests. Clamped by the server (QuestRules.ClampKills).
    Dictionary<string, int>? Kills = null
);

/// <summary>
/// The player gave up a run (BloodiedRules): the server closes it and Bloodies everyone who went in.
/// A lost run that was not given up is never reported; its cost is the capture roll.
/// </summary>
public record PveAbandonRequest(
    [Required] Guid RunId,
    [Required] string SharedContractVersion
);

/// <summary>Who came out Bloodied, and until when.</summary>
public record PveAbandonResponse(List<BloodiedDto> Bloodied);

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
    int ResolveRestored = 0,
    /// <summary>
    /// Materials paid into the player's wallet for the clear, rolled by the server. The client shows
    /// these and re-reads its balances; it does not add them itself.
    /// </summary>
    List<MuggaLuggaTD.Shared.Gameplay.MaterialGrant>? Materials = null,
    /// <summary>
    /// Gold paid for the clear. Derived from <see cref="Experience"/> by the server, never picked up
    /// in the scene — there is no coin to collect, so there is nothing for a client to over-report.
    /// </summary>
    long Gold = 0,
    /// <summary>
    /// The purse after the clear, including whatever the players land accrued while they were
    /// fighting. Sent back so the completion screen can show a real balance rather than adding the
    /// grant to a figure it read before the run.
    /// </summary>
    long GoldBalance = 0,
    /// <summary>
    /// Whether this clear shaped the realm (resolve, a recruit, refresh resets, season points). A
    /// player's clears of one site do that once every eight hours (<c>SiteRotationRules</c>).
    /// </summary>
    bool WorldRewards = true,
    /// <summary>When this player may fight the site again (the ten-minute lockout); null for a capture.</summary>
    DateTime? LockedUntil = null,
    /// <summary>When a clear here will shape the realm again for this player; null for a capture.</summary>
    DateTime? WorldRewardsBackAt = null
);

/// <summary>
/// One site this player has cleared (<c>SiteRotationRules</c>): when they may fight it again, and
/// when clearing it will shape the realm again. The region view greys a locked site and the panel
/// says when its realm rewards return.
/// </summary>
public record SiteClearDto(string SiteId, DateTime LockedUntil, DateTime WorldRewardsBackAt);

public record SiteClearsResponse(List<SiteClearDto> Clears, DateTime ServerNow);
