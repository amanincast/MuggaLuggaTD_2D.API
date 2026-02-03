using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.DTOs;

public record SavePlayerGameDataRequest(
    [Required] object GameData
);

public record PlayerGameDataResponse(
    Guid Id,
    Guid GameInstanceId,
    string UserId,
    object GameData,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record PlayerGameDataListResponse(
    IEnumerable<PlayerGameDataSummary> PlayerData
);

public record PlayerGameDataSummary(
    Guid Id,
    Guid GameInstanceId,
    string UserId,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
