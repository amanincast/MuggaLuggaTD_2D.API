using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.DTOs;

public record CreateGameInstanceRequest(
    [Required][MaxLength(100)] string Name
);

public record UpdateGameInstanceRequest(
    [Required][MaxLength(100)] string Name
);

public record GameInstanceResponse(
    Guid Id,
    string Name,
    string OwnerId,
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
    DateTime CreatedAt,
    DateTime UpdatedAt
);
