using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// A code that lets someone register, while the game is open to invited testers only
/// (<c>Registration:RequireInviteCode</c>). Minted from the command line
/// (<c>invite-codes --count 5</c>, see <see cref="Services.InviteCodeService"/>), never over HTTP,
/// so there is no admin surface to attack.
///
/// <para>The game build is useless without an account, so this - not the download - is the gate.</para>
/// </summary>
public class InviteCode
{
    [Key]
    public int Id { get; set; }

    /// <summary>Normalised: upper case, <c>XXXX-XXXX</c> (<see cref="Services.InviteCodeService.Normalize"/>).</summary>
    [Required]
    [MaxLength(32)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Who it was made for, so a list of codes reads as a list of people.</summary>
    [MaxLength(200)]
    public string? Note { get; set; }

    public int MaxUses { get; set; } = 1;

    /// <summary>A concurrency token: two registrations racing for the last use cannot both win.</summary>
    [ConcurrencyCheck]
    public int Uses { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Null for a code that never expires.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>A revoked code admits nobody new; accounts already made with it are untouched.</summary>
    public bool Revoked { get; set; }
}
