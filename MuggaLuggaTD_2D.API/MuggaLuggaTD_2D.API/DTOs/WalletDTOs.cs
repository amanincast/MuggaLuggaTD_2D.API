using System.ComponentModel.DataAnnotations;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>One material balance.</summary>
public record MaterialBalance(string MaterialName, int Quantity);

/// <summary>Everything the player holds in this realm: materials, and the purse.</summary>
public record WalletResponse(
    List<MaterialBalance> Materials,
    /// <summary>
    /// Gold in hand, including whatever the players land has accrued since it was last settled.
    /// Projected at read time rather than banked, so asking for a balance is never what makes it
    /// correct.
    /// </summary>
    long Gold = 0,
    /// <summary>
    /// What the players holdings are currently paying per hour. Shown so land reads as an income
    /// rather than as a number that quietly grows.
    /// </summary>
    double GoldPerHour = 0);

/// <summary>
/// A request to consume materials — merging an item today, and hiring at the Tavern later.
///
/// <para>The reason is for the log, not for validation: spending only ever destroys the caller's own
/// materials, so there is nothing to gain by lying about why. What the server guards is the other
/// direction — nothing here can add to a balance.</para>
/// </summary>
public record WalletSpendRequest(
    [Required] List<MaterialGrant> Materials,
    string? Reason,
    [Required] string SharedContractVersion
);
