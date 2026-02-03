using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.DTOs;

public record SaveWorldViewGameDataRequest(
    [Required] object GameData
);

public record WorldViewGameDataResponse(
    Guid Id,
    Guid GameInstanceId,
    object GameData,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
