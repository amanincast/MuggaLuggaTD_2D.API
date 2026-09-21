using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Who owns the existence of a world.
///
/// <para>Generation moved to the server when regions arrived, because the server has to agree with
/// the client about what is inside a region before it can judge a claim against it. This is the code
/// that decides a world exists, that an old one must be thrown away, and where a player stands when
/// they first open the map — and it has been triggered twice in a week by format bumps.</para>
/// </summary>
public class WorldProvisioningServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();

    private WorldProvisioningService Service
        => new(_db, NullLogger<WorldProvisioningService>.Instance);

    public void Dispose() => _db.Dispose();

    // -----------------------------------------------------------------
    // Generating a world
    // -----------------------------------------------------------------

    [Fact]
    public async Task AnInstanceWithNoWorldGetsOne()
    {
        var instance = await _db.AddInstanceAsync();

        var row = await Service.EnsureWorldAsync(instance.Id);

        var world = JsonNode.Parse(row.GameData);
        Assert.Equal(WorldRegionBlob.CurrentFormatVersion, WorldRegionBlob.GetFormatVersion(world));
        Assert.Equal(WorldMapGenerator.DefaultRegionCount, WorldRegionBlob.ReadAllRegions(world).Count);
    }

    [Fact]
    public async Task AWorldThatAlreadyExistsIsLeftExactlyAsItWas()
    {
        // Every entry to the map calls this. Regenerating on the way past would wipe the world any
        // time two players were playing it.
        var instance = await _db.AddInstanceAsync();
        var first = await Service.EnsureWorldAsync(instance.Id);
        var before = first.GameData;

        var second = await Service.EnsureWorldAsync(instance.Id);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(before, second.GameData);
        Assert.Single(await _db.WorldViewGameData.ToListAsync());
    }

    [Fact]
    public async Task TheOwnerOfTheInstanceIsSeatedWhenTheWorldIsMade()
    {
        // Otherwise the person who made the realm opens the map with nowhere to stand.
        var instance = await _db.AddInstanceAsync(TestIds.Owner, "Owner");

        var row = await Service.EnsureWorldAsync(instance.Id);

        var capital = CapitalOf(JsonNode.Parse(row.GameData), TestIds.Owner);
        Assert.True(capital.IsCapital);
        Assert.Equal(LocationOwnership.Player, capital.Ownership);
        Assert.Equal(FactionId.Player, capital.Faction);
    }

    [Fact]
    public async Task EveryoneWithAFootholdIsSeatedTogether()
    {
        // A world generated for a group must not hand the first caller the only capital: whoever
        // already has data in the instance is seated alongside the owner.
        var instance = await _db.AddInstanceAsync();
        await AddPlayerAsync(instance.Id, TestIds.Player);
        await AddPlayerAsync(instance.Id, TestIds.Rival);

        var row = await Service.EnsureWorldAsync(instance.Id);
        var world = JsonNode.Parse(row.GameData);

        foreach (var userId in new[] { TestIds.Owner, TestIds.Player, TestIds.Rival })
            Assert.True(CapitalOf(world, userId).IsCapital, $"{userId} was not seated.");
    }

    [Fact]
    public async Task PlayersAreNotSeatedOnTopOfEachOther()
    {
        var instance = await _db.AddInstanceAsync();
        await AddPlayerAsync(instance.Id, TestIds.Player);
        await AddPlayerAsync(instance.Id, TestIds.Rival);

        var world = JsonNode.Parse((await Service.EnsureWorldAsync(instance.Id)).GameData);
        var capitals = WorldRegionBlob.ReadAllRegions(world).Where(r => r.IsCapital).ToList();

        Assert.Equal(3, capitals.Count);
        foreach (var a in capitals)
        foreach (var b in capitals)
        {
            if (a.RegionId == b.RegionId) continue;
            Assert.True(HexCoord.Distance(a.Hex, b.Hex) > 1,
                $"{a.RegionId} and {b.RegionId} were seated adjacent to one another.");
        }
    }

    [Fact]
    public async Task ASeatStartsFortifiedEnoughToSurviveTheFirstDays()
    {
        var instance = await _db.AddInstanceAsync();

        var capital = CapitalOf(JsonNode.Parse((await Service.EnsureWorldAsync(instance.Id)).GameData), TestIds.Owner);

        Assert.Equal(2, capital.Entrenchment);
        Assert.Equal(1, capital.Tier);
        Assert.Equal(BiomeType.RiverVale, capital.Biome);
        Assert.Equal(100, capital.Resolve);
    }

    // -----------------------------------------------------------------
    // Throwing an old world away
    // -----------------------------------------------------------------

    [Fact]
    public async Task AWorldFromBeforeRegionsIsRegenerated()
    {
        // Format 1 is a flat list of locations with no regions to put them in. There is nothing to
        // migrate, and every instance of it is a pre-release test realm.
        var instance = await _db.AddInstanceAsync();
        var row = await _db.AddWorldAsync(instance.Id, JsonNode.Parse("{\"Locations\":[]}")!);

        var result = await Service.EnsureWorldAsync(instance.Id);

        Assert.Equal(row.Id, result.Id); // regenerated in place, not duplicated
        var world = JsonNode.Parse(result.GameData);
        Assert.Equal(WorldRegionBlob.CurrentFormatVersion, WorldRegionBlob.GetFormatVersion(world));
        Assert.Equal(WorldMapGenerator.DefaultRegionCount, WorldRegionBlob.ReadAllRegions(world).Count);
    }

    [Fact]
    public async Task AWorldFromAnEarlierRegionFormatIsRegeneratedRatherThanCarriedForward()
    {
        // The generator is the data. An older format's site overrides are keyed by ids the current
        // generator hands to different cells, so keeping them would mark the wrong sites cleared —
        // a world that looks fine and is quietly wrong.
        var instance = await _db.AddInstanceAsync();
        var stale = TestWorld.Blob(TestWorld.Region("r0"));
        stale["FormatVersion"] = WorldRegionBlob.CurrentFormatVersion - 1;
        await _db.AddWorldAsync(instance.Id, stale);

        var result = await Service.EnsureWorldAsync(instance.Id);
        var world = JsonNode.Parse(result.GameData);

        Assert.Equal(WorldRegionBlob.CurrentFormatVersion, WorldRegionBlob.GetFormatVersion(world));
        Assert.Equal(WorldMapGenerator.DefaultRegionCount, WorldRegionBlob.ReadAllRegions(world).Count);
    }

    [Fact]
    public async Task RegeneratingAWorldStampsWhenItChanged()
    {
        var instance = await _db.AddInstanceAsync();
        var row = await _db.AddWorldAsync(instance.Id, JsonNode.Parse("{\"Locations\":[]}")!);
        row.UpdatedAt = DateTime.UtcNow.AddDays(-1);
        await _db.SaveChangesAsync();
        var before = row.UpdatedAt;

        var result = await Service.EnsureWorldAsync(instance.Id);

        Assert.True(result.UpdatedAt > before);
    }

    // -----------------------------------------------------------------
    // The same instance always means the same world
    // -----------------------------------------------------------------

    [Fact]
    public void AnInstancesSeedIsDerivedFromItsIdRatherThanRemembered()
    {
        var id = Guid.NewGuid();

        Assert.Equal(WorldProvisioningService.SeedFor(id), WorldProvisioningService.SeedFor(id));
        Assert.NotEqual(WorldProvisioningService.SeedFor(id), WorldProvisioningService.SeedFor(Guid.NewGuid()));
    }

    [Fact]
    public async Task AWorldRebuiltFromNothingButTheInstanceIsTheSameWorld()
    {
        // The seed is not stored, so this is the only thing keeping a regenerated world recognisable
        // — and the reason two servers handed the same instance would agree about the map.
        var instance = await _db.AddInstanceAsync();
        var first = (await Service.EnsureWorldAsync(instance.Id)).GameData;

        _db.WorldViewGameData.RemoveRange(_db.WorldViewGameData);
        await _db.SaveChangesAsync();

        var second = (await Service.EnsureWorldAsync(instance.Id)).GameData;

        Assert.Equal(first, second);
    }

    // -----------------------------------------------------------------
    // Seating someone who arrives late
    // -----------------------------------------------------------------

    [Fact]
    public async Task ALateJoinerIsGivenACapital()
    {
        // Without this they open the map with nowhere to stand and no supply line to anywhere.
        var instance = await _db.AddInstanceAsync();
        await Service.EnsureWorldAsync(instance.Id);

        var seated = await Service.EnsureSeatAsync(instance.Id, TestIds.Player, "Mike");

        Assert.True(seated);
        var capital = CapitalOf(await _db.ReadWorldAsync(instance.Id), TestIds.Player);
        Assert.True(capital.IsCapital);
        Assert.Equal("Mike", capital.OwnerDisplayName);
        Assert.Equal(2, capital.Entrenchment);
        Assert.Equal(1, capital.Tier);
    }

    [Fact]
    public async Task APlayerWhoAlreadyHoldsGroundIsNotSeatedAgain()
    {
        var instance = await _db.AddInstanceAsync();
        await Service.EnsureWorldAsync(instance.Id);
        await Service.EnsureSeatAsync(instance.Id, TestIds.Player, "Mike");
        var before = (await _db.WorldViewGameData.AsNoTracking()
            .FirstAsync(w => w.GameInstanceId == instance.Id)).GameData;

        var seated = await Service.EnsureSeatAsync(instance.Id, TestIds.Player, "Mike");

        Assert.False(seated);
        Assert.Equal(before, (await _db.WorldViewGameData.AsNoTracking()
            .FirstAsync(w => w.GameInstanceId == instance.Id)).GameData);
    }

    [Fact]
    public async Task HoldingGroundThatIsNotACapitalStillCountsAsSeated()
    {
        // Conquest can leave a player owning regions but no capital of their own. They are in the
        // world already; handing them a fresh capital would be a free region for losing one.
        var instance = await _db.AddInstanceAsync();
        await Service.EnsureWorldAsync(instance.Id);

        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == instance.Id);
        var world = JsonNode.Parse(row.GameData)!;
        var somewhere = WorldRegionBlob.GetRegions(world)!
            .First(r => (LocationOwnership)r!["Ownership"]!.GetValue<int>() == LocationOwnership.Neutral)!;
        WorldRegionBlob.CaptureRegion(somewhere, TestIds.Player, "Mike");
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();

        var seated = await Service.EnsureSeatAsync(instance.Id, TestIds.Player, "Mike");

        Assert.False(seated);
        Assert.DoesNotContain(WorldRegionBlob.ReadAllRegions(await _db.ReadWorldAsync(instance.Id)),
            r => r.IsCapital && r.OwnerUserId == TestIds.Player);
    }

    [Fact]
    public async Task ALateJoinerIsNotDroppedOnANeighboursDoorstep()
    {
        var instance = await _db.AddInstanceAsync();
        await Service.EnsureWorldAsync(instance.Id);

        await Service.EnsureSeatAsync(instance.Id, TestIds.Player, "Mike");
        await Service.EnsureSeatAsync(instance.Id, TestIds.Rival, "Rival");

        var capitals = WorldRegionBlob.ReadAllRegions(await _db.ReadWorldAsync(instance.Id))
            .Where(r => r.IsCapital).ToList();

        foreach (var a in capitals)
        foreach (var b in capitals)
        {
            if (a.RegionId == b.RegionId) continue;
            Assert.True(HexCoord.Distance(a.Hex, b.Hex) > 1,
                $"{a.RegionId} and {b.RegionId} were seated adjacent to one another.");
        }
    }

    [Fact]
    public async Task AFullWorldSeatsNobody()
    {
        // Every region spoken for. Better to say so than to hand out a region somebody holds.
        var instance = await _db.AddInstanceAsync();
        await Service.EnsureWorldAsync(instance.Id);

        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == instance.Id);
        var world = JsonNode.Parse(row.GameData)!;
        foreach (var region in WorldRegionBlob.GetRegions(world)!)
            WorldRegionBlob.CaptureRegion(region!, TestIds.Rival, "Rival");
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();

        Assert.False(await Service.EnsureSeatAsync(instance.Id, TestIds.Player, "Mike"));
    }

    [Fact]
    public async Task SeatingSomeoneInAnInstanceWithNoWorldYetMakesTheWorldFirst()
    {
        var instance = await _db.AddInstanceAsync();

        var seated = await Service.EnsureSeatAsync(instance.Id, TestIds.Player, "Mike");

        Assert.True(seated);
        var world = await _db.ReadWorldAsync(instance.Id);
        Assert.Equal(WorldRegionBlob.CurrentFormatVersion, WorldRegionBlob.GetFormatVersion(world));
        Assert.True(CapitalOf(world, TestIds.Player).IsCapital);
    }

    // -----------------------------------------------------------------

    private static WorldRegionData CapitalOf(JsonNode? world, string userId)
    {
        var region = WorldRegionBlob.ReadAllRegions(world)
            .FirstOrDefault(r => r.OwnerUserId == userId && r.IsCapital);

        Assert.True(region != null, $"No capital found for {userId}.");
        return region!;
    }

    private async Task AddPlayerAsync(Guid instanceId, string userId)
    {
        _db.Users.Add(new ApplicationUser { Id = userId, UserName = userId, DisplayName = userId });
        await _db.SaveChangesAsync();
        await _db.AddPlayerSaveAsync(instanceId, userId, "{}");
    }
}
