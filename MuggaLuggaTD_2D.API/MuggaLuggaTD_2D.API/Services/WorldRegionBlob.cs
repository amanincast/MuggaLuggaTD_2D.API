using System.Text.Json;
using System.Text.Json.Nodes;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Reads and edits the region-shaped world blob (format 2).
///
/// <para>The blob stores regions, not their contents. A region carries a seed, and
/// <see cref="RegionGenerator"/> rebuilds its sites from that seed whenever they are needed — here
/// on the server to judge a claim, and on the client to draw the place. Only what has diverged from
/// the generated region is written back, as overrides keyed by site id.</para>
///
/// <para>Edits are JsonNode surgery rather than deserialise-mutate-reserialise, and deliberately so:
/// the client writes fields the server has no model for, and a round trip through a server-side type
/// would quietly drop them. Only the fields the server is authoritative for are touched.</para>
/// </summary>
public static class WorldRegionBlob
{
    /// <summary>Blobs written before the region map. They are regenerated rather than migrated.</summary>
    public const int LegacyFormatVersion = 1;

    /// <summary>
    /// Version 3 reshaped the terrain; version 4 reshaped the region itself, to 32x18.
    ///
    /// <para>Bumping this regenerates rather than migrates, which is the point. A region's sites are
    /// rebuilt from its seed, so changing <see cref="RegionGenerator"/> re-rolls where they stand —
    /// and an older blob's overrides are keyed by site id, so keeping them would attach "this
    /// dungeon is cleared" to whatever site now happens to hold that index. Regenerating loses the
    /// world's progress; carrying it forward would silently corrupt it.</para>
    /// </summary>
    public const int CurrentFormatVersion = 4;

    public static int GetFormatVersion(JsonNode? world)
    {
        var version = world?["FormatVersion"];
        return version == null ? LegacyFormatVersion : version.GetValue<int>();
    }

    public static bool IsRegionWorld(JsonNode? world) => GetFormatVersion(world) >= CurrentFormatVersion;

    public static JsonArray? GetRegions(JsonNode? world) => world?["Regions"] as JsonArray;

    public static JsonNode? FindRegion(JsonNode? world, string? regionId)
    {
        if (string.IsNullOrEmpty(regionId)) return null;

        var regions = GetRegions(world);
        if (regions == null) return null;

        foreach (var region in regions)
        {
            if (region?["RegionId"]?.GetValue<string>() == regionId)
                return region;
        }

        return null;
    }

    /// <summary>
    /// Reads a region node into the shared model, which is what the generator needs to rebuild the
    /// interior. Only the fields that drive generation and ownership are read.
    /// </summary>
    public static WorldRegionData ReadRegion(JsonNode region)
    {
        return new WorldRegionData
        {
            RegionId = region["RegionId"]?.GetValue<string>() ?? string.Empty,
            Seed = region["Seed"]?.GetValue<int>() ?? 0,
            Biome = (BiomeType)(region["Biome"]?.GetValue<int>() ?? 0),
            Tier = region["Tier"]?.GetValue<int>() ?? 1,
            Hex = new HexCoord(
                region["Hex"]?["Q"]?.GetValue<int>() ?? 0,
                region["Hex"]?["R"]?.GetValue<int>() ?? 0),
            Ownership = (LocationOwnership)(region["Ownership"]?.GetValue<int>() ?? 0),
            OwnerUserId = region["OwnerUserId"]?.GetValue<string>(),
            OwnerDisplayName = region["OwnerDisplayName"]?.GetValue<string>(),
            Faction = (FactionId)(region["Faction"]?.GetValue<int>() ?? 0),
            IsCapital = region["IsCapital"]?.GetValue<bool>() ?? false,
            Entrenchment = region["Entrenchment"]?.GetValue<int>() ?? 0,
            Resolve = region["Resolve"]?.GetValue<int>() ?? 100,
            SiteOverrides = ReadOverrides(region)
        };
    }

    /// <summary>
    /// The region's site overrides, as the shared model.
    ///
    /// <para>Read because a region's defence is the sum of what stands at its sites, and the server
    /// has to compute that itself to judge a raid — <see cref="RegionHoldCalculator.GarrisonPowerOf"/>
    /// reads it straight off the model. Only the fields the server acts on are parsed; the blob
    /// itself stays the source of truth and is edited in place, so anything not read here is still
    /// preserved on write.</para>
    /// </summary>
    private static Dictionary<string, SiteOverride> ReadOverrides(JsonNode region)
    {
        var result = new Dictionary<string, SiteOverride>(StringComparer.Ordinal);
        if (region["SiteOverrides"] is not JsonObject overrides) return result;

        foreach (var (siteId, node) in overrides)
        {
            if (node is not JsonObject entry) continue;

            result[siteId] = new SiteOverride
            {
                Cleared = entry["Cleared"]?.GetValue<bool>() ?? false,
                Repaired = entry["Repaired"]?.GetValue<bool>() ?? false,
                GarrisonPower = entry["GarrisonPower"]?.GetValue<float>() ?? 0f,
                GarrisonCharacterIds = ReadStrings(entry["GarrisonCharacterIds"]),
                CapturedCharacterIds = ReadStrings(entry["CapturedCharacterIds"])
            };
        }

        return result;
    }

    private static List<string> ReadStrings(JsonNode? node)
    {
        var result = new List<string>();
        if (node is not JsonArray array) return result;

        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text))
                result.Add(text);
        }

        return result;
    }

    /// <summary>Every region in the blob, as the shared model. Used for supply and for the map view.</summary>
    public static List<WorldRegionData> ReadAllRegions(JsonNode? world)
    {
        var result = new List<WorldRegionData>();
        var regions = GetRegions(world);
        if (regions == null) return result;

        foreach (var region in regions)
        {
            if (region != null)
                result.Add(ReadRegion(region));
        }

        return result;
    }

    // -----------------------------------------------------------------
    // Sites
    // -----------------------------------------------------------------

    /// <summary>
    /// Resolves a site id against the live world: finds its region, rebuilds the interior from the
    /// seed and returns the site, along with whatever has since happened to it.
    ///
    /// <para>This is the heart of validating a PvE claim. The client names a site; the server does
    /// not take its word for what that site is, it regenerates the region and looks.</para>
    /// </summary>
    public static SiteResolution? ResolveSite(JsonNode? world, string? siteId)
    {
        var regionId = SiteSpec.RegionIdOf(siteId);
        if (regionId == null) return null;

        var regionNode = FindRegion(world, regionId);
        if (regionNode == null) return null;

        var region = ReadRegion(regionNode);
        var layout = RegionGenerator.Generate(region);
        var site = layout.FindSite(siteId!);
        if (site == null) return null;

        return new SiteResolution(regionNode, region, site, GetOverride(regionNode, siteId!));
    }

    public static JsonNode? GetOverride(JsonNode regionNode, string siteId)
    {
        return regionNode["SiteOverrides"]?[siteId];
    }

    /// <summary>Returns the override for a site, creating it if this is the first change to it.</summary>
    public static JsonObject EnsureOverride(JsonNode regionNode, string siteId)
    {
        if (regionNode["SiteOverrides"] is not JsonObject overrides)
        {
            overrides = new JsonObject();
            regionNode["SiteOverrides"] = overrides;
        }

        if (overrides[siteId] is not JsonObject entry)
        {
            entry = new JsonObject();
            overrides[siteId] = entry;
        }

        return entry;
    }

    public static bool IsCleared(JsonNode? siteOverride)
    {
        return siteOverride?["Cleared"]?.GetValue<bool>() ?? false;
    }

    /// <summary>
    /// Marks a dungeon or portal as spent. The region map's answer to removing a location: the site
    /// still generates from the seed, so it cannot simply be deleted, but it no longer offers a fight.
    /// </summary>
    public static void MarkCleared(JsonNode regionNode, string siteId)
    {
        EnsureOverride(regionNode, siteId)["Cleared"] = true;
    }

    // -----------------------------------------------------------------
    // Regions
    // -----------------------------------------------------------------

    /// <summary>
    /// Sets a region's resolve, keeping it inside its bounds.
    ///
    /// <para>Resolve is the region's morale and multiplies the whole of its hold. Raiding wears it
    /// down; clearing the region's own fightable sites builds it back. It is the only part of a
    /// region a rival can move without taking it.</para>
    /// </summary>
    public static int SetResolve(JsonNode regionNode, int resolve)
    {
        int clamped = RegionResolveRules.Clamp(resolve);
        regionNode["Resolve"] = clamped;
        return clamped;
    }

    /// <summary>
    /// Ids the given user cannot march with: stationed at one of their own regions, or held prisoner
    /// anywhere in the world.
    ///
    /// <para>Read from the world blob rather than taken from the request, so a client cannot field a
    /// champion it has already committed elsewhere. The region equivalent of what the flat world did
    /// per location — garrisons now live in each site's override.</para>
    /// </summary>
    public static HashSet<string> CollectCommittedCharacterIds(JsonNode? world, string userId)
    {
        var committed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var region in ReadAllRegions(world))
        {
            bool mine = region.IsOwnedByPlayer(userId);

            foreach (var over in region.SiteOverrides.Values)
            {
                if (over == null) continue;

                // Only your own garrisons tie your champions up; another player's garrison is made
                // of their characters, not yours.
                if (mine && over.GarrisonCharacterIds != null)
                {
                    foreach (var id in over.GarrisonCharacterIds) committed.Add(id);
                }

                // Prisoners are held wherever they were taken, whoever owns it now.
                if (over.CapturedCharacterIds != null)
                {
                    foreach (var id in over.CapturedCharacterIds) committed.Add(id);
                }
            }
        }

        return committed;
    }

    /// <summary>
    /// Hands a region to a player. Taking the keep takes the region, which is the split the design
    /// rests on: regions are owned, sites are cleared or claimed.
    /// </summary>
    public static void CaptureRegion(JsonNode regionNode, string userId, string? displayName)
    {
        regionNode["Ownership"] = (int)LocationOwnership.Player;
        regionNode["OwnerUserId"] = userId;
        regionNode["OwnerDisplayName"] = displayName ?? string.Empty;
        regionNode["Faction"] = (int)FactionId.Player;

        // A region taken by force does not come with its previous owner's morale.
        regionNode["Resolve"] = 100;
    }

    // -----------------------------------------------------------------
    // Writing a new world
    // -----------------------------------------------------------------

    /// <summary>Serialises a freshly generated world into the blob shape the client reads.</summary>
    public static JsonObject BuildWorld(int worldSeed, IReadOnlyList<WorldRegionData> regions)
    {
        var array = new JsonArray();
        foreach (var region in regions)
            array.Add(WriteRegion(region));

        return new JsonObject
        {
            ["FormatVersion"] = CurrentFormatVersion,
            ["WorldSeed"] = worldSeed,
            ["Regions"] = array
        };
    }

    private static JsonObject WriteRegion(WorldRegionData region)
    {
        return new JsonObject
        {
            ["RegionId"] = region.RegionId,
            ["Hex"] = new JsonObject { ["Q"] = region.Hex.Q, ["R"] = region.Hex.R },
            ["Seed"] = region.Seed,
            ["Biome"] = (int)region.Biome,
            ["Tier"] = region.Tier,
            ["Ownership"] = (int)region.Ownership,
            ["OwnerUserId"] = region.OwnerUserId ?? string.Empty,
            ["OwnerDisplayName"] = region.OwnerDisplayName ?? string.Empty,
            ["Faction"] = (int)region.Faction,
            ["IsCapital"] = region.IsCapital,
            ["Entrenchment"] = region.Entrenchment,
            ["Resolve"] = region.Resolve,
            ["SiteOverrides"] = new JsonObject()
        };
    }
}

/// <summary>A site found in the live world, with its region and whatever has happened to it.</summary>
public record SiteResolution(JsonNode RegionNode, WorldRegionData Region, SiteSpec Site, JsonNode? Override)
{
    public bool IsCleared => WorldRegionBlob.IsCleared(Override);
}
