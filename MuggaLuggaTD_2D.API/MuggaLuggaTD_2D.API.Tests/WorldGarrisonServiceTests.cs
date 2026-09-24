using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Stationing a garrison, and buying prisoners back.
///
/// <para>Both of these used to happen on the client. Garrisoning applied itself locally and persisted by
/// POSTing the old flat world save over the shared blob — which the server cannot read and therefore
/// regenerates, so one garrison wiped the realm's map for everybody. It also priced the garrison in
/// Unity, while the server derives hold, the raid bar and the siege encounter from that number.</para>
///
/// <para>So what is pinned here is that the server decides <b>who</b> may stand there and <b>what they
/// are worth</b>, and that a prisoner's release is the server's too.</para>
/// </summary>
public class WorldGarrisonServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeGameContent _content = new();

    private GoldService Gold => new(_db, new FakeSessionLog(), NullLogger<GoldService>.Instance);

    private WorldGarrisonService Service => new(
        _db, _content, Gold, new FakeSessionLog(), NullLogger<WorldGarrisonService>.Instance);

    public void Dispose() => _db.Dispose();

    private static GarrisonRequest Ask(string siteId, params string[] ids)
        => new(siteId, ids.ToList(), SharedContract.Version);

    private static RansomRequest AskRansom(string siteId) => new(siteId, SharedContract.Version);

    /// <summary>A realm where the player holds one region, with a roster of three.</summary>
    private async Task<(Guid Instance, WorldRegionData Region, string Keep)> SeedAsync(
        string owner = TestIds.Player, params string[] roster)
    {
        var instance = await _db.AddInstanceAsync();
        var region = TestWorld.OwnedBy(owner, "r1");
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(region));

        var ids = roster.Length > 0 ? roster : new[] { "hero-1", "hero-2", "hero-3" };
        await _db.AddPlayerSaveAsync(instance.Id, owner,
            TestSave.ToJson(TestSave.Roster(ids.Select(id => TestSave.Character(id)).ToArray())));

        return (instance.Id, region, TestWorld.KeepIn(region));
    }

    private async Task<WorldRegionData> ReadRegionAsync(Guid instance)
        => TestWorld.ReadRegion(await _db.ReadWorldAsync(instance), "r1");

    /// <summary>
    /// Writes back the world the service returned, which is what the controller does.
    ///
    /// <para>The service deliberately does not persist: the controller writes and broadcasts in one
    /// step, so a change is never stored without everyone being told about it.</para>
    /// </summary>
    private async Task PersistAsync(Guid instance, JsonNode? world)
    {
        Assert.NotNull(world);
        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == instance);
        row.GameData = world!.ToJsonString();
        await _db.SaveChangesAsync();
    }

    // -----------------------------------------------------------------
    // Stationing
    // -----------------------------------------------------------------

    [Fact]
    public async Task AGarrisonIsStationedAndPricedByTheServer()
    {
        var (instance, _, keep) = await SeedAsync();

        var (outcome, response, world) = await Service.SetAsync(
            instance, TestIds.Player, Ask(keep, "hero-1", "hero-2"));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(2, response!.CharacterIds.Count);
        Assert.NotNull(world);
    }

    [Fact]
    public async Task TheClientCannotNameItsOwnGarrisonPower()
    {
        // The whole reason this moved: GarrisonPower was written by the client, and hold, the raid bar
        // and the siege encounter are all derived from it. The request has nowhere to put a number.
        var (instance, _, keep) = await SeedAsync();

        var (_, _, world) = await Service.SetAsync(instance, TestIds.Player, Ask(keep, "hero-1"));
        await PersistAsync(instance, world);

        var stored = (await ReadRegionAsync(instance)).SiteOverrides[keep];
        Assert.Single(stored.GarrisonCharacterIds);

        // Whatever the roster prices at, it is a number this test never supplied — the request has
        // nowhere to put one.
        Assert.Contains("GarrisonPower", (await _db.ReadWorldAsync(instance))!.ToJsonString());
    }

    [Fact]
    public async Task OnlyCharactersInThePersistedRosterCanBeStationed()
    {
        var (instance, _, keep) = await SeedAsync(TestIds.Player, "hero-1");

        var (outcome, response, _) = await Service.SetAsync(
            instance, TestIds.Player, Ask(keep, "hero-1", "not-mine", "also-not-mine"));

        Assert.True(outcome.Succeeded);
        Assert.Equal(new[] { "hero-1" }, response!.CharacterIds);
    }

    [Fact]
    public async Task ResettingASitesOwnGarrisonIsAnOrdinaryEdit()
    {
        // The site's own defenders would otherwise read as "committed elsewhere" and be filtered out of
        // the very list resetting them.
        var (instance, _, keep) = await SeedAsync();

        await Service.SetAsync(instance, TestIds.Player, Ask(keep, "hero-1", "hero-2"));
        var (outcome, response, _) = await Service.SetAsync(
            instance, TestIds.Player, Ask(keep, "hero-1", "hero-2", "hero-3"));

        Assert.True(outcome.Succeeded);
        Assert.Equal(3, response!.CharacterIds.Count);
    }

    [Fact]
    public async Task AnEmptyListRecallsTheGarrison()
    {
        var (instance, _, keep) = await SeedAsync();
        var (_, _, stationed) = await Service.SetAsync(instance, TestIds.Player, Ask(keep, "hero-1"));
        await PersistAsync(instance, stationed);

        var (outcome, response, world) = await Service.SetAsync(instance, TestIds.Player, Ask(keep));
        await PersistAsync(instance, world);

        Assert.True(outcome.Succeeded);
        Assert.Empty(response!.CharacterIds);
        Assert.Empty((await ReadRegionAsync(instance)).SiteOverrides[keep].GarrisonCharacterIds);
    }

    [Fact]
    public async Task ARegionYouDoNotHoldCannotBeGarrisoned()
    {
        var (instance, region, _) = await SeedAsync(TestIds.Rival);
        var keep = TestWorld.KeepIn(region);

        var (outcome, _, _) = await Service.SetAsync(instance, TestIds.Player, Ask(keep, "hero-1"));

        Assert.Equal(GarrisonError.NotYours, outcome.Error);
    }

    [Fact]
    public async Task ADungeonIsNotSomethingYouGarrison()
    {
        var (instance, region, _) = await SeedAsync();

        var (outcome, _, _) = await Service.SetAsync(
            instance, TestIds.Player, Ask(TestWorld.DungeonIn(region), "hero-1"));

        Assert.Equal(GarrisonError.NotGarrisonable, outcome.Error);
    }

    [Fact]
    public async Task AStaleClientIsRefused()
    {
        var (instance, _, keep) = await SeedAsync();

        var (outcome, _, _) = await Service.SetAsync(
            instance, TestIds.Player, new GarrisonRequest(keep, new List<string> { "hero-1" }, "0.0.0"));

        Assert.Equal(GarrisonError.ContractMismatch, outcome.Error);
    }

    // -----------------------------------------------------------------
    // Ransom
    // -----------------------------------------------------------------

    /// <summary>Puts this player's characters in a cell at <paramref name="siteId"/>.</summary>
    private async Task ImprisonAsync(Guid instance, string siteId, DateTime takenAt, params string[] ids)
    {
        var world = await _db.ReadWorldAsync(instance);
        var regionNode = WorldRegionBlob.FindRegion(world, "r1")!;
        var entry = WorldRegionBlob.EnsureOverride(regionNode, siteId);

        entry["CapturedCharacterIds"] = new JsonArray(ids.Select(id => (JsonNode)id!).ToArray());
        entry["CapturedAtUtcTicks"] = takenAt.Ticks;

        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == instance);
        row.GameData = world!.ToJsonString();
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task ARansomFreesYourPrisonersAndChargesForThem()
    {
        var (instance, _, keep) = await SeedAsync();
        await ImprisonAsync(instance, keep, DateTime.UtcNow, "hero-1", "hero-2");
        await Gold.GrantAsync(instance, TestIds.Player, 100_000, "test");

        long before = await Gold.BalanceAsync(instance, TestIds.Player);
        var (outcome, response, world) = await Service.RansomAsync(instance, TestIds.Player, AskRansom(keep));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(2, response!.CharacterIds.Count);
        Assert.Equal(CaptivityRules.RansomCostGold(2), before - await Gold.BalanceAsync(instance, TestIds.Player));
        Assert.NotNull(world);
    }

    [Fact]
    public async Task PrisonersWhoAlreadyWalkedHomeCannotBeRansomed()
    {
        // Charging for a release that had already happened is the obvious way to get this wrong.
        var (instance, _, keep) = await SeedAsync();
        await ImprisonAsync(instance, keep, DateTime.UtcNow.AddHours(-CaptivityRules.PrisonerReturnHours - 1), "hero-1");
        await Gold.GrantAsync(instance, TestIds.Player, 100_000, "test");

        long before = await Gold.BalanceAsync(instance, TestIds.Player);
        var (outcome, _, _) = await Service.RansomAsync(instance, TestIds.Player, AskRansom(keep));

        Assert.Equal(GarrisonError.NobodyHeld, outcome.Error);
        Assert.Equal(before, await Gold.BalanceAsync(instance, TestIds.Player));
    }

    [Fact]
    public async Task YouCannotRansomSomebodyElsesPrisoners()
    {
        var (instance, _, keep) = await SeedAsync();
        await ImprisonAsync(instance, keep, DateTime.UtcNow, "someone-elses-hero");
        await Gold.GrantAsync(instance, TestIds.Player, 100_000, "test");

        var (outcome, _, _) = await Service.RansomAsync(instance, TestIds.Player, AskRansom(keep));

        Assert.Equal(GarrisonError.NotYourPrisoners, outcome.Error);
    }

    [Fact]
    public async Task ARansomNobodyCanAffordFreesNobody()
    {
        var (instance, _, keep) = await SeedAsync();
        await ImprisonAsync(instance, keep, DateTime.UtcNow, "hero-1");

        var (outcome, _, _) = await Service.RansomAsync(instance, TestIds.Player, AskRansom(keep));

        Assert.Equal(GarrisonError.CannotAfford, outcome.Error);

        var (ids, stamp) = WorldRegionBlob.PrisonersAt(
            WorldRegionBlob.FindRegion(await _db.ReadWorldAsync(instance), "r1")!, keep);

        Assert.Single(ids);
        Assert.True(CaptivityRules.IsHeld(stamp, DateTime.UtcNow));
    }

    // -----------------------------------------------------------------
    // What a lost region does to its defenders
    // -----------------------------------------------------------------

    [Fact]
    public async Task LosingARegionTakesItsGarrisonPrisonerWithAStamp()
    {
        var (instance, _, keep) = await SeedAsync();
        var (_, _, stationed) = await Service.SetAsync(instance, TestIds.Player, Ask(keep, "hero-1", "hero-2"));
        await PersistAsync(instance, stationed);

        var world = await _db.ReadWorldAsync(instance);
        var regionNode = WorldRegionBlob.FindRegion(world, "r1")!;
        var taken = DateTime.UtcNow;

        int captured = WorldRegionBlob.CaptureWrecked(regionNode, TestIds.Rival, "Rival", taken);

        Assert.Equal(2, captured);

        var (ids, stamp) = WorldRegionBlob.PrisonersAt(regionNode, keep);
        Assert.Equal(2, ids.Count);
        Assert.True(CaptivityRules.IsHeld(stamp, taken));

        // And they come home rather than being held for the rest of the season.
        Assert.False(CaptivityRules.IsHeld(stamp, taken.AddHours(CaptivityRules.PrisonerReturnHours)));

        // The garrison itself is gone — they are in a cell, not still on the wall.
        Assert.Empty(WorldRegionBlob.ReadRegion(regionNode).SiteOverrides[keep].GarrisonCharacterIds);
    }

    [Fact]
    public async Task ASecondCaptureDoesNotReimprisonTheFirstCompany()
    {
        // Merging rather than replacing would re-stamp ids belonging to a company that is long since
        // home — imprisoning them again for a battle they were not in.
        var (instance, _, keep) = await SeedAsync();

        var world = await _db.ReadWorldAsync(instance);
        var regionNode = WorldRegionBlob.FindRegion(world, "r1")!;

        await ImprisonAsync(instance, keep, DateTime.UtcNow.AddDays(-2), "old-hero");
        world = await _db.ReadWorldAsync(instance);
        regionNode = WorldRegionBlob.FindRegion(world, "r1")!;

        WorldRegionBlob.SetGarrison(regionNode, keep, new[] { "hero-1" }, 100);
        WorldRegionBlob.CaptureWrecked(regionNode, TestIds.Rival, "Rival", DateTime.UtcNow);

        var (ids, _) = WorldRegionBlob.PrisonersAt(regionNode, keep);

        Assert.Equal(new[] { "hero-1" }, ids);
        Assert.DoesNotContain("old-hero", ids);
    }

    [Fact]
    public async Task AReturnedPrisonerIsNotCommittedAnyMore()
    {
        // CollectCommittedCharacterIds decides who may march, garrison or run a dungeon. Reading the
        // list instead of asking the rule would lock a freed character out of their own party for good.
        var (instance, _, keep) = await SeedAsync();
        await ImprisonAsync(instance, keep, DateTime.UtcNow.AddDays(-1), "hero-1");

        var committed = WorldRegionBlob.CollectCommittedCharacterIds(
            await _db.ReadWorldAsync(instance), TestIds.Player);

        Assert.DoesNotContain("hero-1", committed);
    }

    [Fact]
    public async Task APrisonerStillHeldIsCommitted()
    {
        var (instance, _, keep) = await SeedAsync();
        await ImprisonAsync(instance, keep, DateTime.UtcNow, "hero-1");

        var committed = WorldRegionBlob.CollectCommittedCharacterIds(
            await _db.ReadWorldAsync(instance), TestIds.Player);

        Assert.Contains("hero-1", committed);
    }
}
