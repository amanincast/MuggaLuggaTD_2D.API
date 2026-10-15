using System.ComponentModel.DataAnnotations;
using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// A player's place in one week of the Weekly Trial (Active Content E, <see cref="TrialRules"/>): their best try, and
/// the chest the week paid them once it was over. One row per player per week, across every realm.
/// </summary>
public class TrialEntry : IRevisioned
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public int Week { get; set; }

    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    public long BestScore { get; set; }
    public int BestKills { get; set; }
    public int BestWaves { get; set; }
    public int BestSeconds { get; set; }

    /// <summary>Which of the week's four heroes the best try was fought with.</summary>
    public int BestHero { get; set; }

    public DateTime? BestAt { get; set; }

    /// <summary>Set once the week is over and its chest paid.</summary>
    public DateTime? PaidAt { get; set; }

    /// <summary>The realm the chest was paid into: where the player next came to the Hall after the week ended.</summary>
    public Guid? PaidInstanceId { get; set; }

    public ItemRarityTypes? ChestRarity { get; set; }
    public int? FinalRank { get; set; }
    public int? FinalTotal { get; set; }

    [ConcurrencyCheck]
    public long Revision { get; set; }
}

/// <summary>
/// One try at the Weekly Trial: begun (it spends one of the day's tries) and, when the fight ends, finished with what
/// it scored. A try never finished scores nothing. <see cref="Trimmed"/> marks one the server had to hold to what
/// could have happened (<see cref="TrialRules.Check"/>), logged as an outlier.
/// </summary>
public class TrialAttempt
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    public int Week { get; set; }

    /// <summary>The UTC day it was begun on (<see cref="TrialRules.DayOf"/>): three a day.</summary>
    public int Day { get; set; }

    public int Hero { get; set; }

    /// <summary>The realm it was begun from (for the log; the trial is the same in every realm).</summary>
    public Guid? GameInstanceId { get; set; }

    public DateTime BegunAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public int Kills { get; set; }
    public int Waves { get; set; }
    public int Seconds { get; set; }
    public long Score { get; set; }

    /// <summary>What the client said, before the check, when it had to be trimmed.</summary>
    [MaxLength(64)]
    public string? Reported { get; set; }

    public bool Trimmed { get; set; }
}
