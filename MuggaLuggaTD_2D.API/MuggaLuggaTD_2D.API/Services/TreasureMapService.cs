using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>A map as its holder sees it: where it points, what guards the cache, and whether it has been opened.</summary>
public record TreasureMapView(
    Guid Id, string SiteId, string RegionId, string LandmarkSiteId, BiomeType Biome, int Tier, int Level,
    DateTime FoundAt, string FoundAtSiteId, bool Chained, DateTime? OpenedAt);

public record TreasureMapsResponse(List<TreasureMapView> Held, List<TreasureMapView> Opened);

/// <summary>A map held and its cache's terms, for the fight.</summary>
public record TreasureMapState(TreasureMap Map, WorldRegionData Region, SiteSpec? Landmark, int Tier, int Level);

/// <summary>
/// Treasure maps (Active Content D): finding one on a clear, the holder's list, and opening a cache once. The fight
/// itself goes through pve begin/claim (<see cref="WorldPveService"/>, <c>cache:{map id}</c>).
/// </summary>
public class TreasureMapService
{
    /// <summary>How long an opened map stays in the holder's list.</summary>
    private static readonly TimeSpan OpenedShownFor = TimeSpan.FromHours(24);

    private readonly ApplicationDbContext _context;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<TreasureMapService> _logger;

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public TreasureMapService(ApplicationDbContext context, ISessionLog sessionLog, ILogger<TreasureMapService> logger)
    {
        _context = context;
        _sessionLog = sessionLog;
        _logger = logger;
    }

    /// <summary>
    /// A clear's chance at a map: <paramref name="roll"/> under the site's chance finds one, if the player holds fewer than
    /// <see cref="TreasureMapRules.MostHeld"/>. Returns the map found, or null.
    /// </summary>
    public async Task<TreasureMapView?> TryFindAsync(Guid gameInstanceId, string userId, LocationType site, string foundAtSiteId,
        double roll, JsonNode? world = null)
    {
        if (roll >= TreasureMapRules.ChanceFrom(site)) return null;
        return await GrantAsync(gameInstanceId, userId, foundAtSiteId, null, world);
    }

    /// <summary>Gives the player a map, pointing where its id says; null when they hold the most already, or nowhere will do.</summary>
    public async Task<TreasureMapView?> GrantAsync(Guid gameInstanceId, string userId, string foundAtSiteId, Guid? chainedFrom,
        JsonNode? world = null, string? regionId = null)
    {
        var instance = await _context.GameInstances.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        if (instance == null) return null;

        int held = await _context.TreasureMaps.CountAsync(m => m.GameInstanceId == gameInstanceId && m.UserId == userId
            && m.SeasonNumber == instance.SeasonNumber && m.OpenedAt == null);
        if (held >= TreasureMapRules.MostHeld) return null;

        world ??= await ReadWorldAsync(gameInstanceId);
        if (world == null) return null;
        var regions = WorldRegionBlob.ReadAllRegions(world);

        var map = new TreasureMap
        {
            GameInstanceId = gameInstanceId,
            SeasonNumber = instance.SeasonNumber,
            UserId = userId,
            FoundAt = Clock(),
            FoundAtSiteId = Trim(foundAtSiteId, 64),
            ChainedFromId = chainedFrom,
        };
        var region = regionId != null
            ? regions.FirstOrDefault(r => r.RegionId == regionId)
            : TreasureMapRules.PickRegion(map.Id, TreasureMapRules.Eligible(regions, userId));
        var landmark = region == null ? null : TreasureMapRules.Landmark(map.Id, RegionGenerator.Generate(region));
        if (region == null || landmark == null) return null;

        map.RegionId = region.RegionId;
        map.LandmarkSiteId = landmark.SiteId;
        _context.TreasureMaps.Add(map);
        await _context.SaveChangesAsync();

        _sessionLog.Log("MAP-FOUND", $"realm={gameInstanceId} user={userId} map={map.Id} region={map.RegionId} at={map.FoundAtSiteId}");
        return ViewOf(map, region, landmark);
    }

    /// <summary>The player's maps this season: those held, and those opened in the last day.</summary>
    public async Task<TreasureMapsResponse> ViewAsync(Guid gameInstanceId, string userId)
    {
        var instance = await _context.GameInstances.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        if (instance == null) return new TreasureMapsResponse(new(), new());

        var since = Clock() - OpenedShownFor;
        var maps = await _context.TreasureMaps.AsNoTracking()
            .Where(m => m.GameInstanceId == gameInstanceId && m.UserId == userId && m.SeasonNumber == instance.SeasonNumber
                        && (m.OpenedAt == null || m.OpenedAt > since))
            .OrderBy(m => m.FoundAt)
            .ToListAsync();
        if (maps.Count == 0) return new TreasureMapsResponse(new(), new());

        var world = await ReadWorldAsync(gameInstanceId);
        var regions = world == null ? new Dictionary<string, WorldRegionData>()
            : WorldRegionBlob.ReadAllRegions(world).ToDictionary(r => r.RegionId);
        var views = maps
            .Where(m => regions.ContainsKey(m.RegionId))
            .Select(m =>
            {
                var region = regions[m.RegionId];
                var landmark = RegionGenerator.Generate(region).Sites?.FirstOrDefault(s => s.SiteId == m.LandmarkSiteId);
                return (m.OpenedAt, View: ViewOf(m, region, landmark));
            })
            .ToList();
        return new TreasureMapsResponse(
            views.Where(v => v.OpenedAt == null).Select(v => v.View).ToList(),
            views.Where(v => v.OpenedAt != null).OrderByDescending(v => v.OpenedAt).Select(v => v.View).ToList());
    }

    /// <summary>A map this player holds, unopened, this season, with its cache's terms; null otherwise.</summary>
    public async Task<TreasureMapState?> HeldAsync(Guid gameInstanceId, string userId, Guid mapId)
    {
        var instance = await _context.GameInstances.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        var map = await _context.TreasureMaps.FirstOrDefaultAsync(m => m.Id == mapId && m.GameInstanceId == gameInstanceId);
        if (instance == null || map == null || map.UserId != userId || map.OpenedAt != null || map.SeasonNumber != instance.SeasonNumber)
            return null;

        var world = await ReadWorldAsync(gameInstanceId);
        var region = world == null ? null : WorldRegionBlob.ReadAllRegions(world).FirstOrDefault(r => r.RegionId == map.RegionId);
        if (region == null) return null;
        var landmark = RegionGenerator.Generate(region).Sites?.FirstOrDefault(s => s.SiteId == map.LandmarkSiteId);
        var (tier, level) = TreasureMapRules.Terms(landmark);
        return new TreasureMapState(map, region, landmark, tier, level);
    }

    /// <summary>Opens the map's cache: true for the one request that does, false if it was already open.</summary>
    public async Task<bool> TryOpenAsync(Guid mapId)
    {
        bool opened = false;
        await Concurrency.RetryAsync(_context, async () =>
        {
            await Concurrency.RefreshAsync<TreasureMap>(_context, m => m.Id == mapId);
            var map = _context.TreasureMaps.Local.FirstOrDefault(m => m.Id == mapId)
                      ?? await _context.TreasureMaps.FirstOrDefaultAsync(m => m.Id == mapId);
            if (map == null || map.OpenedAt != null) { opened = false; return; }
            map.OpenedAt = Clock();
            await _context.SaveChangesAsync();
            opened = true;
        });
        return opened;
    }

    /// <summary>Development only: forgets this player's maps this season.</summary>
    public async Task DebugForgetAsync(Guid gameInstanceId, string userId)
    {
        var maps = await _context.TreasureMaps.Where(m => m.GameInstanceId == gameInstanceId && m.UserId == userId).ToListAsync();
        _context.TreasureMaps.RemoveRange(maps);
        await _context.SaveChangesAsync();
    }

    public static TreasureMapView ViewOf(TreasureMap map, WorldRegionData region, SiteSpec? landmark)
    {
        var (tier, level) = TreasureMapRules.Terms(landmark);
        return new TreasureMapView(map.Id, TreasureMapRules.SiteId(map.Id), map.RegionId, map.LandmarkSiteId, region.Biome,
            tier, level, DateTime.SpecifyKind(map.FoundAt, DateTimeKind.Utc), map.FoundAtSiteId, map.ChainedFromId != null,
            map.OpenedAt == null ? null : DateTime.SpecifyKind(map.OpenedAt.Value, DateTimeKind.Utc));
    }

    private async Task<JsonNode?> ReadWorldAsync(Guid gameInstanceId)
    {
        var row = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        return row == null ? null : JsonNode.Parse(row.GameData);
    }

    private static string Trim(string? text, int most) =>
        string.IsNullOrEmpty(text) ? "" : text.Length > most ? text[..most] : text;
}
