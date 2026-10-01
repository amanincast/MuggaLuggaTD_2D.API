namespace MuggaLuggaTD_2D.API.DTOs;

public record FortifyRequest(string RegionId, string SharedContractVersion);

/// <summary>Works begun: from and to which level, when they finish, and the goods they cost.</summary>
public record FortifyResponse(
    string RegionId,
    int FromLevel,
    int ToLevel,
    DateTime StartedAt,
    DateTime CompletesAt,
    Dictionary<string, int> Spent);
