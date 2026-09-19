using System.Text.Json.Nodes;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Combines a player's incoming per-instance save with what is already stored.
///
/// Two independent client systems write the same PlayerGameData blob through the same endpoint:
/// the roster/inventory save (<c>Characters</c>, <c>InventoryItems</c>, …) and fog-of-war discovery
/// (<c>DiscoveredLocationIds</c>). Each only knows its own fields, so replacing the blob wholesale let
/// every discovery save erase the player's party in that world, and every party save erase their fog.
///
/// Merging by top-level property lets each writer own its fields: a property present in the
/// incoming document replaces the stored one, and anything the writer didn't send is kept.
/// </summary>
public static class PlayerDataMerger
{
    /// <returns>
    /// The merged document. If either side is not a JSON object there is nothing to merge
    /// field-by-field, and the incoming document wins as before.
    /// </returns>
    public static JsonNode? Merge(string? storedJson, JsonNode? incoming)
    {
        if (incoming is not JsonObject incomingObject || string.IsNullOrWhiteSpace(storedJson))
            return incoming;

        JsonNode? stored;
        try
        {
            stored = JsonNode.Parse(storedJson);
        }
        catch (System.Text.Json.JsonException)
        {
            // A corrupt stored blob can't be merged into; the fresh save replaces it.
            return incoming;
        }

        if (stored is not JsonObject merged)
            return incoming;

        foreach (var (key, value) in incomingObject)
            merged[key] = value?.DeepClone();

        return merged;
    }
}
