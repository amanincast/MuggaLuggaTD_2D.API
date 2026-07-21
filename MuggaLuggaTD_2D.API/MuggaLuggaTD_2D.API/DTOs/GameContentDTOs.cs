using System.Text.Json.Nodes;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>Full game content payload: every document plus the version that identifies the set.</summary>
public record GameContentResponse(
    string Version,
    IReadOnlyDictionary<string, JsonNode> Documents
);

/// <summary>
/// Just the content version. Clients poll this to decide whether their cached content is current
/// before joining a shared game instance.
/// </summary>
public record GameContentVersionResponse(
    string Version
);
