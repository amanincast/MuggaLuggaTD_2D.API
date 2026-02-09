using System.ComponentModel.DataAnnotations;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.DTOs;

public record CreateGameInstanceRequest(
    [Required][MaxLength(100)] string Name,
    GameInstanceAccessType AccessType = GameInstanceAccessType.Public,
    [Range(1, int.MaxValue)] int Capacity = 10
);

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
    DateTime UpdatedAt
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
    DateTime UpdatedAt
);
