using System.Text.Json.Nodes;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Reads and mutates the shared world blob.
///
/// The blob is edited as a JsonNode rather than round-tripped through a typed model on purpose: the
/// client owns the world schema, so the server touches only the fields it is authoritative for and
/// leaves everything else — including fields it does not know about — byte-for-byte intact.
///
/// Shared by PvP and PvE conquest so both apply a capture the same way.
/// </summary>
public static class WorldBlobEditor
{
    public static JsonArray? GetLocations(JsonNode? world) => world?["Locations"] as JsonArray;

    public static JsonNode? FindLocation(JsonNode? world, string locationId)
    {
        return GetLocations(world)?.FirstOrDefault(l =>
            string.Equals(l?["LocationId"]?.GetValue<string>(), locationId, StringComparison.Ordinal));
    }

    public static LocationType GetLocationType(JsonNode location)
        => (LocationType)(location["Type"]?.GetValue<int>() ?? -1);

    public static LocationOwnership GetOwnership(JsonNode location)
        => (LocationOwnership)(location["Ownership"]?.GetValue<int>() ?? 0);

    public static string? GetOwnerUserId(JsonNode location)
        => location["OwnerUserId"]?.GetValue<string>();

    /// <summary>
    /// Flips a location to <paramref name="userId"/> and seizes any garrison stationed there — the
    /// defenders become prisoners held at the location, rescuable if their owner retakes it.
    /// </summary>
    public static void Capture(JsonNode location, string userId, string? displayName)
    {
        var garrison = location["GarrisonCharacterIds"] as JsonArray;
        if (garrison is { Count: > 0 })
        {
            AddCaptured(location, ReadStrings(garrison));
            location["GarrisonCharacterIds"] = new JsonArray();
            location["GarrisonPower"] = JsonValue.Create(0f);
        }

        location["Ownership"] = JsonValue.Create((int)LocationOwnership.Player);
        location["OwnerUserId"] = JsonValue.Create(userId);
        location["OwnerDisplayName"] = JsonValue.Create(displayName);
    }

    /// <summary>Removes a cleared location (Dungeon/Portal) from the world.</summary>
    public static bool RemoveLocation(JsonNode? world, string locationId)
    {
        var locations = GetLocations(world);
        if (locations == null) return false;

        for (int i = 0; i < locations.Count; i++)
        {
            if (string.Equals(locations[i]?["LocationId"]?.GetValue<string>(), locationId, StringComparison.Ordinal))
            {
                locations.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Records characters as imprisoned at this location, without duplicating existing ids.</summary>
    public static void AddCaptured(JsonNode location, IEnumerable<string> characterIds)
    {
        var captured = location["CapturedCharacterIds"] as JsonArray ?? new JsonArray();
        var existing = ReadStrings(captured).ToHashSet(StringComparer.Ordinal);

        foreach (var id in characterIds)
            if (existing.Add(id)) captured.Add(JsonValue.Create(id));

        location["CapturedCharacterIds"] = captured;
    }

    /// <summary>
    /// Ids the given user cannot field: garrisoned at one of their own locations, or held prisoner
    /// anywhere. Read from the world blob so the client cannot claim otherwise.
    /// </summary>
    public static HashSet<string> CollectCommittedCharacterIds(JsonNode? world, string userId)
    {
        var committed = new HashSet<string>(StringComparer.Ordinal);
        var locations = GetLocations(world);
        if (locations == null) return committed;

        foreach (var location in locations)
        {
            if (location == null) continue;

            if (string.Equals(GetOwnerUserId(location), userId, StringComparison.Ordinal)
                && location["GarrisonCharacterIds"] is JsonArray garrison)
            {
                foreach (var id in ReadStrings(garrison)) committed.Add(id);
            }

            // Prisoners are held at whichever location captured them, regardless of who owns it.
            if (location["CapturedCharacterIds"] is JsonArray captured)
            {
                foreach (var id in ReadStrings(captured)) committed.Add(id);
            }
        }

        return committed;
    }

    private static IEnumerable<string> ReadStrings(JsonArray array)
        => array.Select(n => n?.GetValue<string>()).OfType<string>();
}
