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
/// A faction's manpower (<c>docs/design/npc-factions.md</c> phase 1). Mike's protection for players is
/// that a faction can only do what its strength pays for, so what is pinned here is that strength is
/// sized by land, refills slowly, refills slower still when Bloodied, and is fed by ransom.
/// </summary>
public class FactionStrengthTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private static readonly DateTime Noon = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _db.Dispose();

    private FactionService Factions => new(_db, _log);

    private static WorldRegionData Held(FactionId faction, string id, int tier, int entrenchment)
    {
        var region = TestWorld.Region(id, q: id.Length, r: id[^1], ownership: LocationOwnership.Enemy, tier: tier);
        region.Faction = faction;
        region.Entrenchment = entrenchment;
        return region;
    }

    // -----------------------------------------------------------------
    // The rule
    // -----------------------------------------------------------------

    [Fact]
    public void TheCapIsWhatItsLandSupports_SoMoreLandIsAStrongerFaction()
    {
        var regions = new List<WorldRegionData>
        {
            Held(FactionId.Grimjaw, "g1", tier: 3, entrenchment: 2),
            Held(FactionId.Grimjaw, "g2", tier: 4, entrenchment: 0),
            Held(FactionId.Ashkin, "a1", tier: 3, entrenchment: 3),
            TestWorld.OwnedBy(TestIds.Player, "p1", tier: 4)
        };

        // 500 x 3 x 1.30 + 500 x 4 x 1.00: the hold's own floor, entrenched.
        Assert.Equal(1950 + 2000, FactionStrengthRules.Cap(FactionId.Grimjaw, regions));
        Assert.Equal(Math.Round(1500 * 1.45), FactionStrengthRules.Cap(FactionId.Ashkin, regions));

        regions.RemoveAt(1);
        Assert.Equal(1950, FactionStrengthRules.Cap(FactionId.Grimjaw, regions));
    }

    [Fact]
    public void ItRefillsFromEmptyInADay_AndAtHalfThatWhileBloodied()
    {
        const double cap = 2400;

        Assert.Equal(1200, FactionStrengthRules.Settle(0, Noon, Noon.AddHours(12), cap, null), 6);
        Assert.Equal(cap, FactionStrengthRules.Settle(0, Noon, Noon.AddHours(30), cap, null), 6);

        // Bloodied for the first 8 of 12 hours: 8 at half rate and 4 at full = 8 hours' worth.
        Assert.Equal(800, FactionStrengthRules.Settle(0, Noon, Noon.AddHours(12), cap, Noon.AddHours(8)), 6);
    }

    [Fact]
    public void LostLand_LeavesItNoStrongerThanWhatIsLeftSupports()
    {
        Assert.Equal(1000, FactionStrengthRules.Settle(3000, Noon, Noon, cap: 1000, bloodiedUntilUtc: null));
        Assert.Equal(0, FactionStrengthRules.Settle(3000, Noon, Noon.AddHours(5), cap: 0, bloodiedUntilUtc: null));
    }

    [Fact]
    public void ItIsReadyToMarchOnlyFromSixTenths_AndNeverWhileBloodied()
    {
        Assert.Equal(FactionReadiness.Rebuilding, FactionStrengthRules.Word(599, 1000, bloodied: false));
        Assert.Equal(FactionReadiness.Ready, FactionStrengthRules.Word(600, 1000, bloodied: false));
        Assert.Equal(FactionReadiness.Bloodied, FactionStrengthRules.Word(1000, 1000, bloodied: true));
    }

    [Fact]
    public void ARansomIsBankedAsStrength_UpToTheCap()
    {
        Assert.Equal(1500, FactionStrengthRules.BankRansom(500, 1000, cap: 2000));
        Assert.Equal(2000, FactionStrengthRules.BankRansom(1500, 1000, cap: 2000));
    }

    // -----------------------------------------------------------------
    // The service
    // -----------------------------------------------------------------

    private async Task<Guid> RealmAsync(params WorldRegionData[] regions)
    {
        var instance = await _db.AddInstanceAsync();
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(regions));
        return instance.Id;
    }

    private static FactionStrengthResponse Of(FactionsResponse response, FactionId faction)
        => response.Factions.Single(f => f.Faction == faction.ToString());

    [Fact]
    public async Task AFactionFirstReadStandsAtFullStrength_AndAFactionWithNoLandAtNothing()
    {
        var realm = await RealmAsync(Held(FactionId.Grimjaw, "g1", tier: 3, entrenchment: 1));

        var response = await Factions.ReadAsync(realm, Noon);

        var grimjaw = Of(response!, FactionId.Grimjaw);
        Assert.Equal(grimjaw.Cap, grimjaw.Strength);
        Assert.Equal(1, grimjaw.Readiness);
        Assert.Equal(nameof(FactionReadiness.Ready), grimjaw.Word);
        Assert.Equal(1, grimjaw.RegionsHeld);

        var ashkin = Of(response!, FactionId.Ashkin);
        Assert.Equal(0, ashkin.Cap);
        Assert.Equal(nameof(FactionReadiness.Rebuilding), ashkin.Word);

        Assert.Equal(2, await _db.FactionStates.CountAsync(f => f.GameInstanceId == realm));
    }

    [Fact]
    public async Task ABloodiedFaction_RecoversSlowerThanOneThatWasNot()
    {
        var realm = await RealmAsync(
            Held(FactionId.Grimjaw, "g1", tier: 4, entrenchment: 0),
            Held(FactionId.Ashkin, "a1", tier: 4, entrenchment: 0));
        await Factions.ReadAsync(realm, Noon);

        await Factions.DebugAsync(realm, new FactionDebugRequest("", Readiness: 0), Noon);
        await Factions.DebugAsync(realm, new FactionDebugRequest("Grimjaw", Bloody: true), Noon);

        var later = await Factions.ReadAsync(realm, Noon.AddHours(6));

        // Six hours of a day's refill is a quarter; Bloodied, an eighth.
        Assert.Equal(500, Of(later!, FactionId.Ashkin).Strength);
        Assert.Equal(250, Of(later!, FactionId.Grimjaw).Strength);
        Assert.Equal(nameof(FactionReadiness.Bloodied), Of(later!, FactionId.Grimjaw).Word);
        Assert.Equal(Noon.AddHours(8), Of(later!, FactionId.Grimjaw).BloodiedUntil);
    }

    [Fact]
    public async Task SimulatingHours_IsTheSameAsWaitingThem()
    {
        var realm = await RealmAsync(Held(FactionId.Ashkin, "a1", tier: 4, entrenchment: 0));
        await Factions.DebugAsync(realm, new FactionDebugRequest("Ashkin", Readiness: 0, Bloody: true), Noon);

        var simulated = await Factions.DebugAsync(realm, new FactionDebugRequest("Ashkin", SimulateHours: 12), Noon);

        // 8 Bloodied hours at half rate and 4 at full: 8 hours' worth of 2000 a day.
        var ashkin = Of(simulated!, FactionId.Ashkin);
        Assert.Equal(667, ashkin.Strength);
        Assert.Null(ashkin.BloodiedUntil);
    }

    [Fact]
    public async Task ARealmWithNoWorld_HasNoFactionsToRead()
    {
        var instance = await _db.AddInstanceAsync();
        Assert.Null(await Factions.ReadAsync(instance.Id, Noon));
    }

    [Fact]
    public async Task ARansomPaidForHeroesAFactionHolds_IsBankedAsItsStrength()
    {
        var region = Held(FactionId.Grimjaw, "r1", tier: 4, entrenchment: 0);
        var realm = await RealmAsync(region);
        await _db.AddPlayerSaveAsync(realm, TestIds.Player,
            TestSave.ToJson(TestSave.Roster(TestSave.Character("hero-1"), TestSave.Character("hero-2"))));

        string keep = TestWorld.KeepIn(region);
        var world = await _db.ReadWorldAsync(realm);
        var entry = WorldRegionBlob.EnsureOverride(WorldRegionBlob.FindRegion(world, "r1")!, keep);
        entry["CapturedCharacterIds"] = new JsonArray("hero-1", "hero-2");
        entry["CapturedAtUtcTicks"] = DateTime.UtcNow.Ticks;
        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == realm);
        row.GameData = world!.ToJsonString();
        await _db.SaveChangesAsync();

        var gold = new GoldService(_db, new FakeSessionLog(), NullLogger<GoldService>.Instance);
        await gold.GrantAsync(realm, TestIds.Player, 100_000, "test");
        await Factions.DebugAsync(realm, new FactionDebugRequest("Grimjaw", Readiness: 0));

        var garrisons = new WorldGarrisonService(
            _db, new FakeGameContent(), gold, new FakeSessionLog(), NullLogger<WorldGarrisonService>.Instance,
            new WarLogService(_db, new FakeHubContext(), NullLogger<WarLogService>.Instance, new FakeClock()),
            factions: Factions);
        var (outcome, _, _) = await garrisons.RansomAsync(realm, TestIds.Player, new RansomRequest(keep, SharedContract.Version));

        Assert.True(outcome.Succeeded, outcome.Message);
        var grimjaw = Of((await Factions.ReadAsync(realm))!, FactionId.Grimjaw);
        Assert.InRange(grimjaw.Strength, CaptivityRules.RansomCostGold(2), CaptivityRules.RansomCostGold(2) + 1);
    }
}
