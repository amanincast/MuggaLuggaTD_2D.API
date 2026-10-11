using System.ComponentModel.DataAnnotations;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// A Rift contract (Invasions spec R7): a Lesser Rift's lord hands the right to close it to another player,
/// who pays for it (Sell) or is paid (Hire). The server holds the gold in <see cref="Escrow"/> until the rift is
/// closed by the holder, when it goes where the contract says, or the contract ends another way, when it goes back.
///
/// <para>Not tied to the rift by a foreign key: a season's end deletes its rifts, and the gold its contracts hold
/// is handed back after that (<c>RiftService.ExpireContractsAsync</c>), as the Bazaar's is.</para>
/// </summary>
public class RiftContract : IRevisioned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GameInstanceId { get; set; }

    public Guid RiftId { get; set; }

    [MaxLength(32)]
    public string RegionId { get; set; } = string.Empty;

    public RiftContractKind Kind { get; set; }

    public RiftContractStatus Status { get; set; }

    /// <summary>The lord who posted it.</summary>
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>The Sell price or the Hire fee, in gold.</summary>
    public long Price { get; set; }

    public DateTime PostedAt { get; set; }

    /// <summary>Who took it, while it is taken.</summary>
    [MaxLength(450)]
    public string? HolderUserId { get; set; }

    public DateTime? TakenAt { get; set; }

    /// <summary>When the holder's right to close the rift runs out (<see cref="RiftRules.ContractHold"/> after taking it).</summary>
    public DateTime? HeldUntil { get; set; }

    /// <summary>The gold the server holds for it now: a Hire's fee from posting, a Sell's price once taken.</summary>
    public long Escrow { get; set; }

    public DateTime? EndedAt { get; set; }

    [ConcurrencyCheck]
    public long Revision { get; set; }
}
