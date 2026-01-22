using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.DTOs;

public record SaveGameRequest(
    [Required][MaxLength(100)] string SlotName,
    [Required] object GameData
);

public record GameSaveResponse(
    Guid Id,
    string SlotName,
    object GameData,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record GameSaveListResponse(
    IEnumerable<GameSaveSummary> Saves
);

public record GameSaveSummary(
    Guid Id,
    string SlotName,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
