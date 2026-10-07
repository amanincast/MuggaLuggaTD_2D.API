using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// NPC factions grow and fight each other (<c>docs/design/npc-factions.md</c> phase 4). Pinned: a
/// faction expands only into wild hard country on its border that touches no capital, and pays for it;
/// it fortifies its most threatened land, walls and resolve; and it besieges another faction's worn
/// land as it does a player's, settled by the server, with a Bloodied defender holding at a quarter less.
/// </summary>
public class FactionGrowthTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private readonly FakeHubContext _hub = new();
    private readonly FakeGameContent _content = new();
    private static readonly DateTime Noon = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _db.Dispose();

    private sealed class FixedRandom : Random
    {
        public double Roll { get; set; }
        public int D20 { get; set; } = 20;
        public override double NextDouble() => Roll;
        public override int Next(int minValue, int maxValue) => maxValue == 21 ? D20 : minValue;
        public override int Next() => 0;
    }

    private readonly FixedRandom _dice = new();

    private FactionService Factions => new(_db, _log, _content,
        new WarLogService(_db, _hub, NullLogger<WarLogService>.Instance, new FakeClock()), null, _hub, _dice);

    private static WorldRegionData Held(FactionId faction, string id, int q, int r, int tier = 4, int entrenchment = 2, int resolve = 100)
    {
        var region = TestWorld.Region(id, q, r, LocationOwnership.Enemy, tier: tier);
        region.Faction = faction;
        region.Entrenchment = entrenchment;
        region.Resolve = resolve;
        return region;
    }

    private static WorldRegionData Wild(string id, int q, int r, int tier)
    {
        var region = TestWorld.Region(id, q, r, tier: tier);
        region.Entrenchment = 0;
        return region;
    }

    private static WorldRegionData Seat(string id, int q, int r)
    {
        var region = TestWorld.Region(id, q, r, LocationOwnership.Player, TestIds.Player, tier: 1);
        region.IsCapital = true;
        return region;
    }

    private async Task<Guid> RealmAsync(params WorldRegionData[] regions)
    {
        var instance = await _db.AddInstanceAsync();
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(regions));
        return instance.Id;
    }

    private async Task<WorldRegionData> RegionAsync(Guid realm, string id) =>
        TestWorld.ReadRegion(await _db.ReadWorldAsync(realm), id);

    private async Task<FactionState> StateAsync(Guid realm, FactionId faction) =>
        await _db.FactionStates.AsNoTracking().SingleAsync(f => f.GameInstanceId == realm && f.Faction == faction);

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void OnlyWildHardCountry_AwayFromTheSeats_IsExpandable()
    {
        var seat = Seat("seat", -2, 0);
        var hard = Wild("hard", 1, 0, tier: 3);
        var gentle = Wild("gentle", 0, 1, tier: 2);
        var doorstep = Wild("doorstep", -1, 0, tier: 4);
        var rival = Held(FactionId.Ashkin, "a", 1, -1);
        var regions = new List<WorldRegionData> { seat, hard, gentle, doorstep, rival };

        Assert.True(FactionGrowthRules.IsExpandable(hard, regions));
        Assert.False(FactionGrowthRules.IsExpandable(gentle, regions));
        Assert.False(FactionGrowthRules.IsExpandable(doorstep, regions));
        Assert.False(FactionGrowthRules.IsExpandable(rival, regions));
        Assert.False(FactionGrowthRules.IsExpandable(seat, regions));
    }

    [Fact]
    public void ItFortifiesTheMostThreatenedLand_AndNothingUnderSiegeOrWhole()
    {
        var inner = Held(FactionId.Grimjaw, "inner", 0, 0, entrenchment: 0, resolve: 30);
        var regions = new List<WorldRegionData> { inner };
        foreach (var (hex, i) in inner.Hex.Neighbours().Select((h, i) => (h, i)))
        {
            regions.Add(Held(FactionId.Grimjaw, "ring" + i, hex.Q, hex.R, resolve: i == 2 ? 60 : 90));
            regions.Add(Wild("beyond" + i, hex.Q * 2, hex.R * 2, tier: 3));
        }

        // Every ring region touches land beyond, so the heartland waits however worn it is; the most worn ring goes first.
        Assert.Equal("ring2", FactionGrowthRules.PickFortify(FactionId.Grimjaw, regions)!.RegionId);
        Assert.NotEqual("ring2", FactionGrowthRules.PickFortify(FactionId.Grimjaw, regions, new[] { "ring2" })!.RegionId);

        var whole = Held(FactionId.Ashkin, "whole", 9, 9, entrenchment: FortifyRules.MaxLevel, resolve: 100);
        Assert.Null(FactionGrowthRules.PickFortify(FactionId.Ashkin, new List<WorldRegionData> { whole }));

        var worn = Held(FactionId.Ashkin, "worn", 9, 9, entrenchment: FortifyRules.MaxLevel, resolve: 95);
        Assert.Equal((FortifyRules.MaxLevel, 100), FactionGrowthRules.Fortified(worn));
    }

    // -----------------------------------------------------------------
    // Expanding
    // -----------------------------------------------------------------

    [Fact]
    public async Task AFactionClaimsWildLandOnItsBorder_AndPaysForIt()
    {
        var realm = await RealmAsync(Held(FactionId.Grimjaw, "g", 0, 0), Wild("wild", 1, 0, tier: 3), Wild("gentle", 0, 1, tier: 2));
        var before = await Factions.ReadAsync(realm, Noon);
        var grimjaw = before!.Factions.Single(f => f.Faction == "Grimjaw");

        var done = await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw, forceAction: FactionAction.Expand);

        Assert.Contains(done, l => l.Contains("claimed wild"));
        var wild = await RegionAsync(realm, "wild");
        Assert.Equal(FactionId.Grimjaw, wild.Faction);
        Assert.Equal(LocationOwnership.Enemy, wild.Ownership);
        Assert.Equal("The Grimjaw", wild.OwnerDisplayName);
        Assert.Equal(0, wild.ClaimedAtUtcTicks); // nobody held it: no truce
        Assert.Equal(FactionId.None, (await RegionAsync(realm, "gentle")).Faction);

        var state = await StateAsync(realm, FactionId.Grimjaw);
        Assert.Equal(grimjaw.Strength * (1 - FactionGrowthRules.ExpandShare), state.Strength, 0);

        var after = (await Factions.ReadAsync(realm, Noon))!.Factions.Single(f => f.Faction == "Grimjaw");
        Assert.Equal(2, after.RegionsHeld);
        Assert.True(after.Cap > grimjaw.Cap);

        var line = Assert.Single(_db.WarLog.Where(e => e.Kind == nameof(WarLogKind.Expanded)));
        Assert.Equal("faction:Grimjaw", line.ActorUserId);
        Assert.Equal("wild", line.RegionId);
        Assert.Null(line.SubjectUserId);
        Assert.Contains(_hub.Sent, s => s.Method == "WorldViewGameDataUpdated");
    }

    [Fact]
    public async Task WithNoWildLandLeft_ALeanToExpand_FortifiesInstead()
    {
        _content.Factions = new[]
        {
            new FactionTemperament { Id = FactionId.Grimjaw, Name = "The Grimjaw", Raid = 0, Expand = 1 },
            new FactionTemperament { Id = FactionId.Ashkin, Name = "The Ashkin", Raid = 0 }
        };
        var realm = await RealmAsync(Held(FactionId.Grimjaw, "g", 0, 0, resolve: 60), Wild("gentle", 1, 0, tier: 2));

        var forced = await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw, forceAction: FactionAction.Expand);
        Assert.Contains(forced, l => l.Contains("no wild land"));
        Assert.Equal(2, (await RegionAsync(realm, "g")).Entrenchment);

        var done = await Factions.ActAsync(realm, Noon.AddMinutes(15));
        Assert.Contains(done, l => l.Contains("fortified g"));
        var g = await RegionAsync(realm, "g");
        Assert.Equal(3, g.Entrenchment);
        Assert.Equal(60 + FactionGrowthRules.FortifyResolveBonus, g.Resolve);
    }

    // -----------------------------------------------------------------
    // Fortifying
    // -----------------------------------------------------------------

    [Fact]
    public async Task AFactionFortifies_RaisingWallsAndResolve_AndSaysSo()
    {
        var realm = await RealmAsync(
            Held(FactionId.Ashkin, "a", 0, 0, resolve: 100),
            Held(FactionId.Ashkin, "worn", 1, 0, entrenchment: 1, resolve: 40),
            Wild("gentle", 2, 0, tier: 2));
        await Factions.ReadAsync(realm, Noon);
        double strength = (await StateAsync(realm, FactionId.Ashkin)).Strength;

        await Factions.DebugAsync(realm, new FactionDebugRequest("Ashkin", ForceAct: true, Action: "Fortify"), Noon);

        var worn = await RegionAsync(realm, "worn");
        Assert.Equal(2, worn.Entrenchment);
        Assert.Equal(55, worn.Resolve);
        Assert.Equal(strength * (1 - FactionGrowthRules.FortifyShare), (await StateAsync(realm, FactionId.Ashkin)).Strength, 0);

        var line = Assert.Single(_db.WarLog.Where(e => e.Kind == nameof(WarLogKind.Fortified)));
        Assert.Equal("faction:Ashkin", line.ActorUserId);
        Assert.Equal("The Ashkin", line.ActorName);
        Assert.Equal("II", line.Detail);
    }

    // -----------------------------------------------------------------
    // Faction against faction
    // -----------------------------------------------------------------

    private async Task<Guid> RivalsAsync(int ashkinResolve = 40) => await RealmAsync(
        Held(FactionId.Grimjaw, "g", 0, 0),
        Held(FactionId.Ashkin, "a", 1, 0, tier: 3, entrenchment: 0, resolve: ashkinResolve),
        Held(FactionId.Ashkin, "a2", 2, 0, tier: 3, entrenchment: 0));

    [Fact]
    public async Task AFactionBesiegesItsRivalsWornLand_AndTakesIt()
    {
        var realm = await RivalsAsync();
        var before = (await Factions.ReadAsync(realm, Noon))!.Factions.Single(f => f.Faction == "Ashkin");

        var done = await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw, forceAction: FactionAction.Siege);
        Assert.Contains(done, l => l.Contains("laid siege to a"));

        var siege = await _db.FactionSieges.AsNoTracking().SingleAsync();
        Assert.Equal("faction:Ashkin", siege.DefenderUserId);
        Assert.Equal(FactionId.Ashkin, siege.DefenderFaction);
        var declared = Assert.Single(_db.WarLog.Where(e => e.Kind == nameof(WarLogKind.SiegeDeclared)));
        Assert.Equal("faction:Ashkin", declared.SubjectUserId);
        Assert.Equal("The Ashkin", declared.SubjectName);

        // Nobody may sally out of a faction's land.
        var (refused, _) = await Factions.BeginSortieAsync(realm, TestIds.Player, siege.Id,
            new FactionSortieRequest(new List<string> { "hero-1" }, MuggaLuggaTD.Shared.SharedContract.Version), Noon);
        Assert.Equal(SiegeError.NotDefender, refused.Error);

        await Factions.DebugAsync(realm, new FactionDebugRequest("Grimjaw", CloseMuster: true), Noon.AddHours(1));

        Assert.Equal(SiegeState.Won, (await _db.FactionSieges.AsNoTracking().SingleAsync()).State);
        var taken = await RegionAsync(realm, "a");
        Assert.Equal(FactionId.Grimjaw, taken.Faction);
        Assert.Equal(SiegeAssaultRules.WreckedResolve, taken.Resolve);

        var after = (await Factions.ReadAsync(realm, Noon.AddHours(1)))!.Factions.Single(f => f.Faction == "Ashkin");
        Assert.Equal(1, after.RegionsHeld);
        Assert.True(after.Cap < before.Cap);

        var won = Assert.Single(_db.WarLog.Where(e => e.Kind == nameof(WarLogKind.SiegeWon)));
        Assert.Equal("faction:Ashkin", won.SubjectUserId);
        Assert.Equal("The Ashkin", won.SubjectName);
    }

    [Fact]
    public async Task ABloodiedFaction_DefendsASiegeAtAQuarterLess()
    {
        var realm = await RivalsAsync();
        await Factions.ReadAsync(realm, Noon);
        await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw, forceAction: FactionAction.Siege);
        await Factions.DebugAsync(realm, new FactionDebugRequest("Ashkin", Bloody: true), Noon);

        await Factions.DebugAsync(realm, new FactionDebugRequest("Grimjaw", CloseMuster: true), Noon.AddHours(1));

        var region = TestWorld.Region("a", 1, 0, LocationOwnership.Enemy, tier: 3);
        region.Faction = FactionId.Ashkin;
        region.Entrenchment = 0;
        region.Resolve = 40;
        long whole = RegionHoldCalculator.AssessRegion(region, new[] { region }, 0).Hold;
        var siege = await _db.FactionSieges.AsNoTracking().SingleAsync();
        Assert.Equal(FactionDecisionRules.DefendingHold(whole, true), siege.FrozenHold);
        Assert.True(siege.FrozenHold < whole);
    }

    [Fact]
    public async Task ARivalsSiege_IsCalledOff_WhenTheLandChangesHandsUnderIt()
    {
        var realm = await RivalsAsync();
        await Factions.ReadAsync(realm, Noon);
        await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw, forceAction: FactionAction.Siege);

        // A player wins the keep while the warband musters.
        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == realm);
        var world = System.Text.Json.Nodes.JsonNode.Parse(row.GameData)!;
        WorldRegionBlob.CaptureRegion(WorldRegionBlob.FindRegion(world, "a")!, TestIds.Player, "Mike", Noon);
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();

        await Factions.SettleDueSiegesAsync(realm, Noon + SiegeRules.Muster);

        Assert.Equal(SiegeState.Cancelled, (await _db.FactionSieges.AsNoTracking().SingleAsync()).State);
        Assert.Equal(FactionId.Player, (await RegionAsync(realm, "a")).Faction);
    }
}
