using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Tests.TestSupport;

/// <summary>
/// A throwaway <see cref="ApplicationDbContext"/> backed by EF's in-memory provider.
///
/// Each context gets its own database name so tests never see each other's rows, which matters more
/// than it sounds: several of these tests assert on "the only open run" or "the world row", and a
/// shared store would make them pass or fail depending on order.
/// </summary>
public static class TestDb
{
    public static ApplicationDbContext Create()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"tests-{Guid.NewGuid()}")
            .Options;

        return new ApplicationDbContext(options);
    }

    /// <summary>
    /// Creates a game instance and its owner. <see cref="Services.WorldProvisioningService"/> reads
    /// both when it generates a world, so the pair has to exist for anything world-shaped.
    /// </summary>
    public static async Task<GameInstance> AddInstanceAsync(
        this ApplicationDbContext context, string ownerId = TestIds.Owner, string? ownerName = "Owner")
    {
        var owner = new ApplicationUser { Id = ownerId, UserName = ownerName, DisplayName = ownerName };
        var instance = new GameInstance { Name = "Test Realm", OwnerId = ownerId };

        context.Users.Add(owner);
        context.GameInstances.Add(instance);
        await context.SaveChangesAsync();

        return instance;
    }

    /// <summary>Stores <paramref name="world"/> as the instance's shared world blob.</summary>
    public static async Task<WorldViewGameData> AddWorldAsync(
        this ApplicationDbContext context, Guid gameInstanceId, System.Text.Json.Nodes.JsonNode world)
    {
        var row = new WorldViewGameData
        {
            GameInstanceId = gameInstanceId,
            GameData = world.ToJsonString()
        };

        context.WorldViewGameData.Add(row);
        await context.SaveChangesAsync();
        return row;
    }

    /// <summary>Stores a player's save, which is what PvP recomputes attacking power from.</summary>
    public static async Task AddPlayerSaveAsync(
        this ApplicationDbContext context, Guid gameInstanceId, string userId, string saveJson)
    {
        context.PlayerGameData.Add(new PlayerGameData
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            GameData = saveJson
        });

        await context.SaveChangesAsync();
    }

    /// <summary>Re-reads the world blob from storage, so assertions see what was persisted.</summary>
    public static async Task<System.Text.Json.Nodes.JsonNode?> ReadWorldAsync(
        this ApplicationDbContext context, Guid gameInstanceId)
    {
        var row = await context.WorldViewGameData.AsNoTracking()
            .FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);

        return row == null ? null : System.Text.Json.Nodes.JsonNode.Parse(row.GameData);
    }
}

/// <summary>Named ids, so a test reads as "the rival's region" rather than as a string literal.</summary>
public static class TestIds
{
    public const string Owner = "user-owner";
    public const string Player = "user-player";
    public const string Rival = "user-rival";
}
