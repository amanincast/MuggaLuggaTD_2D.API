using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Reclaiming phase 3a, the horde returns (spec §6, Mike 2026-11-16). Land left unruly or invaded for 12 hours gathers a
/// horde; fixing the cause scatters it; at the end of its 8 hours the garrison's hold faces it with a siege's dice. Land
/// that falls stays its holder's but overrun, only they take it back and for the fight's pay alone, and after three days
/// it lapses to the wild.
/// </summary>
public class HordeReturnTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeGameContent _content = new();
    private readonly FixedDice _dice = new();
    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private const string Hero = "hero";

    private sealed class FixedDice : Random
    {
        public int D20 { get; set; } = 15;
        public override int Next(int minValue, int maxValue) => maxValue == 21 ? D20 : minValue;
        public override double NextDouble() => 0.5;
    }

    private GoldService Gold => new(_db, new FakeSessionLog(), NullLogger<GoldService>.Instance);
    private MaterialWalletService Wallet => new(_db, new FakeSessionLog(), NullLogger<MaterialWalletService>.Instance);
    private TavernService Tavern => new(_db, _content, Wallet, Gold, new FakeSessionLog(), NullLogger<TavernService>.Instance);
    private ItemLedgerService Items => new(_db, new FakeSessionLog(), NullLogger<ItemLedgerService>.Instance);
    private WorldPveService Pve => new(_db, _content, Wallet, Gold, Tavern, NullLogger<WorldPveService>.Instance, Items);
    private LetterService Letters => new(_db, new FakeHubContext(), NullLogger<LetterService>.Instance, new FakeClock(Now));

    private HordeReturnService Returns => new(_db, _content, new FakeSessionLog(), NullLogger<HordeReturnService>.Instance,
        new FakeHubContext(), Letters, null, _dice);

    public void Dispose() => _db.Dispose();

    // -----------------------------------------------------------------
    // The world
    // -----------------------------------------------------------------

    private static WorldRegionData Seat() =>
        new Func<WorldRegionData>(() => { var r = TestWorld.OwnedBy(TestIds.Player, "r0"); r.IsCapital = true; return r; })();

    /// <summary>Land claimed from the wild two hexes out: it needs a steward, and has none.</summary>
    private static WorldRegionData Unruly(string id = "r2", int q = 2, int r = 0, TimeSpan? since = null)
    {
        var region = TestWorld.Region(id, q, r, LocationOwnership.Player, TestIds.Player);
        region.Horde = HordeRules.NewWildHorde(2);
        region.Horde.ReclaimedAtUtcTicks = (Now - TimeSpan.FromDays(2)).Ticks;
        region.StewardNeeded = true;
        if (since.HasValue) region.UnrulySinceUtcTicks = (Now - since.Value).Ticks;
        return region;
    }

    private static WorldRegionData Gathering(WorldRegionData region, TimeSpan startedAgo)
    {
        region.Gathering = HordeReturnRules.NewGathering(TestIds.Player, HordeCause.Unruly, Now - startedAgo);
        return region;
    }

    private async Task<Guid> SeedAsync(params WorldRegionData[] regions)
    {
        var instance = await _db.AddInstanceAsync(TestIds.Owner, TestIds.Owner);
        instance.SeasonStartedAt = Now - TimeSpan.FromDays(5);
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(regions));
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, TestSave.ToJson(TestSave.Roster(TestSave.Character(Hero, 12))));
        await _db.SaveChangesAsync();
        return instance.Id;
    }

    private async Task<WorldRegionData> StoredAsync(Guid instance, string regionId)
    {
        var row = await _db.WorldViewGameData.AsNoTracking().SingleAsync(w => w.GameInstanceId == instance);
        return WorldRegionBlob.ReadAllRegions(JsonNode.Parse(row.GameData)).Single(r => r.RegionId == regionId);
    }

    /// <summary>Stations <paramref name="ids"/> at the region's keep, priced at <paramref name="power"/>, the first appointed steward.</summary>
    private async Task GarrisonAsync(Guid instance, string regionId, double power, bool steward, params string[] ids)
    {
        var row = await _db.WorldViewGameData.SingleAsync(w => w.GameInstanceId == instance);
        var world = JsonNode.Parse(row.GameData)!;
        var node = WorldRegionBlob.FindRegion(world, regionId)!;
        var region = WorldRegionBlob.ReadRegion(node);
        WorldRegionBlob.SetGarrison(node, TestWorld.KeepIn(region), ids, power);
        if (steward) WorldRegionBlob.SetSteward(node, ids.FirstOrDefault());
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();
    }

    private Task<List<Letter>> LettersAsync(Guid instance, LetterKind kind) =>
        _db.Letters.AsNoTracking().Where(l => l.GameInstanceId == instance && l.Kind == kind.ToString()).ToListAsync();

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void TheUnrulyStamp_IsKeptWhileUnruly_AndClearedOnceStewarded()
    {
        var region = Unruly();
        long first = HordeReturnRules.UnrulyStamp(region, Now);
        Assert.Equal(Now.Ticks, first);
        region.UnrulySinceUtcTicks = first;
        Assert.Equal(first, HordeReturnRules.UnrulyStamp(region, Now.AddHours(5)));

        region.StewardId = Hero;
        region.SiteOverrides[TestWorld.KeepIn(region)] = new SiteOverride { GarrisonCharacterIds = new List<string> { Hero } };
        Assert.Equal(0, HordeReturnRules.UnrulyStamp(region, Now.AddHours(6)));
    }

    [Fact]
    public void ACause_MustStandTwelveHours_AndTheOlderOneCounts()
    {
        Assert.Equal(HordeCause.None, HordeReturnRules.CauseOf(Unruly(since: TimeSpan.FromHours(11)), Now, null).Cause);
        Assert.Equal(HordeCause.Unruly, HordeReturnRules.CauseOf(Unruly(since: TimeSpan.FromHours(12)), Now, null).Cause);

        var both = HordeReturnRules.CauseOf(Unruly(since: TimeSpan.FromHours(12)), Now, Now - TimeSpan.FromHours(20));
        Assert.Equal(HordeCause.Invaded, both.Cause);
        Assert.Equal(HordeCause.None, HordeReturnRules.CauseOf(TestWorld.OwnedBy(TestIds.Player, "r1"), Now, Now - TimeSpan.FromHours(11)).Cause);
    }

    [Fact]
    public void TheSeat_IsNeverStruck_EvenInvaded()
    {
        Assert.False(HordeReturnRules.CanBeStruck(Seat()));
        Assert.Equal(HordeCause.None, HordeReturnRules.CauseOf(Seat(), Now, Now - TimeSpan.FromDays(1)).Cause);
    }

    [Fact]
    public void OneGatheringPerPlayer_TheOldestCauseFirst_NoneOnSafeLand_NoneAtSeasonsEnd()
    {
        var older = Unruly("r2", 2, 0, TimeSpan.FromHours(20));
        var newer = Unruly("r3", 0, 2, TimeSpan.FromHours(14));
        var regions = new List<WorldRegionData> { Seat(), newer, older };
        var none = new Dictionary<string, DateTime>();

        var picks = HordeReturnRules.ToGather(regions, Now, none, seasonClosing: false);
        Assert.Equal("r2", Assert.Single(picks).Region.RegionId);
        Assert.Empty(HordeReturnRules.ToGather(regions, Now, none, seasonClosing: true));

        older.HordeSafeUntilUtcTicks = Now.AddHours(1).Ticks;
        Assert.Equal("r3", Assert.Single(HordeReturnRules.ToGather(regions, Now, none, false)).Region.RegionId);

        Gathering(newer, TimeSpan.FromHours(1));
        older.HordeSafeUntilUtcTicks = 0;
        Assert.Empty(HordeReturnRules.ToGather(regions, Now, none, false));
    }

    [Fact]
    public void EmptyLand_FallsOnMostRolls_AndAGarrisonTwiceTheHordeTurnsItAway()
    {
        var empty = Unruly();
        var all = new List<WorldRegionData> { Seat(), empty };
        double strength = HordeReturnRules.Strength(empty.Tier);
        long emptyHold = HordeReturnRules.Hold(empty, all, 0);
        Assert.True(HordeReturnRules.Settle(strength, emptyHold, 3).Fell, $"hold {emptyHold} vs {strength}");

        long patrolled = HordeReturnRules.Hold(empty, all, strength * 2);
        var turned = HordeReturnRules.Settle(strength, patrolled, 20);
        Assert.False(turned.Fell, $"hold {patrolled} vs {strength}");
    }

    [Fact]
    public void AReturnedHorde_IsShapedByTier_ButOnlyItsHolderFightsIt()
    {
        var region = TestWorld.OwnedBy(TestIds.Player, "r2");
        region.StewardNeeded = true;
        region.Horde = HordeRules.NewReturnedHorde(2, Now);
        var layout = RegionGenerator.Generate(region);

        Assert.True(HordeRules.IsOverrun(region));
        Assert.True(HordeRules.IsTiered(region));
        Assert.False(StewardRules.IsUnruly(region));
        var packs = HordeRules.Packs(region);
        Assert.Equal(HordeRules.WildPacks(2), packs.Count(p => !HordeRules.IsGuard(p.Id)));
        Assert.Equal(HordeRules.WildBaseLevel(2) + HordeRules.LairLevelsAbove, HordeRules.LevelAt(region, layout, HordeRules.LairOf(region, layout)!.SiteId));
        Assert.Null(HordeRules.WhyNotBreak(region, packs[0].Id, TestIds.Player));
        Assert.NotNull(HordeRules.WhyNotBreak(region, packs[0].Id, TestIds.Rival));
        Assert.Equal(Now + TimeSpan.FromDays(3), HordeReturnRules.LapsesAt(region));
    }

    // -----------------------------------------------------------------
    // The sweep
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheSweep_StampsUnrulyLand_AndGathersAHordeTwelveHoursOn()
    {
        var instance = await SeedAsync(Seat(), Unruly());

        await Returns.SweepAsync(instance, Now);
        var stamped = await StoredAsync(instance, "r2");
        Assert.Equal(Now.Ticks, stamped.UnrulySinceUtcTicks);
        Assert.Null(stamped.Gathering);

        var later = Now + HordeReturnRules.CauseLasts;
        await Returns.SweepAsync(instance, later);
        var gathering = (await StoredAsync(instance, "r2")).Gathering;
        Assert.NotNull(gathering);
        Assert.Equal(HordeCause.Unruly, gathering!.Cause);
        Assert.Equal(TestIds.Player, gathering.UserId);
        Assert.Equal((later + TimeSpan.FromHours(8)).Ticks, gathering.StrikesAtUtcTicks);
        var letter = Assert.Single(await LettersAsync(instance, LetterKind.HordeGathers));
        Assert.Equal("r2", letter.RegionId);
        Assert.Equal("Unruly", letter.Detail);
    }

    [Fact]
    public async Task AStewardStationedThere_ScattersIt()
    {
        var instance = await SeedAsync(Seat(), Gathering(Unruly(since: TimeSpan.FromHours(14)), TimeSpan.FromHours(2)));
        await GarrisonAsync(instance, "r2", 900, steward: true, Hero);

        await Returns.SweepAsync(instance, Now);

        var region = await StoredAsync(instance, "r2");
        Assert.Null(region.Gathering);
        Assert.Equal(0, region.UnrulySinceUtcTicks);
        Assert.Equal("fixed", Assert.Single(await LettersAsync(instance, LetterKind.HordeScattered)).Detail);
    }

    [Fact]
    public async Task AtItsClose_EmptyLandFalls_StillItsHolders_AndItsGarrisonComesHome()
    {
        var region = Gathering(Unruly(since: TimeSpan.FromHours(22)), TimeSpan.FromHours(9));
        var instance = await SeedAsync(Seat(), region);
        // A hero too weak to be its steward: stationed, but the land is still unruly.
        await GarrisonAsync(instance, "r2", 10, steward: false, "weakling");

        _dice.D20 = 15;
        await Returns.SweepAsync(instance, Now);

        var fallen = await StoredAsync(instance, "r2");
        Assert.True(fallen.IsOwnedByPlayer(TestIds.Player));
        Assert.True(HordeRules.IsReturned(fallen) && HordeRules.IsOverrun(fallen));
        Assert.Null(fallen.Gathering);
        Assert.Null(fallen.StewardId);
        Assert.Equal(0, RegionHoldCalculator.GarrisonCountOf(fallen));
        Assert.Equal(0, GoldRules.RateFor(fallen));
        // Struck at the gathering's close, however late the sweep came.
        Assert.Equal(HordeReturnRules.StrikesAt(region).Ticks, fallen.Horde!.FellAtUtcTicks);
        Assert.Equal("1", Assert.Single(await LettersAsync(instance, LetterKind.RegionFell)).Detail);
    }

    [Fact]
    public async Task AtItsClose_AStrongGarrisonHolds_AndTheLandIsSafeADay()
    {
        var region = Gathering(Unruly(since: TimeSpan.FromHours(22)), TimeSpan.FromHours(9));
        var instance = await SeedAsync(Seat(), region);
        // Stationed but too weak a hero to be its steward: still unruly, still the cause, but the walls hold.
        var row = await _db.WorldViewGameData.SingleAsync(w => w.GameInstanceId == instance);
        var world = JsonNode.Parse(row.GameData)!;
        var node = WorldRegionBlob.FindRegion(world, "r2")!;
        WorldRegionBlob.SetGarrison(node, TestWorld.KeepIn(region), new[] { "veteran" }, 20000);
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();

        await Returns.SweepAsync(instance, Now);

        var held = await StoredAsync(instance, "r2");
        Assert.False(HordeRules.IsOverrun(held));
        Assert.Null(held.Gathering);
        Assert.Equal((HordeReturnRules.StrikesAt(region) + HordeReturnRules.SafeFor).Ticks, held.HordeSafeUntilUtcTicks);
        Assert.Equal("held", Assert.Single(await LettersAsync(instance, LetterKind.HordeScattered)).Detail);

        await Returns.SweepAsync(instance, Now.AddHours(2));
        Assert.Null((await StoredAsync(instance, "r2")).Gathering);
    }

    [Fact]
    public async Task FallenLand_NobodyReclaims_LapsesToTheWildAfterThreeDays()
    {
        var fallen = TestWorld.OwnedBy(TestIds.Player, "r2");
        fallen.Hex = new HexCoord(2, 0);
        fallen.StewardNeeded = true;
        fallen.Horde = HordeRules.NewReturnedHorde(2, Now);
        var instance = await SeedAsync(Seat(), fallen);

        await Returns.SweepAsync(instance, Now.AddDays(3).AddMinutes(-1));
        Assert.True((await StoredAsync(instance, "r2")).IsOwnedByPlayer(TestIds.Player));

        await Returns.SweepAsync(instance, Now.AddDays(3));
        var wild = await StoredAsync(instance, "r2");
        Assert.False(wild.IsOwnedByPlayer(TestIds.Player));
        Assert.Equal(LocationOwnership.Neutral, wild.Ownership);
        Assert.True(HordeRules.IsWild(wild) && HordeRules.IsOverrun(wild));
        Assert.False(wild.StewardNeeded);
        Assert.Single(await LettersAsync(instance, LetterKind.RegionLapsed));
    }

    [Fact]
    public async Task LandARiftInvaded_TwelveHoursAgo_GathersAHorde_EvenBesideTheSeat()
    {
        var beside = TestWorld.OwnedBy(TestIds.Player, "r1");
        beside.Hex = new HexCoord(1, 0);
        var instance = await SeedAsync(Seat(), beside);
        _db.Rifts.Add(new Rift
        {
            GameInstanceId = instance, SeasonNumber = 1, Kind = RiftKind.Lesser, EventNumber = 1, RegionId = "r1",
            TargetUserId = TestIds.Player, Stage = RiftStage.Invaded, OmenAt = Now.AddHours(-15), OpensAt = Now.AddHours(-14.5),
            ClosesAt = Now.AddHours(-13), InvadedAt = Now.AddHours(-13), BreachMax = 1,
        });
        await _db.SaveChangesAsync();

        await Returns.SweepAsync(instance, Now);

        Assert.Equal(HordeCause.Invaded, (await StoredAsync(instance, "r1")).Gathering?.Cause);
        Assert.Null((await StoredAsync(instance, "r0")).Gathering);
    }

    [Fact]
    public async Task ReclaimingFallenLand_PaysTheFight_NoChestNoGoldNoWorker()
    {
        var fallen = TestWorld.OwnedBy(TestIds.Player, "r2");
        fallen.Hex = new HexCoord(2, 0);
        fallen.Horde = HordeRules.NewReturnedHorde(2, Now);
        foreach (var pack in HordeRules.Packs(fallen)) fallen.Horde.Broken.Add(pack.Id);
        var instance = await SeedAsync(Seat(), fallen);

        var lair = HordeRules.LairOf(fallen, RegionGenerator.Generate(fallen))!;
        var party = new PlayerParty
        {
            GameInstanceId = instance, UserId = TestIds.Player, Name = "The Vanguard",
            CharacterIdsJson = MarchingArmy.WriteIds(new List<string> { Hero }),
            State = CompanyState.Idle, RegionId = "r2", SiteId = lair.SiteId,
        };
        _db.PlayerParties.Add(party);
        await _db.SaveChangesAsync();

        var (begun, runId, encounter) = await Pve.BeginHordeAsync(instance, TestIds.Player,
            new PveBeginRequest(HordeRules.SiteId(lair.SiteId), SharedContract.Version, null, party.Id));
        Assert.True(begun.Succeeded, begun.Message);
        Assert.True(encounter!.IsLair);

        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(2);
        await _db.SaveChangesAsync();
        var (claimed, response, world) = await Pve.ClaimAsync(instance, TestIds.Player, TestIds.Player, new PveClaimRequest(runId, SharedContract.Version));
        Assert.True(claimed.Succeeded, claimed.Message);

        var win = response!.Horde!;
        Assert.True(win.Reclaimed);
        Assert.Null(win.ChestRarity);
        Assert.Empty(win.Chest);
        Assert.Equal(0, win.ReclaimGold);
        Assert.Null(win.WorkerName);
        var after = TestWorld.ReadRegion(world, "r2");
        Assert.True(after.IsOwnedByPlayer(TestIds.Player));
        Assert.False(HordeRules.IsOverrun(after));
    }
}
