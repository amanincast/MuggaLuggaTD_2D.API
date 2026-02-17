namespace MuggaLuggaTD_2D.API.DTOs;

public record WorldViewGameDataUpdated(
    Guid GameInstanceId,
    object GameData,
    DateTime UpdatedAt
);
