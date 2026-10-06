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
/// NPC factions raid (<c>docs/design/npc-factions.md</c> phase 2). What is pinned is Mike's shape:
/// they hit whoever borders them and nobody further, never a seat, never land under truce; a raid
/// takes resolve only; and a faction pays for every raid in strength, much more for a repelled one,
/// which Bloodies it so it cannot strike again for a while.
/// </summary>
public class FactionRaidTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private readonly FakeHubContext _hub = new();
    private static readonly DateTime Noon = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _db.Dispose();

    /// <summary>Dice the test chooses: every roll in [0, 1) is <c>Roll</c>, every d20 is <c>D20</c>.</summary>
    private sealed class FixedRandom : Random
    {
        public double Roll { get; set; }
        public int D20 { get; set; } = 15;
        public override double NextDouble() => Roll;
        public override int Next(int minValue, int maxValue) => maxValue == 21 ? D20 : minValue;
        public override int Next() => 0;
    }

    private readonly FixedRandom _dice = new();

    private FactionService Factions => new(_db, _log, new FakeGameContent(),
        new WarLogService(_db, _hub, NullLogger<WarLogService>.Instance, new FakeClock()), null, _hub, _dice);

    private static WorldRegionData Faction(FactionId faction, string id, int q, int r, int tier = 4, int entrenchment = 2)
    {
        var region = TestWorld.Region(id, q, r, LocationOwnership.Enemy, tier: tier);
        region.Faction = faction;
        region.Entrenchment = entrenchment;
        return region;
    }

    private static WorldRegionData Player(string id, int q, int r, string owner = TestIds.Player)
    {
        var region = TestWorld.Region(id, q, r, LocationOwnership.Player, owner, tier: 1);
        region.Entrenchment = 0;
        return region;
    }

    private async Task<Guid> RealmAsync(params WorldRegionData[] regions)
    {
        var instance = await _db.AddInstanceAsync();
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(regions));
        return instance.Id;
    }

    private async Task<WorldRegionData> RegionAsync(Guid realm, string id) => TestWorld.ReadRegion(await _db.ReadWorldAsync(realm), id);

    private async Task<FactionState> StateAsync(Guid realm, FactionId faction) =>
        await _db.FactionStates.AsNoTracking().SingleAsync(f => f.GameInstanceId == realm && f.Faction == faction);

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void ItActsOnlyWhenReady_AndNeverWhileBloodied()
    {
        Assert.Equal(0, FactionDecisionRules.ChanceToAct(0.59, 1, bloodied: false));
        Assert.Equal(0, FactionDecisionRules.ChanceToAct(1.0, 1, bloodied: true));
        Assert.Equal(FactionDecisionRules.BaseChancePerSweep, FactionDecisionRules.ChanceToAct(1.0, 1, bloodied: false), 9);
        Assert.Equal(FactionDecisionRules.BaseChancePerSweep / 2, FactionDecisionRules.ChanceToAct(0.6, 1, bloodied: false), 9);
        Assert.True(FactionDecisionRules.ChanceToAct(1.0, 1.25, false) > FactionDecisionRules.ChanceToAct(1.0, 0.8, false));
    }

    [Fact]
    public void ALeanTowardSomethingNotBuilt_IsATurnSpentOnNothing()
    {
        var ashkin = new FactionTemperament { Raid = 1, Fortify = 3 };
        Assert.Equal(FactionAction.Raid, FactionDecisionRules.PickAction(ashkin, 0.1));
        Assert.Equal(FactionAction.None, FactionDecisionRules.PickAction(ashkin, 0.9));
    }

    [Fact]
    public void ItTargetsOnlyWhatBordersItsLand()
    {
        var regions = new List<WorldRegionData>
        {
            Faction(FactionId.Grimjaw, "g", 0, 0),
            Player("near", 1, 0),
            Player("far", 5, 5),
            Faction(FactionId.Ashkin, "a", 0, 1)
        };

        var border = FactionDecisionRules.Bordering(FactionId.Grimjaw, regions).Select(r => r.RegionId).ToList();
        Assert.Contains("near", border);
        Assert.Contains("a", border);
        Assert.DoesNotContain("far", border);
        Assert.DoesNotContain("g", border);
    }

    [Fact]
    public void SeatsTruceAndUnclaimedLand_AreNotRaided()
    {
        var seat = Player("seat", 1, 0);
        seat.IsCapital = true;
        Assert.False(FactionDecisionRules.IsRaidable(FactionId.Grimjaw, seat, Noon));

        Assert.False(FactionDecisionRules.IsRaidable(FactionId.Grimjaw, TestWorld.Region("wild", 1, 0), Noon));
        Assert.False(FactionDecisionRules.IsRaidable(FactionId.Grimjaw, Faction(FactionId.Grimjaw, "own", 1, 0), Noon));
        Assert.True(FactionDecisionRules.IsRaidable(FactionId.Grimjaw, Player("p", 1, 0), Noon));
        Assert.True(FactionDecisionRules.IsRaidable(FactionId.Grimjaw, Faction(FactionId.Ashkin, "a", 1, 0), Noon));
    }

    // -----------------------------------------------------------------
    // The service
    // -----------------------------------------------------------------

    [Fact]
    public async Task ALandedRaid_TakesResolveOnly_AndCostsATenthOfTheMarch()
    {
        var realm = await RealmAsync(Faction(FactionId.Grimjaw, "g", 0, 0), Player("near", 1, 0));
        await Factions.ReadAsync(realm, Noon);
        double before = (await StateAsync(realm, FactionId.Grimjaw)).Strength;
        var ownerBefore = await RegionAsync(realm, "near");

        var done = await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw);

        Assert.Contains(done, l => l.Contains("landed"));
        var after = await RegionAsync(realm, "near");
        Assert.True(after.Resolve < ownerBefore.Resolve);
        Assert.Equal(TestIds.Player, after.OwnerUserId);

        var state = await StateAsync(realm, FactionId.Grimjaw);
        double march = before * FactionStrengthRules.RaidShare;
        Assert.Equal(before - march * FactionDecisionRules.WonRaidLoss, state.Strength, 3);
        Assert.Null(state.BloodiedUntilUtc);

        var line = Assert.Single(_db.WarLog.Where(e => e.Kind == nameof(WarLogKind.RaidLanded)));
        Assert.Equal("faction:Grimjaw", line.ActorUserId);
        Assert.Equal("The Grimjaw", line.ActorName);
        Assert.Equal(TestIds.Player, line.SubjectUserId);
        Assert.Contains(_hub.Sent, s => s.Method == "WorldViewGameDataUpdated");
    }

    [Fact]
    public async Task ARepelledRaid_CostsHalfTheMarch_AndBloodiesTheFaction()
    {
        var realm = await RealmAsync(Faction(FactionId.Grimjaw, "g", 0, 0), Player("near", 1, 0));
        await Factions.ReadAsync(realm, Noon);
        double before = (await StateAsync(realm, FactionId.Grimjaw)).Strength;
        _dice.D20 = 1;

        await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw);

        var state = await StateAsync(realm, FactionId.Grimjaw);
        Assert.Equal(before - before * FactionStrengthRules.RaidShare * FactionDecisionRules.LostRaidLoss, state.Strength, 3);
        Assert.Equal(Noon + FactionStrengthRules.BloodiedFor, state.BloodiedUntilUtc);
        Assert.Equal(100, (await RegionAsync(realm, "near")).Resolve);
        Assert.Single(_db.WarLog.Where(e => e.Kind == nameof(WarLogKind.RaidRepelled)));

        // Bloodied, it does nothing on its own however the dice fall.
        _dice.Roll = 0;
        Assert.Empty(await Factions.ActAsync(realm, Noon.AddHours(1)));
    }

    [Fact]
    public async Task OneRegion_IsRaidedByAFactionAtMostOnceInFourHours()
    {
        var realm = await RealmAsync(Faction(FactionId.Grimjaw, "g", 0, 0), Player("near", 1, 0));

        await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw);
        var again = await Factions.ActAsync(realm, Noon.AddHours(3), force: FactionId.Grimjaw);
        Assert.Contains(again, l => l.Contains("nobody"));

        var later = await Factions.ActAsync(realm, Noon.AddHours(4).AddMinutes(1), force: FactionId.Grimjaw);
        Assert.Contains(later, l => l.Contains("raided"));
        Assert.Equal(2, await _db.FactionRaids.CountAsync());
    }

    [Fact]
    public async Task FactionsRaidEachOther_AndTheLogNamesBoth()
    {
        var realm = await RealmAsync(Faction(FactionId.Grimjaw, "g", 0, 0), Faction(FactionId.Ashkin, "a", 1, 0, tier: 1, entrenchment: 0));

        await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw);

        var line = Assert.Single(_db.WarLog.Where(e => e.Kind == nameof(WarLogKind.RaidLanded)));
        Assert.Equal("faction:Ashkin", line.SubjectUserId);
        Assert.Equal("The Ashkin", line.SubjectName);
        Assert.True((await RegionAsync(realm, "a")).Resolve < 100);
    }

    [Fact]
    public async Task OnItsOwnTurn_AReadyFactionActsByChance()
    {
        var realm = await RealmAsync(Faction(FactionId.Grimjaw, "g", 0, 0), Player("near", 1, 0));

        _dice.Roll = 0.99;
        Assert.Empty(await Factions.ActAsync(realm, Noon));

        _dice.Roll = 0;
        Assert.Contains(await Factions.ActAsync(realm, Noon), l => l.Contains("raided"));
    }

    [Fact]
    public async Task SimulatingHours_LetsTheFactionsActThroughThem()
    {
        var realm = await RealmAsync(Faction(FactionId.Grimjaw, "g", 0, 0), Player("near", 1, 0), Player("near2", 0, 1));
        _dice.Roll = 0;

        var response = await Factions.DebugAsync(realm, new FactionDebugRequest("", SimulateHours: 9), Noon);

        // Every sweep it may act, but one region only every four hours: two regions, three windows.
        Assert.Contains(response!.Report!, l => l.Contains("raided"));
        Assert.InRange(await _db.FactionRaids.CountAsync(), 4, 6);
        Assert.All(_db.WarLog, e => Assert.True(e.OccurredAt <= Noon && e.OccurredAt > Noon.AddHours(-9)));
    }
}
