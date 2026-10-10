using System.ComponentModel.DataAnnotations;
using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One rift in one realm's season (Invasions spec §5.1, revision R5). A Great Rift is the realm's
/// shared event; a Lesser Rift belongs to one player's region. <see cref="RiftRules"/> decides when
/// and where; <see cref="Services.RiftService"/> moves it through its stages.
/// </summary>
public class Rift : IRevisioned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GameInstanceId { get; set; }

    public int SeasonNumber { get; set; }

    public RiftKind Kind { get; set; }

    /// <summary>The rift's number among its kind this season, from 1: the subject of its rolls.</summary>
    public int EventNumber { get; set; }

    [MaxLength(32)]
    public string RegionId { get; set; } = string.Empty;

    /// <summary>A Lesser Rift's player: the region's holder when it was announced. Null for a Great Rift.</summary>
    [MaxLength(450)]
    public string? TargetUserId { get; set; }

    public RiftStage Stage { get; set; }

    public DateTime OmenAt { get; set; }
    public DateTime OpensAt { get; set; }
    public DateTime ClosesAt { get; set; }

    /// <summary>When a Lesser Rift's region was invaded (it was not sealed in time).</summary>
    public DateTime? InvadedAt { get; set; }

    /// <summary>A Great Rift's Breach when it opened, and what is left of it. A Lesser Rift has 1: one win.</summary>
    public int BreachMax { get; set; }
    public int Breach { get; set; }

    public DateTime? SealedAt { get; set; }

    [MaxLength(450)]
    public string? SealedByUserId { get; set; }

    [ConcurrencyCheck]
    public long Revision { get; set; }
}

/// <summary>One player's part in one rift: what they took off its Breach, and their chest once it is sealed.</summary>
public class RiftContribution : IRevisioned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RiftId { get; set; }

    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    public int Breach { get; set; }

    public int Wins { get; set; }

    public DateTime? LastWinAt { get; set; }

    /// <summary>The chest this share earned when the rift was sealed; rolled when opened.</summary>
    public ItemRarityTypes? ChestRarity { get; set; }

    public DateTime? ChestOpenedAt { get; set; }

    [ConcurrencyCheck]
    public long Revision { get; set; }
}
