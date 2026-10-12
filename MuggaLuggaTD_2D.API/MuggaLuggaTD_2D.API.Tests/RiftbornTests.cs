using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The Riftborn faction (Invasions spec R8, phase 6). What a player should be able to rely on: a Great Rift nobody seals
/// hands its region to the Riftborn, wild land at once and held land by a siege sized by the Breach left; eruptions hand
/// them land six times a season and then make them stronger instead; they spread into wild land of any tier; they are
/// not scored; no Great Rift opens in a season's last day; and the factions beside a rift wear it down without ever
/// sealing it.
/// </summary>
public class RiftbornTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private readonly FakeHubContext _hub = new();
    private readonly FakeGameContent _content = new();
    private readonly RealmPresence _presence = new();
    private DateTime _now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private Guid _realm;

    private sealed class FixedRandom : Random
    {
        public double Roll { get; set; } = 0.99;
        public override double NextDouble() => Roll;
        public override int Next(int minValue, int maxValue) => maxValue == 21 ? 20 : minValue;
        public override int Next() => 0;
    }

    private readonly FixedRandom _dice = new();

    public RiftbornTests() => _presence.Clock = () => _now;

    public void Dispose() => _db.Dispose();

    private (RiftService Rifts, FactionService Factions) Services()
    {
        var clock = new FakeClock(_now);
        var letters = new LetterService(_db, _hub, NullLogger<LetterService>.Instance, clock);
        var warLog = new WarLogService(_db, _hub, NullLogger<WarLogService>.Instance, clock, letters);
        var items = new ItemLedgerService(_db, _log, NullLogger<ItemLedgerService>.Instance);
        var factions = new FactionService(_db, _log, _content, warLog, null, _hub, _dice);
        var rifts = new RiftService(_db, _content, items, _log, NullLogger<RiftService>.Instance, _hub, _presence, warLog, letters,
            factions: factions) { Clock = () => _now };
        return (rifts, factions);
    }

    private static WorldRegionData Wild(string id, int q, int r, int tier = 1)
    {
        var region = TestWorld.Region(id, q, r, tier: tier);
        region.Entrenchment = 0;
        return region;
    }

    private static WorldRegionData Held(FactionId faction, string id, int q, int r, int tier = 3)
    {
        var region = TestWorld.Region(id, q, r, LocationOwnership.Enemy, tier: tier);
        region.Faction = faction;
        region.OwnerDisplayName = FactionCatalog.TitleOf(faction);
        return region;
    }

    /// <summary>
    /// The player's capital at the centre and land in ring 2 (r2), the rival's land beside the wild r3, wild tier-1 land
    /// round it (r3, r6), and an Ashkin hold next to r3.
    /// </summary>
    private async Task RealmAsync(params WorldRegionData[] extra)
    {
        var instance = await _db.AddInstanceAsync();
        instance.SeasonStartedAt = _now.AddDays(-1);
        instance.SeasonLengthDays = 30;
        _realm = instance.Id;

        var capital = TestWorld.OwnedBy(TestIds.Player, "r0");
        capital.IsCapital = true;
        var regions = new List<WorldRegionData>
        {
            capital,
            TestWorld.Region("r2", 2, 0, LocationOwnership.Player, TestIds.Player),
            Wild("r3", -2, 0),
            TestWorld.Region("r4", -1, 0, LocationOwnership.Player, TestIds.Rival),
            Wild("r6", -3, 0),
        };
        regions.AddRange(extra);
        await _db.AddWorldAsync(_realm, TestWorld.Blob(regions.ToArray()));

        foreach (var user in new[] { TestIds.Player, TestIds.Rival })
        {
            _db.Users.Add(new ApplicationUser { Id = user, UserName = user, DisplayName = user });
            _db.PlayerGameData.Add(new PlayerGameData { GameInstanceId = _realm, UserId = user, UpdatedAt = _now.AddHours(-1) });
        }
        await _db.SaveChangesAsync();
    }

    private async Task<Rift> EruptAsync(string regionId, double breachLeft = 1.0)
    {
        var (rifts, _) = Services();
        var (rift, error) = await rifts.DebugOpenAsync(_realm, TestIds.Player, RiftKind.Great, regionId);
        Assert.Null(error);
        if (breachLeft < 1.0) await rifts.DebugSetBreachAsync(rift!, breachLeft);
        await rifts.DebugTimeOutAsync(rift!);
        return await _db.Rifts.AsNoTracking().SingleAsync(r => r.Id == rift!.Id);
    }

    private async Task<WorldRegionData> RegionAsync(string id) => TestWorld.ReadRegion(await _db.ReadWorldAsync(_realm), id);

    private async Task<List<WorldRegionData>> RegionsAsync() => WorldRegionBlob.ReadAllRegions((await _db.ReadWorldAsync(_realm))!);

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void TheRiftborn_AreAFaction_NoWorldIsSeatedWith_AndNotScored()
    {
        var riftborn = FactionCatalog.Of(FactionId.Riftborn);
        Assert.NotNull(riftborn);
        Assert.False(riftborn!.Seatable);
        Assert.False(FactionCatalog.IsScored(FactionId.Riftborn));
        Assert.True(FactionCatalog.IsScored(FactionId.Grimjaw));
        Assert.DoesNotContain(FactionId.Riftborn, FactionCatalog.DefaultCast);
        Assert.Equal("the Riftborn", riftborn.TitleInSentence);

        // In play once they hold land, not before.
        var none = new List<WorldRegionData> { Wild("a", 0, 0) };
        Assert.DoesNotContain(FactionId.Riftborn, FactionCatalog.InPlay(null, none));
        Assert.Contains(FactionId.Riftborn, FactionCatalog.InPlay(null, new List<WorldRegionData> { Held(FactionId.Riftborn, "a", 0, 0) }));

        // A world generated today still seats only the pair.
        var world = WorldMapGenerator.Generate(777, new List<WorldMapGenerator.PlayerSeat>());
        Assert.DoesNotContain(world, r => r.Faction == FactionId.Riftborn);
    }

    [Fact]
    public void AnEruption_SeizesWildLand_BesiegesHeldLand_AndSurgesPastTheCap()
    {
        var wild = Wild("w", 3, 0);
        var mine = TestWorld.Region("p", 3, 0, LocationOwnership.Player, TestIds.Player);
        var theirs = Held(FactionId.Ashkin, "f", 3, 0);
        var riftborn = Held(FactionId.Riftborn, "rb", 3, 0);

        Assert.Equal(RiftEruption.Seized, RiftRules.EruptionFor(wild, 0, besiegeable: true));
        Assert.Equal(RiftEruption.Besieged, RiftRules.EruptionFor(mine, 0, besiegeable: true));
        Assert.Equal(RiftEruption.Besieged, RiftRules.EruptionFor(theirs, 5, besiegeable: true));
        Assert.Equal(RiftEruption.Surged, RiftRules.EruptionFor(mine, 0, besiegeable: false));
        Assert.Equal(RiftEruption.Surged, RiftRules.EruptionFor(riftborn, 0, besiegeable: true));
        Assert.Equal(RiftEruption.Surged, RiftRules.EruptionFor(wild, RiftRules.EruptionLandCap, besiegeable: true));
        Assert.True(RiftRules.HandsLand(RiftEruption.Besieged));
        Assert.False(RiftRules.HandsLand(RiftEruption.Surged));
    }

    [Fact]
    public void TheEruptionsArmy_IsHalfToOneAndAHalfTimesANormalSiege_ByTheBreachLeft()
    {
        Assert.Equal(0.5, RiftRules.EruptionSiegeFactor(0, 1200), 6);
        Assert.Equal(1.0, RiftRules.EruptionSiegeFactor(600, 1200), 6);
        Assert.Equal(1.5, RiftRules.EruptionSiegeFactor(1200, 1200), 6);
        Assert.Equal(3000, RiftRules.EruptionMarch(2000, 1200, 1200), 6);

        // An untouched rift clears the gate of what it besieges; a nearly sealed one is turned away at the walls.
        Assert.True(RegionHoldCalculator.ClearsGate(RiftRules.EruptionMarch(2000, 1200, 1200), 2000));
        Assert.False(RegionHoldCalculator.ClearsGate(RiftRules.EruptionMarch(2000, 1, 1200), 2000));
    }

    [Fact]
    public void NoGreatRift_OpensInASeasonsLastDay_ButALesserRiftStillMay()
    {
        var start = _now.AddDays(-10);
        var end = _now.AddDays(1);
        Assert.False(RiftRules.InSeason(RiftKind.Great, end.AddHours(-23), start, end));
        Assert.True(RiftRules.InSeason(RiftKind.Great, end.AddHours(-25), start, end));
        Assert.True(RiftRules.InSeason(RiftKind.Lesser, end.AddHours(-23), start, end));
        Assert.False(RiftRules.InSeason(RiftKind.Lesser, end.AddHours(-5), start, end));
    }

    [Fact]
    public void AGreatRift_NeverOpensOnRiftbornLand()
    {
        var capital = TestWorld.OwnedBy(TestIds.Player, "c");
        capital.IsCapital = true;
        var regions = new List<WorldRegionData> { capital, Held(FactionId.Riftborn, "rb", 3, 0), Wild("w", -3, 0) };
        Assert.DoesNotContain(RiftRules.GreatCandidates(regions), c => c.Region.RegionId == "rb");
        Assert.Contains(RiftRules.GreatCandidates(regions), c => c.Region.RegionId == "w");
    }

    [Fact]
    public void TheRiftborn_SpreadIntoWildLandOfAnyTier_TheOthersOnlyHardCountry()
    {
        var regions = new List<WorldRegionData> { Held(FactionId.Riftborn, "rb", 0, 0), Wild("easy", 1, 0, tier: 1) };
        Assert.Equal("easy", FactionGrowthRules.PickExpansion(FactionId.Riftborn, regions, 0)?.RegionId);

        var grimjaw = new List<WorldRegionData> { Held(FactionId.Grimjaw, "g", 0, 0), Wild("easy", 1, 0, tier: 1) };
        Assert.Null(FactionGrowthRules.PickExpansion(FactionId.Grimjaw, grimjaw, 0));
    }

    [Fact]
    public void AFaction_ChipsTheRift_OnlyBesideIt_AndNeverSealsIt()
    {
        var rift = Wild("rift", 0, 0);
        var regions = new List<WorldRegionData> { rift, Held(FactionId.Ashkin, "beside", 1, 0), Held(FactionId.Grimjaw, "far", 3, 0),
            Held(FactionId.Riftborn, "rb", 0, 1) };

        Assert.True(RiftRules.Chips(FactionId.Ashkin, rift, regions));
        Assert.False(RiftRules.Chips(FactionId.Grimjaw, rift, regions));
        Assert.False(RiftRules.Chips(FactionId.Riftborn, rift, regions));
        Assert.Equal(24, RiftRules.FactionChip(1200));
        Assert.Equal(1176, RiftRules.Chipped(1200, 24));
        Assert.Equal(1, RiftRules.Chipped(10, 24));
    }

    [Fact]
    public void ASurge_RaisesTheCapByAQuarter()
    {
        var regions = new List<WorldRegionData> { Held(FactionId.Riftborn, "rb", 0, 0) };
        double land = FactionStrengthRules.Cap(FactionId.Riftborn, regions);
        Assert.True(land > 0);
        Assert.Equal(Math.Round(land * 1.25), FactionStrengthRules.Cap(FactionId.Riftborn, regions, 1));
        Assert.Equal(Math.Round(land * 1.5), FactionStrengthRules.Cap(FactionId.Riftborn, regions, 2));
    }

    // -----------------------------------------------------------------
    // The server
    // -----------------------------------------------------------------

    [Fact]
    public async Task AnEruptionOnWildLand_SeatsTheRiftborn_AndTellsTheNeighbours()
    {
        await RealmAsync();
        var rift = await EruptAsync("r3");

        Assert.Equal(RiftStage.Erupted, rift.Stage);
        Assert.Equal(RiftEruption.Seized, rift.Eruption);

        var region = await RegionAsync("r3");
        Assert.Equal(FactionId.Riftborn, region.Faction);
        Assert.Equal(LocationOwnership.Enemy, region.Ownership);
        Assert.Equal("The Riftborn", region.OwnerDisplayName);

        // They come through in force: the land's strength at once, and they stand on the Powers panel.
        var row = await _db.FactionStates.AsNoTracking().SingleAsync(f => f.GameInstanceId == _realm && f.Faction == FactionId.Riftborn);
        Assert.Equal(FactionStrengthRules.CapOf(region), row.Strength, 3);
        var powers = await Services().Factions.ReadAsync(_realm, _now);
        Assert.Contains(powers!.Factions, f => f.Faction == "Riftborn" && f.RegionsHeld == 1);

        var erupted = await _db.WarLog.AsNoTracking().SingleAsync(w => w.GameInstanceId == _realm && w.Kind == "RiftErupted");
        Assert.Equal("Seized", erupted.Detail);

        // The rival's land borders it; the player's does not.
        Assert.Contains(await _db.Letters.AsNoTracking().Where(l => l.UserId == TestIds.Rival).ToListAsync(),
            l => l.Kind == nameof(LetterKind.RiftErupted) && l.Detail == "Seized");
        Assert.DoesNotContain(await _db.Letters.AsNoTracking().Where(l => l.UserId == TestIds.Player).ToListAsync(),
            l => l.Kind == nameof(LetterKind.RiftErupted));
    }

    [Fact]
    public async Task AnEruptionOnAPlayersLand_LaysASiege_SizedByTheBreachLeft_ThatSettlesLikeAnyOther()
    {
        await RealmAsync();
        var before = await RegionsAsync();
        long hold = RegionHoldCalculator.AssessRegion(before.Single(r => r.RegionId == "r2"), before, 0).Hold;

        var rift = await EruptAsync("r2", breachLeft: 0.5);
        Assert.Equal(RiftEruption.Besieged, rift.Eruption);

        // Still the player's until the muster closes.
        Assert.True((await RegionAsync("r2")).IsOwnedByPlayer(TestIds.Player));
        var siege = await _db.FactionSieges.AsNoTracking().SingleAsync(s => s.GameInstanceId == _realm);
        Assert.Equal(FactionId.Riftborn, siege.Faction);
        Assert.Equal(TestIds.Player, siege.DefenderUserId);
        Assert.Equal(SiegeState.Mustering, siege.State);
        Assert.Equal(Math.Round(hold * RiftRules.EruptionSiegeFactor(rift.Breach, rift.BreachMax)), siege.March);
        Assert.Contains(await _db.Letters.AsNoTracking().Where(l => l.UserId == TestIds.Player).ToListAsync(),
            l => l.Kind == nameof(LetterKind.SiegeDeclaredOnYou));

        // The muster closes and the server settles it, though the Riftborn hold no land yet.
        var lines = await Services().Factions.SettleDueSiegesAsync(_realm, siege.MusterEndsAt.AddMinutes(1));
        Assert.Single(lines);
        Assert.NotEqual(SiegeState.Mustering, (await _db.FactionSieges.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task OnceEruptionsHaveHandedThemSixRegions_TheNextMakesThemStronger()
    {
        await RealmAsync(Held(FactionId.Riftborn, "rb", -4, 0));
        int season = (await _db.GameInstances.AsNoTracking().SingleAsync(g => g.Id == _realm)).SeasonNumber;
        for (int n = 0; n < RiftRules.EruptionLandCap; n++)
        {
            _db.Rifts.Add(new Rift
            {
                GameInstanceId = _realm, SeasonNumber = season, Kind = RiftKind.Great, EventNumber = 100 + n, RegionId = "elsewhere",
                Stage = RiftStage.Erupted, Eruption = n % 2 == 0 ? RiftEruption.Seized : RiftEruption.Besieged,
                OmenAt = _now.AddDays(-1), OpensAt = _now.AddDays(-1), ClosesAt = _now.AddDays(-1), BreachMax = 1200, Breach = 1200,
            });
        }
        await _db.SaveChangesAsync();
        var land = FactionStrengthRules.Cap(FactionId.Riftborn, await RegionsAsync());

        var rift = await EruptAsync("r3");

        Assert.Equal(RiftEruption.Surged, rift.Eruption);
        Assert.Equal(FactionId.None, (await RegionAsync("r3")).Faction);
        var row = await _db.FactionStates.AsNoTracking().SingleAsync(f => f.GameInstanceId == _realm && f.Faction == FactionId.Riftborn);
        Assert.Equal(1, row.Surges);
        var powers = await Services().Factions.ReadAsync(_realm, _now);
        Assert.Equal((long)Math.Round(land * 1.25), powers!.Factions.Single(f => f.Faction == "Riftborn").Cap);
    }

    [Fact]
    public async Task TheRiftborn_ArentScored_InTheSeasonStandings()
    {
        await RealmAsync(Held(FactionId.Riftborn, "rb", -4, 0), Held(FactionId.Grimjaw, "g", 4, 0));
        var seasons = new SeasonScoreService(_db, new GoldService(_db, _log, NullLogger<GoldService>.Instance),
            new WorldProvisioningService(_db, NullLogger<WorldProvisioningService>.Instance), _hub, _log,
            NullLogger<SeasonScoreService>.Instance);
        await seasons.SettleAllAsync(_realm, await _db.ReadWorldAsync(_realm), _now);

        var scored = await _db.FactionSeasonScores.AsNoTracking().Where(f => f.GameInstanceId == _realm).Select(f => f.Faction).ToListAsync();
        Assert.Contains(FactionId.Grimjaw, scored);
        Assert.DoesNotContain(FactionId.Riftborn, scored);
    }

    [Fact]
    public async Task AFactionBesideAnOpenGreatRift_WearsItDown_ButNeverSealsIt()
    {
        await RealmAsync(Held(FactionId.Ashkin, "ash", -2, 1));
        var (rifts, _) = Services();
        var (rift, _) = await rifts.DebugOpenAsync(_realm, TestIds.Player, RiftKind.Great, "r3");
        int max = rift!.BreachMax;

        _dice.Roll = 0;   // every roll lands
        await Services().Factions.ActAsync(_realm, _now);
        var after = await _db.Rifts.AsNoTracking().SingleAsync(r => r.Id == rift.Id);
        Assert.Equal(max - RiftRules.FactionChip(max), after.Breach);
        Assert.Contains(_hub.Sent, s => s.Method == "RiftUpdated");

        var tracked = await _db.Rifts.SingleAsync(r => r.Id == rift.Id);
        tracked.Breach = 1;
        await _db.SaveChangesAsync();
        await Services().Factions.ActAsync(_realm, _now.AddMinutes(15));
        var last = await _db.Rifts.AsNoTracking().SingleAsync(r => r.Id == rift.Id);
        Assert.Equal(1, last.Breach);
        Assert.Equal(RiftStage.Open, last.Stage);
    }
}
