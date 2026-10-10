using System.ComponentModel.DataAnnotations;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// One realm's goal for one UTC day (Active Content B, <see cref="RealmGoalRules"/>). Made the first
/// time the day's goal is asked for or advanced; what it asks is a pure function of the realm and the
/// day, so two requests that race to make it make the same row.
/// </summary>
public class RealmGoal : IRevisioned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GameInstanceId { get; set; }

    /// <summary>The UTC day, yyyymmdd.</summary>
    public int Day { get; set; }

    public RealmGoalKind Kind { get; set; }

    [MaxLength(64)]
    public string? Subject { get; set; }

    public int Target { get; set; }

    public int Count { get; set; }

    /// <summary>How many quarters have been announced in the war log (0 to 4).</summary>
    public int QuartersAnnounced { get; set; }

    /// <summary>When the realm reached it; the chests were paid then.</summary>
    public DateTime? ReachedAt { get; set; }

    [ConcurrencyCheck]
    public long Revision { get; set; }
}

/// <summary>What one player has done toward one day's goal.</summary>
public class RealmGoalShare : IRevisioned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GoalId { get; set; }

    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    public int Count { get; set; }

    /// <summary>The chest paid when the goal was reached, if this share earned one.</summary>
    public Enums.ItemRarityTypes? ChestRarity { get; set; }

    [ConcurrencyCheck]
    public long Revision { get; set; }
}
