using System.ComponentModel.DataAnnotations;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.DTOs;

public record CreateGameInstanceRequest(
    [Required][MaxLength(100)] string Name,
    GameInstanceAccessType AccessType = GameInstanceAccessType.Public,
    [Range(1, int.MaxValue)] int Capacity = 10,
    /// <summary>
    /// How long a season of this realm runs. The creator sets it because a realm's pace belongs to
    /// the group playing it - a weekend group and a month-long group want different worlds - and
    /// because it is what gives the realm an ending, which is what makes winning mean anything.
    /// </summary>
    [Range(GameInstance.MinimumSeasonDays, GameInstance.MaximumSeasonDays)]
    int SeasonLengthDays = GameInstance.DefaultSeasonDays
);

/// <summary>
/// Deliberately without the season length. Moving the finish line in the middle of a race changes
/// who wins it, and every score already earned was earned against the length that was announced.
/// A different pace is a different realm.
/// </summary>
public record UpdateGameInstanceRequest(
    [Required][MaxLength(100)] string Name,
    GameInstanceAccessType AccessType = GameInstanceAccessType.Public,
    [Range(1, int.MaxValue)] int Capacity = 10
);

public record GameInstanceResponse(
    Guid Id,
    string Name,
    string OwnerId,
    GameInstanceAccessType AccessType,
    int Capacity,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int SeasonLengthDays,
    int SeasonNumber,
    DateTime SeasonEndsAt
);

public record GameInstanceListResponse(
    IEnumerable<GameInstanceSummary> GameInstances
);

public record GameInstanceSummary(
    Guid Id,
    string Name,
    string OwnerId,
    GameInstanceAccessType AccessType,
    int Capacity,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int SeasonLengthDays,
    int SeasonNumber,
    /// <summary>When this realm's current season closes - the clock a player is choosing between.</summary>
    DateTime SeasonEndsAt
);
