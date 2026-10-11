using Enums;
using Items.Models;
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
/// Rifts, phase 3: fighting one (Invasions spec §4, §5.3, R3). A rift site is entered by a company at
/// rest in the rift's region, while the rift is open; a Lesser Rift by its region's owner alone. A win
/// pays a run ×1.25 and wears the Breach down; one win seals a Lesser Rift. Each site locks its winner
/// out for 20 minutes.
/// </summary>
public class RiftFightTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private readonly FakeHubContext _hub = new();
    private readonly FakeGameContent _content = new()
    {
        DroppableItems = new[] { new ItemTemplate { ItemName = "Cinder Blade", ItemType = ItemTypes.Weapon } }
    };
    private Guid _realm;
    private const string Hero = "hero";

    public void Dispose() => _db.Dispose();

    private ItemLedgerService Items => new(_db, _log, NullLogger<ItemLedgerService>.Instance);
    private GoldService Gold => new(_db, _log, NullLogger<GoldService>.Instance);
    private MaterialWalletService Wallet => new(_db, _log, NullLogger<MaterialWalletService>.Instance);

    private RiftService Rifts()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        var letters = new LetterService(_db, _hub, NullLogger<LetterService>.Instance, clock);
        var warLog = new WarLogService(_db, _hub, NullLogger<WarLogService>.Instance, clock, letters);
        return new RiftService(_db, _content, Items, _log, NullLogger<RiftService>.Instance, _hub, new RealmPresence(), warLog, letters);
    }

    private WorldPveService Pve => new(_db, _content, Wallet, Gold,
        new TavernService(_db, _content, Wallet, Gold, _log, NullLogger<TavernService>.Instance),
        NullLogger<WorldPveService>.Instance, Items, rifts: Rifts());

    /// <summary>The player holds r1 (tier 2, Forest); r2 is wild; the rival holds r3.</summary>
    private async Task RealmAsync()
    {
        var instance = await _db.AddInstanceAsync();
        _realm = instance.Id;
        await _db.AddWorldAsync(_realm, TestWorld.Blob(
            TestWorld.Region("r1", 0, 0, LocationOwnership.Player, TestIds.Player, tier: 2, biome: BiomeType.Forest),
            TestWorld.Region("r2", 1, 0, tier: 4),
            TestWorld.Region("r3", 2, 0, LocationOwnership.Player, TestIds.Rival)));
        foreach (var user in new[] { TestIds.Player, TestIds.Rival })
        {
            _db.Users.Add(new ApplicationUser { Id = user, UserName = user, DisplayName = user });
            await _db.AddPlayerSaveAsync(_realm, user, TestSave.ToJson(TestSave.Roster(TestSave.Character(Hero))));
        }
        await _db.SaveChangesAsync();
    }

    private async Task<Rift> RiftAsync(RiftKind kind, string regionId, RiftStage stage = RiftStage.Open, int breach = 1000)
    {
        var now = DateTime.UtcNow;
        var rift = new Rift
        {
            GameInstanceId = _realm, SeasonNumber = 1, Kind = kind, EventNumber = 1, RegionId = regionId,
            TargetUserId = kind == RiftKind.Lesser ? TestIds.Player : null, Stage = stage,
            OmenAt = now.AddMinutes(-30), OpensAt = now.AddMinutes(-10), ClosesAt = now.AddHours(1),
            BreachMax = kind == RiftKind.Lesser ? 1 : breach, Breach = kind == RiftKind.Lesser ? 1 : breach,
        };
        _db.Rifts.Add(rift);
        await _db.SaveChangesAsync();
        return rift;
    }

    /// <summary>Stands the user's company at rest at a site, and asks to fight <paramref name="siteId"/> with it.</summary>
    private async Task<PveBeginRequest> CompanyAtAsync(string standsAt, string siteId, string userId = TestIds.Player)
    {
        var party = await _db.PlayerParties.FirstOrDefaultAsync(p => p.GameInstanceId == _realm && p.UserId == userId);
        if (party == null)
        {
            party = new PlayerParty
            {
                GameInstanceId = _realm, UserId = userId, Name = "The Vanguard",
                CharacterIdsJson = MarchingArmy.WriteIds(new List<string> { Hero }),
            };
            _db.PlayerParties.Add(party);
        }
        party.State = CompanyState.Idle;
        party.RegionId = SiteSpec.RegionIdOf(standsAt);
        party.SiteId = standsAt;
        await _db.SaveChangesAsync();
        return new PveBeginRequest(siteId, SharedContract.Version, null, party.Id);
    }

    private async Task<PveClaimResponse> WinAsync(string siteId, string standsAt, string userId = TestIds.Player)
    {
        var (began, runId, _) = await Pve.BeginRiftAsync(_realm, userId, await CompanyAtAsync(standsAt, siteId, userId));
        Assert.True(began.Succeeded, began.Message);
        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow.AddMinutes(-3);
        await _db.SaveChangesAsync();

        var (claimed, response, world) = await Pve.ClaimAsync(_realm, userId, userId, new PveClaimRequest(runId, SharedContract.Version));
        Assert.True(claimed.Succeeded, claimed.Message);
        Assert.Null(world);   // a rift win changes no land
        return response!;
    }

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void ARiftSitesId_RoundTrips_AndARegionsSiteIsNotOne()
    {
        string id = RiftRules.SiteId("abc-123", 2);
        Assert.Equal("rift:abc-123:2", id);
        Assert.True(RiftRules.TryParseSiteId(id, out var rift, out var index));
        Assert.Equal(("abc-123", 2), (rift, index));

        Assert.False(RiftRules.TryParseSiteId("r4:3", out _, out _));
        Assert.False(RiftRules.TryParseSiteId("rift:abc", out _, out _));
        Assert.False(RiftRules.TryParseSiteId("rift::1", out _, out _));
    }

    [Fact]
    public void TheHollowAndTheHeart_AreATierHigher_UpToIV()
    {
        Assert.Equal(2, RiftRules.SiteTier(RiftKind.Great, 2, 0));
        Assert.Equal(3, RiftRules.SiteTier(RiftKind.Great, 2, 1));
        Assert.Equal(3, RiftRules.SiteTier(RiftKind.Great, 2, 2));
        Assert.Equal(4, RiftRules.SiteTier(RiftKind.Great, 4, 2));
        Assert.Equal(2, RiftRules.SiteTier(RiftKind.Lesser, 2, 0));
    }

    [Fact]
    public void TheRiftborn_ComeFromAnotherBiome_TheSameEveryTime()
    {
        foreach (var biome in (BiomeType[])Enum.GetValues(typeof(BiomeType)))
            for (int i = 0; i < 20; i++)
            {
                string rift = $"rift-{i}";
                var donor = RiftRules.DonorBiome(rift, biome);
                Assert.NotEqual(biome, donor);
                Assert.Equal(donor, RiftRules.DonorBiome(rift, biome));
            }
    }

    [Fact]
    public void ARiftRollsTwoMutators_TheBreachTakesTheFirst_TheHollowAndHeartBoth()
    {
        var seen = new HashSet<RiftMutator>();
        for (int i = 0; i < 40; i++)
        {
            string rift = Guid.NewGuid().ToString();
            var breach = RiftRules.Mutators(rift, RiftKind.Great, 0);
            var hollow = RiftRules.Mutators(rift, RiftKind.Great, 1);
            Assert.Single(breach);
            Assert.Equal(2, hollow.Count);
            Assert.Equal(breach[0], hollow[0]);
            Assert.NotEqual(hollow[0], hollow[1]);
            Assert.Equal(hollow, RiftRules.Mutators(rift, RiftKind.Great, 2));
            Assert.Equal(breach, RiftRules.Mutators(rift, RiftKind.Lesser, 0));
            Assert.All(hollow, m => Assert.Contains(m, RiftRules.RolledMutators));
            seen.UnionWith(hollow);
        }
        Assert.Equal(RiftRules.RolledMutators.Length, seen.Count);
    }

    // -----------------------------------------------------------------
    // Going in
    // -----------------------------------------------------------------

    [Fact]
    public async Task ACompanyAnywhereInTheRegion_CanEnterAGreatRiftsSite_AndIsToldTheFight()
    {
        await RealmAsync();
        var rift = await RiftAsync(RiftKind.Great, "r2");

        var (outcome, runId, encounter) = await Pve.BeginRiftAsync(_realm, TestIds.Player,
            await CompanyAtAsync("r2:4", RiftRules.SiteId(rift.Id.ToString(), 1)));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.NotEqual(Guid.Empty, runId);
        Assert.NotNull(encounter);
        Assert.Equal(4, encounter!.Tier);   // the Hollow on tier-4 land stays at IV
        Assert.Equal(RiftRules.SiteLevel(4), encounter.Level);
        Assert.NotEqual(BiomeType.Grassland, encounter.DonorBiome);
        Assert.Equal(2, encounter.Mutators.Count);
    }

    [Fact]
    public async Task NobodyGoesIn_BeforeItOpens_FromAnotherRegion_OrToAnotherLordsLesserRift()
    {
        await RealmAsync();
        var omen = await RiftAsync(RiftKind.Great, "r2", RiftStage.Omen);
        var (early, _, _) = await Pve.BeginRiftAsync(_realm, TestIds.Player,
            await CompanyAtAsync("r2:0", RiftRules.SiteId(omen.Id.ToString(), 0)));
        Assert.Equal(PveError.NotPveTarget, early.Error);

        omen.Stage = RiftStage.Open;
        await _db.SaveChangesAsync();
        var (elsewhere, _, _) = await Pve.BeginRiftAsync(_realm, TestIds.Player,
            await CompanyAtAsync("r1:0", RiftRules.SiteId(omen.Id.ToString(), 0)));
        Assert.Equal(PveError.NoCompanyThere, elsewhere.Error);

        var (noSuchSite, _, _) = await Pve.BeginRiftAsync(_realm, TestIds.Player,
            await CompanyAtAsync("r2:0", RiftRules.SiteId(omen.Id.ToString(), 3)));
        Assert.Equal(PveError.LocationNotFound, noSuchSite.Error);

        var lesser = await RiftAsync(RiftKind.Lesser, "r1");
        var (notTheirs, _, _) = await Pve.BeginRiftAsync(_realm, TestIds.Rival,
            await CompanyAtAsync("r1:0", RiftRules.SiteId(lesser.Id.ToString(), 0), TestIds.Rival));
        Assert.Equal(PveError.NotPveTarget, notTheirs.Error);
    }

    // -----------------------------------------------------------------
    // Winning
    // -----------------------------------------------------------------

    [Fact]
    public async Task AWin_PaysARunAndAQuarter_WearsTheBreach_AndLocksThatSiteFor20Minutes()
    {
        await RealmAsync();
        var rift = await RiftAsync(RiftKind.Great, "r2");
        string heart = RiftRules.SiteId(rift.Id.ToString(), 2);

        var won = await WinAsync(heart, "r2:1");

        int tier = RiftRules.SiteTier(RiftKind.Great, 4, 2);
        var plain = RunRewardCalculator.Calculate(RiftRules.SiteLevel(tier), tier, _content.RunTuning,
            Array.Empty<ItemTemplate>(), new Random(1), site: RiftRules.SiteType);
        Assert.Equal(RiftRules.Boosted(plain.Experience), won.Experience);
        Assert.Equal(RiftRules.Boosted(plain.Gold), won.Gold);

        Assert.NotNull(won.Rift);
        Assert.Equal(100, won.Rift!.BreachTaken);
        Assert.Equal(900, won.Rift.Breach);
        Assert.Equal(RiftStage.Open, won.Rift.Stage);
        var mine = await _db.RiftContributions.AsNoTracking().SingleAsync(c => c.RiftId == rift.Id);
        Assert.Equal((TestIds.Player, 100, 1), (mine.UserId, mine.Breach, mine.Wins));
        Assert.True(won.LockedUntil > DateTime.UtcNow.AddMinutes(19));

        // The same site is locked to them; the rift's other sites are not, and nor is it to anyone else.
        var (again, _, _) = await Pve.BeginRiftAsync(_realm, TestIds.Player, await CompanyAtAsync("r2:1", heart));
        Assert.Equal(PveError.SiteLocked, again.Error);
        var (breach, _, _) = await Pve.BeginRiftAsync(_realm, TestIds.Player,
            await CompanyAtAsync("r2:1", RiftRules.SiteId(rift.Id.ToString(), 0)));
        Assert.True(breach.Succeeded, breach.Message);
        var (rival, _, _) = await Pve.BeginRiftAsync(_realm, TestIds.Rival, await CompanyAtAsync("r2:2", heart, TestIds.Rival));
        Assert.True(rival.Succeeded, rival.Message);

        // The region view's lockout list carries it, at 20 minutes.
        var clears = await Pve.ClearsAsync(_realm, TestIds.Player);
        var locked = Assert.Single(clears.Clears, c => c.SiteId == heart);
        Assert.InRange((locked.LockedUntil - DateTime.UtcNow).TotalMinutes, 19, 20.1);
    }

    [Fact]
    public async Task OneWin_SealsALesserRift_AndItsLordGetsTheChest()
    {
        await RealmAsync();
        var rift = await RiftAsync(RiftKind.Lesser, "r1", RiftStage.Invaded);

        var won = await WinAsync(RiftRules.SiteId(rift.Id.ToString(), 0), "r1:0");

        Assert.Equal(RiftStage.Sealed, won.Rift!.Stage);
        Assert.True(won.Rift.SealedByMe);
        var mine = await _db.RiftContributions.AsNoTracking().SingleAsync(c => c.RiftId == rift.Id);
        Assert.Equal(RiftRules.LesserChest, mine.ChestRarity);
        Assert.Contains(_hub.Sent, s => s.Method == "RiftUpdated");
    }

    [Fact]
    public async Task ARiftThatClosedMidFight_StillPaysTheRun_ButTakesNoBreach()
    {
        await RealmAsync();
        var rift = await RiftAsync(RiftKind.Great, "r2");
        string site = RiftRules.SiteId(rift.Id.ToString(), 0);
        var (began, runId, _) = await Pve.BeginRiftAsync(_realm, TestIds.Player, await CompanyAtAsync("r2:0", site));
        Assert.True(began.Succeeded, began.Message);

        var stored = await _db.Rifts.SingleAsync(r => r.Id == rift.Id);
        stored.Stage = RiftStage.Erupted;
        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow.AddMinutes(-3);
        await _db.SaveChangesAsync();

        var (claimed, response, _) = await Pve.ClaimAsync(_realm, TestIds.Player, "p", new PveClaimRequest(runId, SharedContract.Version));
        Assert.True(claimed.Succeeded, claimed.Message);
        Assert.True(response!.Experience > 0);
        Assert.Equal(0, response.Rift!.BreachTaken);
        Assert.Equal(RiftStage.Erupted, response.Rift.Stage);
    }
}
