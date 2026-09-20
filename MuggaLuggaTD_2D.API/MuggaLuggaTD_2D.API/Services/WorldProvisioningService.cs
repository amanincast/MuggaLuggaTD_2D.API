using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Owns the existence of a game instance's world.
///
/// <para>World generation used to be the client's job: it built the map and pushed the blob. With
/// regions that no longer works, because the server has to agree with the client about what is
/// inside a region in order to validate a claim against it. The server generates the world, and the
/// client renders what it is given.</para>
///
/// <para>The seed is derived from the instance id, so the same world is rebuilt rather than
/// remembered, and two servers handed the same instance would produce the same map.</para>
/// </summary>
public class WorldProvisioningService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<WorldProvisioningService> _logger;

    public WorldProvisioningService(ApplicationDbContext context, ILogger<WorldProvisioningService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Returns the instance's world, generating it if it does not exist and regenerating it if it
    /// predates the region map.
    ///
    /// <para>Pre-release worlds are regenerated rather than migrated: a format 1 blob is a flat list
    /// of locations with no regions to put them in, and writing a migration for test realms would be
    /// writing code to be used once and then deleted.</para>
    /// </summary>
    public async Task<WorldViewGameData> EnsureWorldAsync(Guid gameInstanceId)
    {
        var row = await _context.WorldViewGameData
            .FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);

        if (row != null && WorldRegionBlob.IsRegionWorld(JsonNode.Parse(row.GameData)))
            return row;

        var world = await GenerateWorldAsync(gameInstanceId);
        var json = world.ToJsonString();

        if (row == null)
        {
            row = new WorldViewGameData
            {
                GameInstanceId = gameInstanceId,
                GameData = json,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _context.WorldViewGameData.Add(row);
            _logger.LogInformation("Generated a new region world for instance {Instance}.", gameInstanceId);
        }
        else
        {
            row.GameData = json;
            row.UpdatedAt = DateTime.UtcNow;
            _logger.LogWarning(
                "Instance {Instance} had a pre-region world; it has been regenerated.", gameInstanceId);
        }

        await _context.SaveChangesAsync();
        return row;
    }

    /// <summary>
    /// Gives a player a capital if they do not have one, for someone who joins a world that was
    /// generated before they arrived. Without this a late joiner would open the map with nowhere to
    /// stand and no supply line to anywhere.
    /// </summary>
    public async Task<bool> EnsureSeatAsync(Guid gameInstanceId, string userId, string? displayName)
    {
        var row = await EnsureWorldAsync(gameInstanceId);
        var world = JsonNode.Parse(row.GameData);
        var regions = WorldRegionBlob.GetRegions(world);
        if (regions == null) return false;

        foreach (var region in regions)
        {
            if (region?["OwnerUserId"]?.GetValue<string>() == userId)
                return false; // Already seated.
        }

        var seat = ChooseSeat(regions);
        if (seat == null)
        {
            _logger.LogWarning("No unclaimed region left to seat {User} in instance {Instance}.", userId, gameInstanceId);
            return false;
        }

        WorldRegionBlob.CaptureRegion(seat, userId, displayName);
        seat["IsCapital"] = true;
        seat["Entrenchment"] = StartingEntrenchment;
        seat["Tier"] = 1;
        seat["Biome"] = (int)BiomeType.RiverVale;

        row.GameData = world!.ToJsonString();
        row.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Seated {User} at {Region} in instance {Instance}.",
            userId, seat["RegionId"], gameInstanceId);
        return true;
    }

    private const int StartingEntrenchment = 2;

    /// <summary>
    /// Picks the unclaimed region furthest from every seat already taken, so a late joiner is not
    /// dropped on a neighbour's doorstep.
    /// </summary>
    private static JsonNode? ChooseSeat(JsonArray regions)
    {
        var taken = new List<HexCoord>();
        foreach (var region in regions)
        {
            if (region?["IsCapital"]?.GetValue<bool>() == true)
            {
                taken.Add(new HexCoord(
                    region["Hex"]?["Q"]?.GetValue<int>() ?? 0,
                    region["Hex"]?["R"]?.GetValue<int>() ?? 0));
            }
        }

        JsonNode? best = null;
        int bestScore = -1;

        foreach (var region in regions)
        {
            if (region == null) continue;
            if ((LocationOwnership)(region["Ownership"]?.GetValue<int>() ?? 0) != LocationOwnership.Neutral) continue;

            var hex = new HexCoord(
                region["Hex"]?["Q"]?.GetValue<int>() ?? 0,
                region["Hex"]?["R"]?.GetValue<int>() ?? 0);

            int score = taken.Count == 0 ? 0 : taken.Min(t => HexCoord.Distance(hex, t));
            if (score <= bestScore) continue;

            bestScore = score;
            best = region;
        }

        return best;
    }

    private async Task<JsonObject> GenerateWorldAsync(Guid gameInstanceId)
    {
        var instance = await _context.GameInstances
            .Include(g => g.Owner)
            .Include(g => g.PlayerGameData)
            .FirstOrDefaultAsync(g => g.Id == gameInstanceId);

        var seats = new List<WorldMapGenerator.PlayerSeat>();

        if (instance != null)
        {
            seats.Add(new WorldMapGenerator.PlayerSeat(instance.OwnerId, instance.Owner?.UserName));

            // Everyone who already has a foothold in this instance is seated with the owner, so a
            // world generated for a group does not hand the first caller the only capital.
            foreach (var player in instance.PlayerGameData)
            {
                if (player.UserId == instance.OwnerId) continue;
                if (seats.Any(s => s.UserId == player.UserId)) continue;
                seats.Add(new WorldMapGenerator.PlayerSeat(player.UserId, null));
            }
        }

        int seed = SeedFor(gameInstanceId);
        var regions = WorldMapGenerator.Generate(seed, seats);
        return WorldRegionBlob.BuildWorld(seed, regions);
    }

    /// <summary>
    /// A stable seed for an instance. Derived from the id rather than stored so the same world can
    /// always be rebuilt from nothing but the instance itself.
    /// </summary>
    public static int SeedFor(Guid gameInstanceId)
    {
        var bytes = gameInstanceId.ToByteArray();
        ulong folded = 0;
        for (int i = 0; i < bytes.Length; i++)
            folded = (folded * 31) + bytes[i];

        return (int)(uint)new DeterministicRandom(folded).NextUInt64();
    }
}
