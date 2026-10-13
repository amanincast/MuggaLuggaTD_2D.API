using System.Text.Json.Nodes;
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
/// Signature acts (Factions 2 spec §2.2, step 3). What a player should be able to rely on: each faction does one thing
/// no other does, and does it through the turn a sweep already runs. The Grimjaw's warband rides on from a raid that
/// lands; the Ashkin's walls restore more; the Drowned Court's won siege floods a neighbour; land taken from the Bone
/// Synod raids its new holder once, truce or not; the Thornwood spread into gentler land; the Horned Kin stampede
/// past the cooldown once a day. A faction without the act does none of it.
/// </summary>
public class FactionSignatureTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private readonly FakeHubContext _hub = new();
    private static readonly DateTime Noon = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

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

    private FactionService Factions => new(_db, _log, new FakeGameContent(),
        new WarLogService(_db, _hub, NullLogger<WarLogService>.Instance, new FakeClock()), null, _hub, _dice);

    private static WorldRegionData Faction(FactionId faction, string id, int q, int r, int tier = 4, int entrenchment = 2, int resolve = 100)
    {
        var region = TestWorld.Region(id, q, r, LocationOwnership.Enemy, tier: tier);
        region.Faction = faction;
        region.Entrenchment = entrenchment;
        region.Resolve = resolve;
        region.OwnerDisplayName = FactionCatalog.TitleOf(faction);
        return region;
    }

    private static WorldRegionData Player(string id, int q, int r, int resolve = 100)
    {
        var region = TestWorld.Region(id, q, r, LocationOwnership.Player, TestIds.Player, tier: 1);
        region.Entrenchment = 0;
        region.Resolve = resolve;
        return region;
    }

    private async Task<Guid> RealmAsync(params WorldRegionData[] regions)
    {
        var instance = await _db.AddInstanceAsync();
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(regions));
        if (!await _db.Users.AnyAsync(u => u.Id == TestIds.Player))
            _db.Users.Add(new ApplicationUser { Id = TestIds.Player, UserName = "Mike", DisplayName = "Mike" });
        await _db.SaveChangesAsync();
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, TestSave.ToJson(TestSave.Roster(TestSave.Character("hero-1", 20))));
        return instance.Id;
    }

    private async Task<WorldRegionData> RegionAsync(Guid realm, string id) => TestWorld.ReadRegion(await _db.ReadWorldAsync(realm), id);

    private async Task<FactionState> StateAsync(Guid realm, FactionId faction) =>
        await _db.FactionStates.AsNoTracking().SingleAsync(f => f.GameInstanceId == realm && f.Faction == faction);

    private List<FactionRaid> Raids(Guid realm) =>
        _db.FactionRaids.AsNoTracking().Where(r => r.GameInstanceId == realm).OrderBy(r => r.Id).ToList();

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void EverySeatedFaction_HasItsOwnAct_AndTheRiftbornNone()
    {
        var seatable = FactionCatalog.All.Where(f => f.Seatable).ToList();
        Assert.All(seatable, f => Assert.NotEqual(FactionSignature.None, f.Signature));
        Assert.Equal(seatable.Count, seatable.Select(f => f.Signature).Distinct().Count());
        Assert.Equal(FactionSignature.None, FactionSignatureRules.Of(FactionId.Riftborn));
        Assert.Equal(FactionSignature.None, FactionSignatureRules.Of(FactionId.None));
        Assert.All(seatable, f => Assert.False(string.IsNullOrEmpty(FactionSignatureRules.Describe(f.Signature))));
        Assert.Equal("The tide", FactionSignatureRules.NameOf(FactionSignature.TheTide));
    }

    [Fact]
    public void TheNumbers_AreTheSpecs()
    {
        Assert.Equal(25, FactionSignatureRules.FortifyResolveBonus(FactionId.Ashkin));
        Assert.Equal(FactionGrowthRules.FortifyResolveBonus, FactionSignatureRules.FortifyResolveBonus(FactionId.Grimjaw));
        Assert.Equal(2, FactionGrowthRules.MinimumTierFor(FactionId.Thornwood));
        Assert.Equal(3, FactionGrowthRules.MinimumTierFor(FactionId.HornedKin));
        Assert.Equal(40, FactionSignatureRules.Flooded(60));
        Assert.Equal(0, FactionSignatureRules.Flooded(10));
        Assert.Equal(50, FactionSignatureRules.WarbandMarch(100));
    }

    [Fact]
    public void AStampede_IsARaidOnLandRaidedHoursBefore_OnceADay()
    {
        var cooldown = RaidResolver.Cooldown;
        var raids = new List<(string, DateTime)> { ("a", Noon), ("b", Noon.AddHours(1)) };
        Assert.True(FactionSignatureRules.MayStampede(raids, Noon.AddHours(2), cooldown));

        raids.Add(("a", Noon.AddHours(2)));
        Assert.True(FactionSignatureRules.WasStampede("a", Noon.AddHours(2), raids, cooldown));
        Assert.False(FactionSignatureRules.MayStampede(raids, Noon.AddHours(3), cooldown));

        // A day after the stampede, they may again.
        Assert.True(FactionSignatureRules.MayStampede(raids, Noon.AddHours(26).AddMinutes(1), cooldown));
    }

    [Fact]
    public void TheDead_RiseOnlyWithinADay_AndNeverAtACapital()
    {
        Assert.True(FactionSignatureRules.MayRise(Noon, Noon.AddHours(23)));
        Assert.False(FactionSignatureRules.MayRise(Noon, Noon.AddDays(1).AddMinutes(1)));
        Assert.True(FactionSignatureRules.RisesOnLoss(FactionId.BoneSynod));
        Assert.False(FactionSignatureRules.RisesOnLoss(FactionId.Grimjaw));

        var seat = Player("seat", 1, 0);
        seat.IsCapital = true;
        Assert.False(FactionSignatureRules.MayRaidBack(FactionId.BoneSynod, seat));
        Assert.True(FactionSignatureRules.MayRaidBack(FactionId.BoneSynod, Player("p", 1, 0)));
        Assert.False(FactionSignatureRules.MayRaidBack(FactionId.BoneSynod, Faction(FactionId.BoneSynod, "own", 1, 0)));
    }

    // -----------------------------------------------------------------
    // The service
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheGrimjawWarband_RidesOnAtHalfMarch_IntoASecondRegion()
    {
        var realm = await RealmAsync(Faction(FactionId.Grimjaw, "g", 0, 0), Player("east", 1, 0), Player("south", 0, 1));
        var done = await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw);

        // Both at the same moment, with Guid ids: the warband is the one with the smaller march.
        var raids = Raids(realm).OrderByDescending(r => r.March).ToList();
        Assert.Equal(2, raids.Count);
        Assert.True(raids[0].AttackerWon);
        Assert.NotEqual(raids[0].RegionId, raids[1].RegionId);
        Assert.Equal(Math.Round(raids[0].March * FactionSignatureRules.WarbandMarchShare), raids[1].March);
        Assert.Contains(done, l => l.Contains("the warband rode on"));
        Assert.Contains(_db.WarLog, e => e.RegionId == raids[1].RegionId && e.Detail!.StartsWith("the warband rode on"));
    }

    [Fact]
    public async Task AFactionWithoutAWarband_RaidsOnce()
    {
        var realm = await RealmAsync(Faction(FactionId.Ashkin, "a", 0, 0), Player("east", 1, 0), Player("south", 0, 1));
        await Factions.ActAsync(realm, Noon, force: FactionId.Ashkin);
        Assert.Single(Raids(realm));
    }

    [Fact]
    public async Task TheHornedKin_StampedePastTheCooldown_OnceADay()
    {
        var realm = await RealmAsync(Faction(FactionId.HornedKin, "h", 0, 0), Player("near", 1, 0));

        await Factions.ActAsync(realm, Noon, force: FactionId.HornedKin);
        var again = await Factions.ActAsync(realm, Noon.AddHours(1), force: FactionId.HornedKin);
        Assert.Contains(again, l => l.Contains("a stampede"));
        Assert.Equal(2, Raids(realm).Count(r => r.RegionId == "near"));

        // Spent for the day: the cooldown holds them now.
        var third = await Factions.ActAsync(realm, Noon.AddHours(2), force: FactionId.HornedKin);
        Assert.Contains(third, l => l.Contains("found nobody"));
        Assert.Equal(2, Raids(realm).Count);
    }

    [Fact]
    public async Task OthersWait_OutTheCooldown()
    {
        var realm = await RealmAsync(Faction(FactionId.Ashkin, "a", 0, 0), Player("near", 1, 0));
        await Factions.ActAsync(realm, Noon, force: FactionId.Ashkin);
        var again = await Factions.ActAsync(realm, Noon.AddHours(1), force: FactionId.Ashkin);
        Assert.Contains(again, l => l.Contains("found nobody"));
    }

    [Fact]
    public async Task TheAshkinsDeepRoots_Restore25Resolve_OthersRestore15()
    {
        var realm = await RealmAsync(Faction(FactionId.Ashkin, "a", 0, 0, entrenchment: 0, resolve: 50),
            Faction(FactionId.Grimjaw, "g", 5, 5, entrenchment: 0, resolve: 50));

        await Factions.ActAsync(realm, Noon, force: FactionId.Ashkin, forceAction: FactionAction.Fortify);
        await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw, forceAction: FactionAction.Fortify);

        Assert.Equal(75, (await RegionAsync(realm, "a")).Resolve);
        Assert.Equal(65, (await RegionAsync(realm, "g")).Resolve);
    }

    [Fact]
    public async Task TheThornwoodsOvergrowth_SpreadsIntoTierTwo_WhereOthersCannot()
    {
        var realm = await RealmAsync(Faction(FactionId.Thornwood, "t", 0, 0), TestWorld.Region("glade", 1, 0, tier: 2),
            Faction(FactionId.Grimjaw, "g", 6, 6), TestWorld.Region("meadow", 7, 6, tier: 2));

        var grown = await Factions.ActAsync(realm, Noon, force: FactionId.Thornwood, forceAction: FactionAction.Expand);
        Assert.Contains(grown, l => l.Contains("claimed glade"));
        Assert.Equal(FactionId.Thornwood, (await RegionAsync(realm, "glade")).Faction);

        var held = await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw, forceAction: FactionAction.Expand);
        Assert.Contains(held, l => l.Contains("found no wild land"));
    }

    [Fact]
    public async Task TheDrownedCourtsTide_FloodsANeighbour_OfLandItTakes()
    {
        var realm = await RealmAsync(Faction(FactionId.DrownedCourt, "d", 0, 0), Player("near", 1, 0, resolve: 40),
            Player("shore", 2, 0, resolve: 60));

        var declared = await Factions.ActAsync(realm, Noon, force: FactionId.DrownedCourt, forceAction: FactionAction.Siege);
        Assert.Contains(declared, l => l.Contains("laid siege to near"));

        var done = await Factions.SettleDueSiegesAsync(realm, Noon + SiegeRules.Muster + TimeSpan.FromMinutes(1));
        Assert.Contains(done, l => l.Contains("took near"));
        Assert.Contains(done, l => l.Contains("tide flooded shore"));
        Assert.Equal(40, (await RegionAsync(realm, "shore")).Resolve);

        var line = Assert.Single(_db.WarLog.Where(e => e.Kind == nameof(WarLogKind.RaidLanded)));
        Assert.Equal("faction:DrownedCourt", line.ActorUserId);
        Assert.Equal(TestIds.Player, line.SubjectUserId);
        Assert.Equal("shore", line.RegionId);
        Assert.StartsWith("the tide", line.Detail);
    }

    [Fact]
    public async Task OthersSieges_FloodNothing()
    {
        var realm = await RealmAsync(Faction(FactionId.Grimjaw, "g", 0, 0), Player("near", 1, 0, resolve: 40),
            Player("shore", 2, 0, resolve: 60));
        await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw, forceAction: FactionAction.Siege);
        await Factions.SettleDueSiegesAsync(realm, Noon + SiegeRules.Muster + TimeSpan.FromMinutes(1));
        Assert.Equal(60, (await RegionAsync(realm, "shore")).Resolve);
    }

    /// <summary>The player takes the Bone Synod's "b" at <paramref name="at"/>, as a conquest would.</summary>
    private async Task TakeAsync(Guid realm, string id, DateTime at)
    {
        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == realm);
        var world = JsonNode.Parse(row.GameData)!;
        WorldRegionBlob.CaptureRegion(WorldRegionBlob.FindRegion(world, id)!, TestIds.Player, "Mike", at);
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task LandTakenFromTheBoneSynod_IsRaidedOnce_ThroughTheTruce_AtNoCost()
    {
        _dice.Roll = 0.999; // nobody acts of their own accord this turn
        var realm = await RealmAsync(Faction(FactionId.BoneSynod, "b", 0, 0, tier: 1, entrenchment: 0),
            Faction(FactionId.BoneSynod, "b2", -1, 0), Player("p", 1, 0));
        await Factions.ReadAsync(realm, Noon);
        await TakeAsync(realm, "b", Noon);
        Assert.True(SiegeRules.IsUnderTruce(await RegionAsync(realm, "b"), Noon.AddMinutes(1)));

        var done = await Factions.ActAsync(realm, Noon.AddMinutes(1));

        var raid = Assert.Single(Raids(realm));
        Assert.Equal(FactionId.BoneSynod, raid.Faction);
        Assert.Equal("b", raid.RegionId);
        Assert.Equal(TestIds.Player, raid.DefenderUserId);
        Assert.Contains(done, l => l.Contains("the dead rose"));
        // Not charged: no cost, no Bloodied, not counted as their turn.
        var synod = await StateAsync(realm, FactionId.BoneSynod);
        Assert.Null(synod.LastActedAtUtc);
        Assert.Null(synod.BloodiedUntilUtc);

        // Once.
        await Factions.ActAsync(realm, Noon.AddHours(5));
        Assert.Single(Raids(realm));
    }

    [Fact]
    public async Task TheDead_StayDown_AfterADay_OrForAnotherFactionsLand()
    {
        _dice.Roll = 0.999;
        var realm = await RealmAsync(Faction(FactionId.BoneSynod, "b", 0, 0), Faction(FactionId.BoneSynod, "b2", -1, 0),
            Faction(FactionId.Grimjaw, "g", 4, 4), Faction(FactionId.Grimjaw, "g2", 5, 4), Player("p", 1, 0));
        await Factions.ReadAsync(realm, Noon);
        await TakeAsync(realm, "b", Noon);
        await TakeAsync(realm, "g", Noon);

        await Factions.ActAsync(realm, Noon.AddDays(2));

        Assert.Empty(Raids(realm));
        Assert.Empty(WorldRegionBlob.Risen(await _db.ReadWorldAsync(realm)));
    }

    [Fact]
    public async Task TheSignatureDebug_FiresTheBoneSynodsAct()
    {
        _dice.Roll = 0.999;
        var realm = await RealmAsync(Faction(FactionId.BoneSynod, "b", 0, 0, tier: 1, entrenchment: 0),
            Faction(FactionId.BoneSynod, "b2", -1, 0), Player("p", 1, 0));

        var response = await Factions.DebugAsync(realm, new FactionDebugRequest("BoneSynod", ForceAct: true, Action: "Signature"), Noon);

        Assert.Contains(response!.Report!, l => l.Contains("lost b"));
        Assert.Contains(response.Report!, l => l.Contains("the dead rose"));
        Assert.True((await RegionAsync(realm, "b")).IsOwnedByPlayer(TestIds.Player));
    }
}
