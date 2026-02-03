using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.DTOs;

public record CreateAllianceRequest(
    [Required][MaxLength(100)] string Name,
    object? GameData = null
);

public record UpdateAllianceRequest(
    [MaxLength(100)] string? Name = null,
    object? GameData = null
);

public record AllianceResponse(
    Guid Id,
    Guid GameInstanceId,
    string Name,
    object GameData,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record AllianceListResponse(
    IEnumerable<AllianceSummary> Alliances
);

public record AllianceSummary(
    Guid Id,
    Guid GameInstanceId,
    string Name,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
