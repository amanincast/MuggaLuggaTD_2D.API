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
/// NPC factions lay sieges (<c>docs/design/npc-factions.md</c> phase 3; Mike, 2026-10-06). Pinned: a
/// faction besieges only bordering player land already worn to the resolve gate; its muster is settled
/// by the server at close, through the resolver unless the defender raised the hold past the gate;
/// falling, the region goes to the faction wrecked with its garrison captured; and the defender may
/// break it once with a sortie, which costs the faction its march and Bloodies it.
/// </summary>
public class FactionSiegeTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private readonly FakeHubContext _hub = new();
    private static readonly DateTime Noon = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MusterClose = Noon + SiegeRules.Muster;

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

    private static WorldRegionData Grimjaw(string id = "g", int q = 0, int r = 0)
    {
        var region = TestWorld.Region(id, q, r, LocationOwnership.Enemy, tier: 4);
        region.Faction = FactionId.Grimjaw;
        region.Entrenchment = 2;
        return region;
    }

    private static WorldRegionData Player(string id = "near", int q = 1, int r = 0, int resolve = 40)
    {
        var region = TestWorld.Region(id, q, r, LocationOwnership.Player, TestIds.Player, tier: 1);
        region.Entrenchment = 0;
        region.Resolve = resolve;
        return region;
    }

    /// <summary>A realm with the Grimjaw beside the player's region, its keep garrisoned by <paramref name="garrison"/>.</summary>
    private async Task<Guid> RealmAsync(int resolve = 40, string[]? garrison = null, float garrisonPower = 0)
    {
        var instance = await _db.AddInstanceAsync();
        var near = Player(resolve: resolve);
        var world = TestWorld.Blob(Grimjaw(), near);
        if (garrison != null || garrisonPower > 0)
        {
            var entry = WorldRegionBlob.EnsureOverride(WorldRegionBlob.FindRegion(world, near.RegionId)!, TestWorld.KeepIn(near));
            entry["GarrisonCharacterIds"] = new JsonArray((garrison ?? Array.Empty<string>()).Select(id => (JsonNode)id!).ToArray());
            entry["GarrisonPower"] = garrisonPower;
        }
        await _db.AddWorldAsync(instance.Id, world);

        _db.Users.Add(new ApplicationUser { Id = TestIds.Player, UserName = "Mike", DisplayName = "Mike" });
        await _db.SaveChangesAsync();
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player,
            TestSave.ToJson(TestSave.Roster(TestSave.Character("hero-1", 20), TestSave.Character("hero-2", 20))));
        return instance.Id;
    }

    private async Task<WorldRegionData> RegionAsync(Guid realm, string id = "near") =>
        TestWorld.ReadRegion(await _db.ReadWorldAsync(realm), id);

    private async Task<FactionState> StateAsync(Guid realm) =>
        await _db.FactionStates.AsNoTracking().SingleAsync(f => f.GameInstanceId == realm && f.Faction == FactionId.Grimjaw);

    private async Task<FactionSiege> SiegeAsync(Guid realm) =>
        await _db.FactionSieges.AsNoTracking().SingleAsync(s => s.GameInstanceId == realm);

    private async Task<FactionSiege> DeclareAsync(Guid realm)
    {
        var done = await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw, forceAction: FactionAction.Siege);
        Assert.Contains(done, l => l.Contains("laid siege"));
        return await SiegeAsync(realm);
    }

    private async Task GarrisonAsync(Guid realm, float power)
    {
        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == realm);
        var world = JsonNode.Parse(row.GameData)!;
        var near = WorldRegionBlob.ReadRegion(WorldRegionBlob.FindRegion(world, "near")!);
        WorldRegionBlob.EnsureOverride(WorldRegionBlob.FindRegion(world, "near")!, TestWorld.KeepIn(near))["GarrisonPower"] = power;
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();
    }

    private static FactionSortieRequest Sortie(params string[] ids) =>
        new(ids.ToList(), MuggaLuggaTD.Shared.SharedContract.Version);

    private static SiegeAssaultClaimRequest Claim(Guid run, bool won) =>
        new(run, won, MuggaLuggaTD.Shared.SharedContract.Version);

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void OnlyWornPlayerLand_OffTheSeatAndOutOfTruce_IsBesiegeable()
    {
        Assert.True(FactionSiegeRules.IsBesiegeable(FactionId.Grimjaw, Player(resolve: 50), Noon));
        Assert.False(FactionSiegeRules.IsBesiegeable(FactionId.Grimjaw, Player(resolve: 51), Noon));

        var seat = Player(resolve: 10);
        seat.IsCapital = true;
        Assert.False(FactionSiegeRules.IsBesiegeable(FactionId.Grimjaw, seat, Noon));

        var truce = Player(resolve: 10);
        truce.ClaimedAtUtcTicks = (Noon - TimeSpan.FromHours(2)).Ticks;
        Assert.False(FactionSiegeRules.IsBesiegeable(FactionId.Grimjaw, truce, Noon));

        // Another faction's land waits for phase 4.
        var ashkin = Grimjaw("a");
        ashkin.Faction = FactionId.Ashkin;
        ashkin.Resolve = 10;
        Assert.False(FactionSiegeRules.IsBesiegeable(FactionId.Grimjaw, ashkin, Noon));
    }

    [Fact]
    public void AHoldRaisedPastTheGate_TurnsTheSiegeAwayWithoutARoll()
    {
        var turned = FactionSiegeRules.Settle(march: 100, frozenHold: 1000, d20Roll: 20);
        Assert.False(turned.Fell);
        Assert.True(turned.BelowGate);

        Assert.True(FactionSiegeRules.Settle(march: 5000, frozenHold: 1000, d20Roll: 20).Fell);
        Assert.False(FactionSiegeRules.Settle(march: 700, frozenHold: 1000, d20Roll: 1).Fell);
    }

    [Fact]
    public void AMusteringFaction_DoesNothingElse_AndSiegeIsNowBuilt()
    {
        Assert.Equal(0, FactionDecisionRules.ChanceToAct(1.0, 2, bloodied: false, mustering: true));
        Assert.Equal(FactionAction.Siege, FactionDecisionRules.PickAction(new FactionTemperament { Raid = 0, Siege = 1 }, 0.5));
    }

    [Fact]
    public void TheSortie_IsAnAssaultTurnedRound_WithTheWarbandsCaptains()
    {
        Assert.Equal(FactionSiegeRules.MinimumCaptains, FactionSiegeRules.CaptainsOf(100));
        Assert.Equal(FactionSiegeRules.MaximumCaptains, FactionSiegeRules.CaptainsOf(1_000_000));

        var small = FactionSiegeRules.SortieEncounter(sortiePower: 500, march: 6000, armySize: 2);
        var big = FactionSiegeRules.SortieEncounter(sortiePower: 20000, march: 6000, armySize: 2);
        Assert.Equal(SiegeAssaultRules.MaximumWaves, small.Waves);
        Assert.Equal(SiegeAssaultRules.MinimumWaves, big.Waves);
        Assert.Equal(3, small.EliteCount);
    }

    // -----------------------------------------------------------------
    // Declaring
    // -----------------------------------------------------------------

    [Fact]
    public async Task AFactionBesiegesWornBorderLand_InTheOpen()
    {
        var realm = await RealmAsync(resolve: 40);
        await Factions.ReadAsync(realm, Noon);
        double strength = (await StateAsync(realm)).Strength;

        var siege = await DeclareAsync(realm);

        Assert.Equal("near", siege.RegionId);
        Assert.Equal(TestIds.Player, siege.DefenderUserId);
        Assert.Equal(SiegeState.Mustering, siege.State);
        Assert.Equal(MusterClose, siege.MusterEndsAt);
        Assert.Equal(Math.Round(strength * FactionStrengthRules.SiegeShare), siege.March);

        var line = Assert.Single(_db.WarLog.Where(e => e.Kind == nameof(WarLogKind.SiegeDeclared)));
        Assert.Equal("faction:Grimjaw", line.ActorUserId);
        Assert.Equal("The Grimjaw", line.ActorName);
        Assert.Equal(TestIds.Player, line.SubjectUserId);
        Assert.Contains(_hub.Sent, s => s.Method == "SiegeUpdated");

        // The players' list shows it in the shape of any siege.
        var listed = Assert.Single(await Factions.LiveSiegesAsync(realm, Noon.AddHours(1)));
        Assert.Equal("faction:Grimjaw", listed.AttackerUserId);
        Assert.Equal("Grimjaw", listed.AttackerFaction);
        Assert.Equal("Mustering", listed.State);
        Assert.False(listed.SortieBegun);

        // While it musters the faction stands Mustering and does nothing else.
        var read = await Factions.ReadAsync(realm, Noon.AddHours(1));
        var grimjaw = read!.Factions.Single(f => f.Faction == "Grimjaw");
        Assert.Equal(nameof(FactionReadiness.Mustering), grimjaw.Word);
        Assert.Equal("near", grimjaw.MusteringAgainst);
        var again = await Factions.ActAsync(realm, Noon.AddHours(1), force: FactionId.Grimjaw);
        Assert.Contains(again, l => l.Contains("mustering"));
        Assert.Empty(_db.FactionRaids);
    }

    [Fact]
    public async Task LandWithItsResolveUp_IsNotRipe()
    {
        var realm = await RealmAsync(resolve: 80);

        var done = await Factions.ActAsync(realm, Noon, force: FactionId.Grimjaw, forceAction: FactionAction.Siege);

        Assert.Contains(done, l => l.Contains("nothing on its border ripe"));
        Assert.Empty(_db.FactionSieges);
    }

    // -----------------------------------------------------------------
    // Settling at muster close
    // -----------------------------------------------------------------

    [Fact]
    public async Task AtMusterClose_TheRegionFalls_WreckedAndItsGarrisonTaken()
    {
        var realm = await RealmAsync(resolve: 40, garrison: new[] { "hero-2" });
        var siege = await DeclareAsync(realm);
        double strength = (await StateAsync(realm)).Strength;

        // Nothing before the muster closes.
        Assert.Empty(await Factions.SettleDueSiegesAsync(realm, MusterClose.AddMinutes(-1)));

        var done = await Factions.SettleDueSiegesAsync(realm, MusterClose.AddMinutes(1));
        Assert.Contains(done, l => l.Contains("took near"));

        var region = await RegionAsync(realm);
        Assert.Equal(FactionId.Grimjaw, region.Faction);
        Assert.True(string.IsNullOrEmpty(region.OwnerUserId));
        Assert.Equal(LocationOwnership.Enemy, region.Ownership);
        Assert.Equal(SiegeAssaultRules.WreckedResolve, region.Resolve);
        Assert.Equal(0, region.Entrenchment);
        Assert.True(SiegeRules.IsUnderTruce(region, MusterClose.AddHours(1)));
        Assert.Contains(region.SiteOverrides.Values, o => o.CapturedCharacterIds.Contains("hero-2"));

        var settled = await SiegeAsync(realm);
        Assert.Equal(SiegeState.Won, settled.State);
        Assert.Equal(1, settled.Captured);
        Assert.Equal(MusterClose, settled.ResolvedAt);
        Assert.True((await StateAsync(realm)).Strength < strength);

        var line = Assert.Single(_db.WarLog.Where(e => e.Kind == nameof(WarLogKind.SiegeWon)));
        Assert.Equal("faction:Grimjaw", line.ActorUserId);
        Assert.Contains("1 champion taken prisoner", line.Detail);
        Assert.Empty(await Factions.LiveSiegesAsync(realm, MusterClose.AddMinutes(2)));
    }

    [Fact]
    public async Task ABadRollAtTheWalls_CostsTheWholeMarch_AndBloodiesTheFaction()
    {
        var realm = await RealmAsync(resolve: 40);
        var siege = await DeclareAsync(realm);
        // A garrison that keeps the hold inside the gate, but close enough that the dice matter.
        await GarrisonAsync(realm, (float)(siege.March * 1.5));
        double strength = (await StateAsync(realm)).Strength;
        _dice.D20 = 1;

        await Factions.SettleDueSiegesAsync(realm, MusterClose);

        Assert.Equal(SiegeState.Repelled, (await SiegeAsync(realm)).State);
        var region = await RegionAsync(realm);
        Assert.Equal(TestIds.Player, region.OwnerUserId);
        Assert.Equal(40 + SiegeAssaultRules.RepelResolveBonus, region.Resolve);

        var state = await StateAsync(realm);
        Assert.Equal(MusterClose + FactionStrengthRules.BloodiedFor, state.BloodiedUntilUtc);
        Assert.True(state.Strength <= strength - siege.March + 1);
        Assert.Single(_db.WarLog.Where(e => e.Kind == nameof(WarLogKind.SiegeRepelled)));
    }

    [Fact]
    public async Task ReinforcingPastTheGate_TurnsItAwayWithNoRoll()
    {
        var realm = await RealmAsync(resolve: 40);
        await DeclareAsync(realm);
        await GarrisonAsync(realm, 10_000_000f);

        await Factions.SettleDueSiegesAsync(realm, MusterClose);

        var siege = await SiegeAsync(realm);
        Assert.Equal(SiegeState.Repelled, siege.State);
        Assert.Equal(0, siege.D20Roll);
        Assert.Contains("the walls outgrew it", _db.WarLog.Single(e => e.Kind == nameof(WarLogKind.SiegeRepelled)).Detail);
    }

    [Fact]
    public async Task ARegionThatChangedHands_CallsTheSiegeOff()
    {
        var realm = await RealmAsync(resolve: 40);
        await DeclareAsync(realm);

        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == realm);
        var world = JsonNode.Parse(row.GameData)!;
        WorldRegionBlob.CaptureRegion(WorldRegionBlob.FindRegion(world, "near")!, TestIds.Rival, "Rival", Noon.AddHours(1));
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();

        await Factions.SettleDueSiegesAsync(realm, MusterClose);

        Assert.Equal(SiegeState.Cancelled, (await SiegeAsync(realm)).State);
        Assert.Equal(TestIds.Rival, (await RegionAsync(realm)).OwnerUserId);
    }

    [Fact]
    public async Task PassingTheHours_SettlesALiveSiege()
    {
        var realm = await RealmAsync(resolve: 40);
        await DeclareAsync(realm);

        var response = await Factions.DebugAsync(realm, new FactionDebugRequest("Grimjaw", SimulateHours: 9), Noon);

        Assert.NotEqual(SiegeState.Mustering, (await SiegeAsync(realm)).State);
        Assert.Contains(response!.Report!, l => l.Contains("near"));
    }

    [Fact]
    public async Task TheDebugControls_LayASiegeAndCloseItsMuster()
    {
        var realm = await RealmAsync(resolve: 40);

        await Factions.DebugAsync(realm, new FactionDebugRequest("Grimjaw", ForceAct: true, Action: "Siege"), Noon);
        Assert.Equal(SiegeState.Mustering, (await SiegeAsync(realm)).State);

        var closed = await Factions.DebugAsync(realm, new FactionDebugRequest("Grimjaw", CloseMuster: true), Noon.AddHours(1));
        Assert.NotEqual(SiegeState.Mustering, (await SiegeAsync(realm)).State);
        Assert.Equal(Noon.AddHours(1), (await SiegeAsync(realm)).ResolvedAt);
        Assert.Contains(closed!.Report!, l => l.Contains("near"));
    }

    // -----------------------------------------------------------------
    // Break the siege
    // -----------------------------------------------------------------

    [Fact]
    public async Task AWonSortie_BreaksTheSiege_AtTheFactionsWholeMarch()
    {
        var realm = await RealmAsync(resolve: 40);
        var siege = await DeclareAsync(realm);
        double strength = (await StateAsync(realm)).Strength;

        var (begun, sortie) = await Factions.BeginSortieAsync(realm, TestIds.Player, siege.Id, Sortie("hero-1"), Noon.AddHours(1));
        Assert.True(begun.Succeeded, begun.Message);
        Assert.Equal(new[] { "hero-1" }, sortie!.ArmyCharacterIds);
        Assert.InRange(sortie.Waves, SiegeAssaultRules.MinimumWaves, SiegeAssaultRules.MaximumWaves);
        Assert.Equal(FactionSiegeRules.CaptainsOf(siege.March), sortie.EliteCount);
        Assert.True((await SiegeAsync(realm)).SortieRunId != null);

        // One sortie per siege.
        var (twice, _) = await Factions.BeginSortieAsync(realm, TestIds.Player, siege.Id, Sortie("hero-2"), Noon.AddHours(1));
        Assert.Equal(SiegeError.AssaultSpent, twice.Error);

        var (claimed, result) = await Factions.ClaimSortieAsync(realm, TestIds.Player, siege.Id, Claim(sortie.RunId, won: true), Noon.AddHours(1).AddMinutes(5));
        Assert.True(claimed.Succeeded, claimed.Message);
        Assert.Equal("Broken", result!.Outcome);

        var broken = await SiegeAsync(realm);
        Assert.Equal(SiegeState.Repelled, broken.State);
        Assert.True(broken.Broken);
        Assert.Equal(40 + SiegeAssaultRules.RepelResolveBonus, (await RegionAsync(realm)).Resolve);

        var state = await StateAsync(realm);
        Assert.NotNull(state.BloodiedUntilUtc);
        Assert.True(state.Strength < strength - siege.March + 100);

        var line = Assert.Single(_db.WarLog.Where(e => e.Kind == nameof(WarLogKind.SiegeBroken)));
        Assert.Equal(TestIds.Player, line.ActorUserId);
        Assert.Equal("faction:Grimjaw", line.SubjectUserId);
        Assert.Equal("The Grimjaw", line.SubjectName);

        // Broken, there is nothing left to settle at muster close.
        Assert.Empty(await Factions.SettleDueSiegesAsync(realm, MusterClose));
    }

    [Fact]
    public async Task ALostSortie_BloodiesTheParty_AndTheSiegeGoesOn()
    {
        var realm = await RealmAsync(resolve: 40);
        var siege = await DeclareAsync(realm);
        var (_, sortie) = await Factions.BeginSortieAsync(realm, TestIds.Player, siege.Id, Sortie("hero-1"), Noon.AddHours(1));

        var (claimed, result) = await Factions.ClaimSortieAsync(realm, TestIds.Player, siege.Id, Claim(sortie!.RunId, won: false), Noon.AddHours(1).AddMinutes(5));

        Assert.True(claimed.Succeeded);
        Assert.Equal("Held", result!.Outcome);
        Assert.Equal(SiegeState.Mustering, (await SiegeAsync(realm)).State);
        Assert.Contains(_db.BloodiedCharacters, b => b.CharacterId == "hero-1" && b.UserId == TestIds.Player);

        await Factions.SettleDueSiegesAsync(realm, MusterClose);
        Assert.NotEqual(SiegeState.Mustering, (await SiegeAsync(realm)).State);
    }

    [Fact]
    public async Task OnlyTheDefenderSalliesOut_AndASortieInTheFieldHoldsTheSettlement()
    {
        var realm = await RealmAsync(resolve: 40);
        var siege = await DeclareAsync(realm);

        var (stranger, _) = await Factions.BeginSortieAsync(realm, TestIds.Rival, siege.Id, Sortie("hero-1"), Noon.AddHours(1));
        Assert.Equal(SiegeError.NotDefender, stranger.Error);

        // Begun just before the muster closes: the close waits for the claim.
        var (_, sortie) = await Factions.BeginSortieAsync(realm, TestIds.Player, siege.Id, Sortie("hero-1"), MusterClose.AddMinutes(-5));
        Assert.Empty(await Factions.SettleDueSiegesAsync(realm, MusterClose.AddMinutes(10)));

        var (claimed, result) = await Factions.ClaimSortieAsync(realm, TestIds.Player, siege.Id, Claim(sortie!.RunId, won: true), MusterClose.AddMinutes(15));
        Assert.True(claimed.Succeeded, claimed.Message);
        Assert.Equal("Broken", result!.Outcome);
    }

    [Fact]
    public async Task AWinReportedImplausiblyFast_IsRefused()
    {
        var realm = await RealmAsync(resolve: 40);
        var siege = await DeclareAsync(realm);
        var (_, sortie) = await Factions.BeginSortieAsync(realm, TestIds.Player, siege.Id, Sortie("hero-1"), Noon.AddHours(1));

        var (fast, _) = await Factions.ClaimSortieAsync(realm, TestIds.Player, siege.Id, Claim(sortie!.RunId, won: true), Noon.AddHours(1).AddSeconds(2));

        Assert.Equal(SiegeError.TooFast, fast.Error);
        Assert.Equal(SiegeState.Mustering, (await SiegeAsync(realm)).State);
    }

    [Fact]
    public async Task ASeasonReset_ClearsTheFactionsSieges()
    {
        var realm = await RealmAsync(resolve: 40);
        await DeclareAsync(realm);

        await FactionService.ResetRealmAsync(_db, realm);
        await _db.SaveChangesAsync();

        Assert.Empty(_db.FactionSieges);
    }
}
