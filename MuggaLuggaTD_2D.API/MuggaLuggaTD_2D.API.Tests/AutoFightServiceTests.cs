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
/// Companies in auto mode (docs/design/auto-fight.md, phase 2). Pinned:
/// <list type="bullet">
/// <item>The day is replayed lazily, and replaying it again changes nothing.</item>
/// <item>It roams without repeating, eats provisions, and stops when they run out.</item>
/// <item>A loss Bloodies the fighters, who rest and are refused every other fight.</item>
/// <item>It goes only where its player holds the land, and its player cannot steer it meanwhile.</item>
/// <item>Experience and gear are banked for the client to collect.</item>
/// </list>
/// </summary>
public class AutoFightServiceTests : IDisposable
{
    private readonly string _store = $"tests-{Guid.NewGuid()}";
    private readonly ApplicationDbContext _db;

    public AutoFightServiceTests() => _db = TestDb.Create(_store);
    private readonly FakeGameContent _content = new();
    private DateTime _now = DateTime.UtcNow;
    private Func<double, string, long, bool> _roll = (_, _, _) => true;

    private GoldService Gold => new(_db, new FakeSessionLog(), NullLogger<GoldService>.Instance);
    private MaterialWalletService Wallet => new(_db, new FakeSessionLog(), NullLogger<MaterialWalletService>.Instance);
    private ItemLedgerService Items => new(_db, new FakeSessionLog(), NullLogger<ItemLedgerService>.Instance);
    private TavernService Tavern => new(_db, _content, Wallet, Gold, new FakeSessionLog(), NullLogger<TavernService>.Instance);

    private AutoFightService Auto => new(_db, _content, Wallet, Gold, Items, new FakeSessionLog(),
        NullLogger<AutoFightService>.Instance) { Clock = () => _now, Roll = _roll, Dice = new Random(3) };

    private PartyService Parties => new(_db, Tavern, _content, Wallet, Gold, new FakeSessionLog(),
        NullLogger<PartyService>.Instance, Items, null, Auto);

    private static string Contract => SharedContract.Version;

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// A realm where the player holds two regions and leads one company of three at
    /// <paramref name="level"/>. Returns the company, the capital's id and the other region's.
    /// </summary>
    private async Task<(Guid Instance, PlayerParty Company, WorldRegionData Home, WorldRegionData Other)> SeedAsync(
        long level = 40, int grain = 100, int hides = 100)
    {
        var instance = await _db.AddInstanceAsync();
        var home = TestWorld.OwnedBy(TestIds.Player, "r1");
        home.IsCapital = true;
        var other = TestWorld.Region("r2", q: 1, r: 0);
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(home, other));

        var save = TestSave.Roster(Enumerable.Range(1, 4).Select(i => TestSave.Character($"hero-{i}", level)).ToArray());
        save.ActiveCharacterIds = new List<string> { "hero-1", "hero-2", "hero-3" };
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, TestSave.ToJson(save));

        if (grain > 0) _db.PlayerMaterials.Add(Good(instance.Id, ResourceNodeRules.Grain, grain));
        if (hides > 0) _db.PlayerMaterials.Add(Good(instance.Id, ResourceNodeRules.Hides, hides));
        await _db.SaveChangesAsync();

        var (_, listed) = await Parties.ListAsync(instance.Id, TestIds.Player);
        var company = await _db.PlayerParties.SingleAsync(p => p.Id == listed!.Parties[0].Id);
        return (instance.Id, company, home, other);
    }

    private static PlayerMaterial Good(Guid instance, string name, int quantity) =>
        new() { GameInstanceId = instance, UserId = TestIds.Player, MaterialName = name, Quantity = quantity };

    private async Task<int> HeldAsync(Guid instance, string good) =>
        (await _db.PlayerMaterials.AsNoTracking().SingleOrDefaultAsync(m =>
            m.GameInstanceId == instance && m.UserId == TestIds.Player && m.MaterialName == good))?.Quantity ?? 0;

    private async Task<PlayerParty> ReloadAsync(Guid id) =>
        await _db.PlayerParties.AsNoTracking().SingleAsync(p => p.Id == id);

    private async Task SendAsync(Guid instance, PlayerParty company, AutoOrder order, string regionId)
    {
        var on = await Auto.SetModeAsync(instance, TestIds.Player, company.Id, new AutoModeRequest(true, Contract));
        Assert.True(on.Succeeded, on.Message);
        var ordered = await Auto.OrderAsync(instance, TestIds.Player, company.Id, new AutoOrderRequest(order, regionId, Contract));
        Assert.True(ordered.Succeeded, ordered.Message);
    }

    private async Task<List<AutoFightReport>> ReportsAsync(Guid instance) =>
        await _db.AutoFightReports.AsNoTracking()
            .Where(r => r.GameInstanceId == instance && r.UserId == TestIds.Player)
            .OrderBy(r => r.At).ToListAsync();

    [Fact]
    public async Task TwoReadsAtOnceReplayTheStretchOnlyOnce()
    {
        var (instance, company, home, _) = await SeedAsync();
        await SendAsync(instance, company, AutoOrder.Roam, home.RegionId);
        _now += TimeSpan.FromHours(1);

        // This request has the company in hand, as of the order...
        await _db.PlayerParties.SingleAsync(p => p.Id == company.Id);

        // ...when another request (the Hall reads companies and reports together) settles it first.
        using (var other = TestDb.Create(_store))
        {
            var first = new AutoFightService(other, _content,
                new MaterialWalletService(other, new FakeSessionLog(), NullLogger<MaterialWalletService>.Instance),
                new GoldService(other, new FakeSessionLog(), NullLogger<GoldService>.Instance),
                new ItemLedgerService(other, new FakeSessionLog(), NullLogger<ItemLedgerService>.Instance),
                new FakeSessionLog(), NullLogger<AutoFightService>.Instance) { Clock = () => _now, Roll = _roll, Dice = new Random(3) };
            await first.SettleAsync(instance, TestIds.Player);
        }
        int once = (await ReportsAsync(instance)).Count;
        int grain = await HeldAsync(instance, ResourceNodeRules.Grain);
        Assert.True(once > 0);

        await Auto.SettleAsync(instance, TestIds.Player);

        Assert.Equal(once, (await ReportsAsync(instance)).Count);
        Assert.Equal(grain, await HeldAsync(instance, ResourceNodeRules.Grain));
    }

    [Fact]
    public async Task ACompanyInAutoModeWaitsForAnOrder()
    {
        var (instance, company, _, _) = await SeedAsync();
        var on = await Auto.SetModeAsync(instance, TestIds.Player, company.Id, new AutoModeRequest(true, Contract));
        Assert.True(on.Succeeded);

        _now += TimeSpan.FromHours(2);
        await Auto.SettleAsync(instance, TestIds.Player);

        var after = await ReloadAsync(company.Id);
        Assert.True(after.AutoMode);
        Assert.Equal(AutoStatus.Ready, after.AutoStatus);
        Assert.Empty(await ReportsAsync(instance));
    }

    [Fact]
    public async Task ItRoamsItsRegionWithoutRepeatingAndEatsGrainForEveryFight()
    {
        var (instance, company, home, _) = await SeedAsync();
        await SendAsync(instance, company, AutoOrder.Roam, home.RegionId);

        _now += TimeSpan.FromHours(3);
        await Auto.SettleAsync(instance, TestIds.Player);

        var reports = await ReportsAsync(instance);
        Assert.True(reports.Count >= 5, $"three hours of roaming should be several fights, was {reports.Count}");
        for (int i = 1; i < reports.Count; i++)
            Assert.NotEqual(reports[i - 1].SiteId, reports[i].SiteId);

        // Every fight began with its Grain paid, including the one still in progress.
        var after = await ReloadAsync(company.Id);
        int started = reports.Count + (after.AutoStatus is AutoStatus.Fighting or AutoStatus.Walking ? 1 : 0);
        Assert.Equal(100 - started * ProvisionRules.GrainPerFight, await HeldAsync(instance, ResourceNodeRules.Grain));
        Assert.All(reports, r => Assert.True(r.Won));
        Assert.All(reports, r => Assert.True(SiteSpec.RegionIdOf(r.SiteId) == home.RegionId));
    }

    [Fact]
    public async Task SettlingTheSameDayTwiceChangesNothing()
    {
        var (instance, company, home, _) = await SeedAsync();
        await SendAsync(instance, company, AutoOrder.Roam, home.RegionId);

        _now += TimeSpan.FromHours(1);
        await Auto.SettleAsync(instance, TestIds.Player);
        var once = await ReportsAsync(instance);
        int grain = await HeldAsync(instance, ResourceNodeRules.Grain);

        await Auto.SettleAsync(instance, TestIds.Player);
        Assert.Equal(once.Count, (await ReportsAsync(instance)).Count);
        Assert.Equal(grain, await HeldAsync(instance, ResourceNodeRules.Grain));
    }

    [Fact]
    public async Task AWinPaysGoldIntoThePurseAndBanksTheExperienceForTheClient()
    {
        var (instance, company, home, _) = await SeedAsync();
        await SendAsync(instance, company, AutoOrder.Roam, home.RegionId);
        _now += TimeSpan.FromHours(1);

        var card = await Auto.ReportsAsync(instance, TestIds.Player);
        Assert.NotEmpty(card.Reports);
        Assert.All(card.Reports, r => Assert.True(r.Experience > 0));
        Assert.All(card.Reports, r => Assert.Equal(new[] { "hero-1", "hero-2", "hero-3" }, r.FighterIds));
        Assert.True(await _db.PlayerGold.AnyAsync(g => g.GameInstanceId == instance && g.LifetimeFromClears > 0));

        int collected = await Auto.CollectAsync(instance, TestIds.Player, new AutoCollectRequest(card.Reports.Select(r => r.Id).ToList()));
        Assert.Equal(card.Reports.Count, collected);
        var left = (await Auto.ReportsAsync(instance, TestIds.Player)).Reports;
        Assert.DoesNotContain(left, r => card.Reports.Any(c => c.Id == r.Id));
    }

    [Fact]
    public async Task ItStopsWhenTheGrainRunsOutAndWaitsForItsPlayer()
    {
        var (instance, company, home, _) = await SeedAsync(grain: 4);
        await SendAsync(instance, company, AutoOrder.Roam, home.RegionId);

        _now += TimeSpan.FromHours(3);
        await Auto.SettleAsync(instance, TestIds.Player);

        Assert.Equal(2, (await ReportsAsync(instance)).Count);
        Assert.Equal(0, await HeldAsync(instance, ResourceNodeRules.Grain));
        Assert.Equal(AutoStatus.OutOfProvisions, (await ReloadAsync(company.Id)).AutoStatus);

        // Goods arriving do not restart it on their own; its player's word does.
        var row = await _db.PlayerMaterials.SingleAsync(m => m.GameInstanceId == instance && m.MaterialName == ResourceNodeRules.Grain);
        row.Quantity = 50;
        await _db.SaveChangesAsync();
        _now += TimeSpan.FromHours(1);
        await Auto.SettleAsync(instance, TestIds.Player);
        Assert.Equal(2, (await ReportsAsync(instance)).Count);
    }

    [Fact]
    public async Task ALossBloodiesItsFightersWhoRestHalfAnHourBeforeFightingAgain()
    {
        var (instance, company, home, _) = await SeedAsync();
        long fights = 0;
        _roll = (_, _, _) => fights++ != 0;   // the first fight is lost, the rest won
        await SendAsync(instance, company, AutoOrder.Roam, home.RegionId);

        _now += TimeSpan.FromHours(2);
        await Auto.SettleAsync(instance, TestIds.Player);

        var reports = await ReportsAsync(instance);
        Assert.False(reports[0].Won);
        Assert.True(reports[1].At - reports[0].At >= BloodiedRules.Recovery,
            "after a loss the company rests until its fighters recover");

        var wounds = await _db.BloodiedCharacters.AsNoTracking().Where(b => b.GameInstanceId == instance).ToListAsync();
        Assert.Equal(3, wounds.Count);
        Assert.All(wounds, w => Assert.Equal(BloodiedRules.RecoversAt(reports[0].At), w.RecoversAt));
    }

    [Fact]
    public async Task ABloodiedCharacterIsRefusedEveryFight()
    {
        var (instance, _, _, _) = await SeedAsync();
        _db.BloodiedCharacters.Add(new BloodiedCharacter
        {
            GameInstanceId = instance, UserId = TestIds.Player, CharacterId = "hero-4",
            RecoversAt = DateTime.UtcNow.AddMinutes(12),
        });
        await _db.SaveChangesAsync();
        var world = TestWorld.RoundTrip(TestWorld.Blob(TestWorld.OwnedBy(TestIds.Player, "r1")));

        var why = await PartyService.WhyCannotFightAsync(_db, NullLogger.Instance, world, instance, TestIds.Player,
            new[] { "hero-1", "hero-4" });
        Assert.NotNull(why);
        Assert.Contains("Bloodied", why);
        Assert.Contains("12 min", why);

        var muster = await MarchingArmy.MusterAsync(_db, _content, NullLogger.Instance, instance, TestIds.Player, world,
            new[] { "hero-1", "hero-4" });
        Assert.Equal(new[] { "hero-1" }, muster.CharacterIds);
    }

    [Fact]
    public async Task ItGoesOnlyWhereItsPlayerHoldsTheLand()
    {
        var (instance, company, _, other) = await SeedAsync();
        await Auto.SetModeAsync(instance, TestIds.Player, company.Id, new AutoModeRequest(true, Contract));

        var refused = await Auto.OrderAsync(instance, TestIds.Player, company.Id, new AutoOrderRequest(AutoOrder.Roam, other.RegionId, Contract));
        Assert.Equal(AutoError.RegionNotHeld, refused.Error);
    }

    [Fact]
    public async Task ItPatrolsOnlyLandBelowItsLevel()
    {
        var (instance, company, home, _) = await SeedAsync(level: 1);
        await Auto.SetModeAsync(instance, TestIds.Player, company.Id, new AutoModeRequest(true, Contract));

        var refused = await Auto.OrderAsync(instance, TestIds.Player, company.Id, new AutoOrderRequest(AutoOrder.Patrol, home.RegionId, Contract));
        Assert.Equal(AutoError.TooStrong, refused.Error);
    }

    [Fact]
    public async Task AWeakCompanyFindsNothingToFight()
    {
        var (instance, company, home, _) = await SeedAsync(level: 1);
        await SendAsync(instance, company, AutoOrder.Roam, home.RegionId);

        _now += TimeSpan.FromHours(1);
        await Auto.SettleAsync(instance, TestIds.Player);

        Assert.Empty(await ReportsAsync(instance));
        Assert.Equal(AutoStatus.NothingToFight, (await ReloadAsync(company.Id)).AutoStatus);
        Assert.Equal(100, await HeldAsync(instance, ResourceNodeRules.Grain));
    }

    [Fact]
    public async Task APatrolMeetsASkirmishEveryTwentyMinutesAndEatsAGrainForEach()
    {
        var (instance, company, home, _) = await SeedAsync();
        await SendAsync(instance, company, AutoOrder.Patrol, home.RegionId);

        _now += TimeSpan.FromHours(2);
        await Auto.SettleAsync(instance, TestIds.Player);

        var reports = await ReportsAsync(instance);
        Assert.Equal(6, reports.Count);
        Assert.All(reports, r => Assert.True(r.Skirmish));
        // Six stints settled, and the seventh begun and paid for.
        Assert.Equal(100 - 7, await HeldAsync(instance, ResourceNodeRules.Grain));
        Assert.Equal(AutoStatus.Patrolling, (await ReloadAsync(company.Id)).AutoStatus);
    }

    [Fact]
    public async Task ItsPlayerCannotSteerItUntilItIsTakenOutOfAutoMode()
    {
        var (instance, company, home, _) = await SeedAsync();
        await SendAsync(instance, company, AutoOrder.Roam, home.RegionId);
        var dungeon = TestWorld.DungeonIn(home);

        var (travel, _) = await Parties.TravelAsync(instance, TestIds.Player, company.Id, new PartyTravelRequest(dungeon, Contract));
        Assert.Equal(PartyError.Busy, travel.Error);
        var (man, _) = await Parties.UpdateAsync(instance, TestIds.Player, company.Id, new PartyUpdateRequest(null, null, new List<string> { "hero-4" }, Contract));
        Assert.Equal(PartyError.Busy, man.Error);

        var off = await Auto.SetModeAsync(instance, TestIds.Player, company.Id, new AutoModeRequest(false, Contract));
        Assert.True(off.Succeeded);
        var after = await ReloadAsync(company.Id);
        Assert.False(after.AutoMode);
        Assert.NotEqual(CompanyState.InRun, after.State);
    }

    [Fact]
    public async Task TheCompanyCardShowsWhatItIsDoing()
    {
        var (instance, company, home, _) = await SeedAsync();
        await SendAsync(instance, company, AutoOrder.Roam, home.RegionId);

        var (_, listed) = await Parties.ListAsync(instance, TestIds.Player);
        var card = listed!.Parties.Single(p => p.Id == company.Id);
        Assert.NotNull(card.Auto);
        Assert.Equal(AutoOrder.Roam, card.Auto!.Order);
        Assert.Equal(home.RegionId, card.Auto.RegionId);
        Assert.Contains(card.Auto.Status, new[] { AutoStatus.Walking, AutoStatus.Fighting });
        Assert.NotNull(card.Auto.StepEndsAt);
        Assert.NotNull(listed.Bloodied);
    }

    [Fact]
    public async Task ALongAbsenceIsReplayedNoFurtherThanTwoDays()
    {
        var (instance, company, home, _) = await SeedAsync(grain: 100_000, hides: 100_000);
        await SendAsync(instance, company, AutoOrder.Roam, home.RegionId);

        _now += TimeSpan.FromDays(5);
        await Auto.SettleAsync(instance, TestIds.Player);

        var reports = await ReportsAsync(instance);
        Assert.True(reports[0].At >= _now - AutoFightRules.MaximumReplay - TimeSpan.FromMinutes(10));
    }

    /// <summary>Rolls just under a held tier-2 road's lowest chance (0.12) and above any patrolled one.</summary>
    private sealed class FixedDice : Random
    {
        public override double NextDouble() => 0.119;
        protected override double Sample() => 0.119;
    }

    /// <summary>Sends a second company (hero-4) from the keep to a dungeon; returns whether its road was ambushed.</summary>
    private async Task<bool> SecondCompanyIsAmbushedAsync(Guid instance, WorldRegionData home)
    {
        var parties = new PartyService(_db, Tavern, _content, Wallet, Gold, new FakeSessionLog(),
            NullLogger<PartyService>.Instance, Items, null, Auto) { Dice = new FixedDice() };
        var (formed, listed) = await parties.CreateAsync(instance, TestIds.Player,
            new PartyCreateRequest(null, null, new List<string> { "hero-4" }, Contract));
        Assert.True(formed.Succeeded, formed.Message);
        var second = listed!.Parties.Last();

        var (sent, _) = await parties.TravelAsync(instance, TestIds.Player, second.Id,
            new PartyTravelRequest(TestWorld.DungeonIn(home), Contract));
        Assert.True(sent.Succeeded, sent.Message);
        return (await ReloadAsync(second.Id)).AmbushAt != null;
    }

    [Fact]
    public async Task WithoutAPatrolThatRoadIsAmbushed()
    {
        var (instance, _, home, _) = await SeedAsync();
        Assert.True(await SecondCompanyIsAmbushedAsync(instance, home));
    }

    [Fact]
    public async Task APatrolMakesItsRegionsRoadsSafer()
    {
        var (instance, company, home, _) = await SeedAsync();
        await SendAsync(instance, company, AutoOrder.Patrol, home.RegionId);
        Assert.False(await SecondCompanyIsAmbushedAsync(instance, home));
    }

    [Fact]
    public async Task ASeasonResetTakesEveryCompanyOutOfAutoModeAndHealsTheWounded()
    {
        var (instance, company, home, _) = await SeedAsync();
        await SendAsync(instance, company, AutoOrder.Roam, home.RegionId);
        _db.BloodiedCharacters.Add(new BloodiedCharacter
        {
            GameInstanceId = instance, UserId = TestIds.Player, CharacterId = "hero-4", RecoversAt = DateTime.UtcNow.AddMinutes(20),
        });
        await _db.SaveChangesAsync();

        await AutoFightService.ResetRealmAsync(_db, instance);
        await _db.SaveChangesAsync();

        Assert.False((await ReloadAsync(company.Id)).AutoMode);
        Assert.Empty(_db.BloodiedCharacters.Where(b => b.GameInstanceId == instance));
    }
}
