using System.Text.Json.Nodes;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Tests.TestSupport;

/// <summary>
/// Builds world blobs for tests.
///
/// <para>Regions are written with the production serialiser and their sites come from the real
/// <see cref="RegionGenerator"/>, never from a hand-written fixture. That is deliberate: a site id
/// only exists because the generator produced it, so a fabricated blob could describe a world the
/// server would never actually accept a claim against — and the test would prove nothing.</para>
/// </summary>
public static class TestWorld
{
    /// <summary>An arbitrary but fixed seed. Any seed works; a constant keeps failures reproducible.</summary>
    public const int RegionSeed = 20250920;

    /// <summary>
    /// A region nobody holds. Tier 2 so the budget places several dungeons, giving tests more than
    /// one fightable site to work with.
    /// </summary>
    public static WorldRegionData Region(
        string regionId = "r0",
        int q = 0,
        int r = 0,
        LocationOwnership ownership = LocationOwnership.Neutral,
        string? ownerUserId = null,
        int tier = 2,
        BiomeType biome = BiomeType.RiverVale)
    {
        return new WorldRegionData
        {
            RegionId = regionId,
            Hex = new HexCoord(q, r),
            // Vary the seed per region so two regions in one test world are not identical. Derived
            // by hand rather than from string.GetHashCode(), which is randomised per process and
            // would quietly make every run generate a different world.
            Seed = RegionSeed + StableOffset(regionId),
            Biome = biome,
            Tier = tier,
            Ownership = ownership,
            OwnerUserId = ownerUserId,
            OwnerDisplayName = ownerUserId,
            Faction = ownership == LocationOwnership.Player ? FactionId.Player : FactionId.None,
            Entrenchment = 1,
            Resolve = 100
        };
    }

    /// <summary>A region held by <paramref name="userId"/>.</summary>
    public static WorldRegionData OwnedBy(string userId, string regionId = "r0", int tier = 2)
        => Region(regionId, ownership: LocationOwnership.Player, ownerUserId: userId, tier: tier);

    /// <summary>A small, stable number derived from a region id.</summary>
    private static int StableOffset(string regionId)
    {
        int sum = 0;
        foreach (var c in regionId) sum = (sum * 31) + c;
        return Math.Abs(sum % 1000);
    }

    /// <summary>A blob in the current format containing exactly these regions.</summary>
    public static JsonObject Blob(params WorldRegionData[] regions)
        => WorldRegionBlob.BuildWorld(RegionSeed, regions);

    /// <summary>The sites the seed actually generates inside a region.</summary>
    public static IReadOnlyList<SiteSpec> SitesIn(WorldRegionData region)
        => RegionGenerator.Generate(region).Sites;

    /// <summary>
    /// The first site of a given type. Every region has exactly one Castle (its keep) and at least
    /// two Dungeons, so both lookups are safe for any region these helpers build.
    /// </summary>
    public static SiteSpec SiteOfType(WorldRegionData region, LocationType type)
    {
        var site = SitesIn(region).FirstOrDefault(s => s.Type == type);
        Assert.NotNull(site);
        return site!;
    }

    public static string DungeonIn(WorldRegionData region) => SiteOfType(region, LocationType.Dungeon).SiteId;

    public static string KeepIn(WorldRegionData region) => SiteOfType(region, LocationType.Castle).SiteId;

    /// <summary>The settlement — a site with no combat to offer, so never a PvE target.</summary>
    public static string SettlementIn(WorldRegionData region)
        => SiteOfType(region, LocationType.NeutralHome).SiteId;

    // -----------------------------------------------------------------
    // Reading a blob back
    // -----------------------------------------------------------------

    public static WorldRegionData ReadRegion(JsonNode? world, string regionId)
    {
        var node = WorldRegionBlob.FindRegion(world, regionId);
        Assert.NotNull(node);
        return WorldRegionBlob.ReadRegion(node!);
    }

    public static bool IsCleared(JsonNode? world, string siteId)
    {
        var regionNode = WorldRegionBlob.FindRegion(world, SiteSpec.RegionIdOf(siteId));
        return regionNode != null && WorldRegionBlob.IsCleared(WorldRegionBlob.GetOverride(regionNode, siteId));
    }

    /// <summary>A blob as it is actually stored and re-read, so a test sees the persistence round trip.</summary>
    public static JsonNode? RoundTrip(JsonNode world) => JsonNode.Parse(world.ToJsonString());
}
