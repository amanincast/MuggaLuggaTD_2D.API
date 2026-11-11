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
/// Reclaiming your home region (Mike 2026-11-10): a new seat starts overrun, each held site is freed with a horde
/// fight, the lair waits for the rest, and its win reclaims the region once. Overrun land can't be left, earns
/// nothing, and can't be worked.
/// </summary>
public class HordeTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeGameContent _content = new();

    private GoldService Gold => new(_db, new FakeSessionLog(), NullLogger<GoldService>.Instance);
    private MaterialWalletService Wallet => new(_db, new FakeSessionLog(), NullLogger<MaterialWalletService>.Instance);
    private TavernService Tavern => new(_db, _content, Wallet, Gold, new FakeSessionLog(), NullLogger<TavernService>.Instance);
    private ItemLedgerService Items => new(_db, new FakeSessionLog(), NullLogger<ItemLedgerService>.Instance);
    private WorldPveService Pve => new(_db, _content, Wallet, Gold, Tavern, NullLogger<WorldPveService>.Instance, Items);
    private PartyService Parties => new(_db, Tavern, _content, Wallet, Gold, new FakeSessionLog(), NullLogger<PartyService>.Instance, Items);

    private static string Contract => SharedContract.Version;
    private const string Hero = "hero";

    public void Dispose() => _db.Dispose();

    private static WorldRegionData Overrun(string regionId = "r1")
    {
        var region = TestWorld.OwnedBy(TestIds.Player, regionId, tier: 1);
        region.IsCapital = true;
        region.Horde = HordeRules.NewHorde(1);
        return region;
    }

    private async Task<Guid> SeedAsync(params WorldRegionData[] regions)
    {
        var instance = await _db.AddInstanceAsync(TestIds.Owner, TestIds.Owner);
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(regions));
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, TestSave.ToJson(TestSave.Roster(TestSave.Character(Hero))));
        return instance.Id;
    }

    private async Task<WorldRegionData> StoredAsync(Guid instance, string regionId)
    {
        var row = await _db.WorldViewGameData.AsNoTracking().SingleAsync(w => w.GameInstanceId == instance);
        return WorldRegionBlob.ReadAllRegions(JsonNode.Parse(row.GameData)).Single(r => r.RegionId == regionId);
    }

    private async Task<PveBeginRequest> StandAtAsync(Guid instance, string siteId)
    {
        var party = await _db.PlayerParties.FirstOrDefaultAsync(p => p.GameInstanceId == instance && p.UserId == TestIds.Player);
        if (party == null)
        {
            party = new PlayerParty
            {
                GameInstanceId = instance, UserId = TestIds.Player, Name = "The Vanguard",
                CharacterIdsJson = MarchingArmy.WriteIds(new List<string> { Hero }),
            };
            _db.PlayerParties.Add(party);
        }
        party.State = CompanyState.Idle;
        party.RegionId = SiteSpec.RegionIdOf(siteId);
        party.SiteId = siteId;
        await _db.SaveChangesAsync();
        return new PveBeginRequest(HordeRules.SiteId(siteId), Contract, null, party.Id);
    }

    /// <summary>Frees a site with a won horde fight, writing back the world the claim returns.</summary>
    private async Task<PveClaimResponse> FreeAsync(Guid instance, string siteId)
    {
        var (begun, runId, encounter) = await Pve.BeginHordeAsync(instance, TestIds.Player, await StandAtAsync(instance, siteId));
        Assert.True(begun.Succeeded, begun.Message);
        Assert.NotNull(encounter);
        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(2);
        await _db.SaveChangesAsync();

        var (claimed, response, world) = await Pve.ClaimAsync(instance, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));
        Assert.True(claimed.Succeeded, claimed.Message);
        var row = await _db.WorldViewGameData.SingleAsync(w => w.GameInstanceId == instance);
        row.GameData = world!.ToJsonString();
        await _db.SaveChangesAsync();
        return response!;
    }

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void EverySiteButTheKeepIsHeld_AndTheLairIsTheFarthestCamp()
    {
        var region = Overrun();
        var layout = RegionGenerator.Generate(region);
        var held = HordeRules.HeldSites(layout);

        Assert.DoesNotContain(held, s => s.Type == LocationType.Castle);
        Assert.Equal(layout.Sites.Count - 1, held.Count);
        var lair = HordeRules.LairOf(layout)!;
        Assert.Equal(LocationType.Camp, lair.Type);
        Assert.Equal(held.Last(s => s.Type == LocationType.Camp).SiteId, lair.SiteId);
        Assert.True(HordeRules.IsHeld(region, lair));
    }

    [Fact]
    public void TheLevelsRampOutFromTheKeep_AndTheWarlordStandsAtTheTop()
    {
        var region = Overrun();
        var layout = RegionGenerator.Generate(region);
        var ramp = HordeRules.HeldSites(layout).Where(s => !HordeRules.IsLair(layout, s.SiteId))
            .Select(s => HordeRules.LevelAt(region, layout, s.SiteId)).ToList();

        Assert.Equal(1 + HordeRules.NearLevelsAbove, ramp.First());
        Assert.Equal(1 + HordeRules.FarLevelsAbove, ramp.Last());
        Assert.Equal(ramp.OrderBy(l => l), ramp);
        Assert.Equal(1 + HordeRules.LairLevelsAbove, HordeRules.LevelAt(region, layout, HordeRules.LairOf(layout)!.SiteId));
    }

    [Fact]
    public void AReturningPlayersHorde_StartsAtTheirLevel()
    {
        var region = Overrun();
        region.Horde = HordeRules.NewHorde(20);
        var layout = RegionGenerator.Generate(region);

        Assert.Equal(20 + HordeRules.LairLevelsAbove, HordeRules.LevelAt(region, layout, HordeRules.LairOf(layout)!.SiteId));
    }

    [Fact]
    public void LandWithNoHorde_IsNotOverrun()
    {
        var region = TestWorld.OwnedBy(TestIds.Player, "r1");
        Assert.False(HordeRules.IsOverrun(region));
        Assert.False(HordeRules.IsHeld(region, RegionGenerator.Generate(region).Sites.First(s => s.Type == LocationType.Dungeon)));
    }

    [Fact]
    public void TheHordeSurvivesTheBlob()
    {
        var region = Overrun();
        region.Horde!.Freed.Add("r1:3");
        var read = WorldRegionBlob.ReadAllRegions(TestWorld.Blob(region)).Single();

        Assert.True(HordeRules.IsOverrun(read));
        Assert.Equal(new[] { "r1:3" }, read.Horde!.Freed);
    }

    // -----------------------------------------------------------------
    // Seating
    // -----------------------------------------------------------------

    [Fact]
    public async Task ANewSeatStartsOverrun()
    {
        var instance = await _db.AddInstanceAsync(TestIds.Owner, TestIds.Owner);
        var provisioning = new WorldProvisioningService(_db, NullLogger<WorldProvisioningService>.Instance);
        await provisioning.EnsureWorldAsync(instance.Id);

        Assert.True(await provisioning.EnsureSeatAsync(instance.Id, TestIds.Player, "Mike"));

        var row = await _db.WorldViewGameData.AsNoTracking().SingleAsync(w => w.GameInstanceId == instance.Id);
        var regions = WorldRegionBlob.ReadAllRegions(JsonNode.Parse(row.GameData));
        Assert.True(HordeRules.IsOverrun(regions.Single(r => r.OwnerUserId == TestIds.Player)));
        Assert.True(HordeRules.IsOverrun(regions.Single(r => r.OwnerUserId == TestIds.Owner)), "the world's own seats start overrun too");
    }

    // -----------------------------------------------------------------
    // Freeing and reclaiming
    // -----------------------------------------------------------------

    [Fact]
    public async Task AWonFight_FreesTheSite_AndPays()
    {
        var region = Overrun();
        var instance = await SeedAsync(region);
        var first = HordeRules.HeldSites(RegionGenerator.Generate(region))[0];

        var response = await FreeAsync(instance, first.SiteId);

        Assert.NotNull(response.Horde);
        Assert.Equal(1, response.Horde!.Freed);
        Assert.False(response.Horde.Reclaimed);
        Assert.True(response.Experience > 0);
        Assert.Contains(first.SiteId, (await StoredAsync(instance, "r1")).Horde!.Freed);
    }

    [Fact]
    public async Task AFreedSiteCannotBeFreedTwice()
    {
        var region = Overrun();
        var instance = await SeedAsync(region);
        var first = HordeRules.HeldSites(RegionGenerator.Generate(region))[0];
        await FreeAsync(instance, first.SiteId);

        var (again, _, _) = await Pve.BeginHordeAsync(instance, TestIds.Player, await StandAtAsync(instance, first.SiteId));

        Assert.Equal(PveError.NotPveTarget, again.Error);
    }

    [Fact]
    public async Task TheLairWaitsForTheRest()
    {
        var region = Overrun();
        var instance = await SeedAsync(region);
        var lair = HordeRules.LairOf(RegionGenerator.Generate(region))!;

        var (begun, _, _) = await Pve.BeginHordeAsync(instance, TestIds.Player, await StandAtAsync(instance, lair.SiteId));

        Assert.Equal(PveError.NotPveTarget, begun.Error);
        Assert.Contains("warlord waits", begun.Message);
    }

    [Fact]
    public async Task TheLairsWin_ReclaimsTheRegion_AndPaysTheChestOnce()
    {
        var region = Overrun();
        var layout = RegionGenerator.Generate(region);
        foreach (var site in HordeRules.HeldSites(layout).Where(s => !HordeRules.IsLair(layout, s.SiteId)))
            region.Horde!.Freed.Add(site.SiteId);
        foreach (var pack in HordeRules.Packs(region))
            region.Horde!.Broken.Add(pack.Id);
        var instance = await SeedAsync(region);
        var lair = HordeRules.LairOf(layout)!;

        var response = await FreeAsync(instance, lair.SiteId);

        Assert.True(response.Horde!.Reclaimed);
        Assert.Equal(Enums.ItemRarityTypes.Rare, response.Horde.ChestRarity);
        Assert.Equal(HordeRules.ReclaimGold, response.Horde.ReclaimGold);
        Assert.True(response.Gold >= HordeRules.ReclaimGold);
        var stored = await StoredAsync(instance, "r1");
        Assert.False(HordeRules.IsOverrun(stored));
        Assert.True(HordeRules.IsReclaimed(stored));

        var (again, _, _) = await Pve.BeginHordeAsync(instance, TestIds.Player, await StandAtAsync(instance, lair.SiteId));
        Assert.Equal(PveError.NotPveTarget, again.Error);
    }

    [Fact]
    public async Task GivingUpAHordeFight_BloodiesNobody()
    {
        var region = Overrun();
        var instance = await SeedAsync(region);
        var first = HordeRules.HeldSites(RegionGenerator.Generate(region))[0];
        var (_, runId, _) = await Pve.BeginHordeAsync(instance, TestIds.Player, await StandAtAsync(instance, first.SiteId));

        var (outcome, response) = await Pve.AbandonAsync(instance, TestIds.Player, new PveAbandonRequest(runId, Contract));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Empty(response!.Bloodied);
    }

    [Fact]
    public async Task AHeldDungeon_IsNotAnOrdinaryFight()
    {
        var region = Overrun();
        var instance = await SeedAsync(region);
        var dungeon = TestWorld.SiteOfType(region, LocationType.Dungeon);
        var request = await StandAtAsync(instance, dungeon.SiteId);

        var (begun, _) = await Pve.BeginAsync(instance, TestIds.Player, request with { SiteId = dungeon.SiteId });

        Assert.Equal(PveError.NotPveTarget, begun.Error);
        Assert.Equal(HordeRules.SiteHeld, begun.Message);
    }

    [Fact]
    public async Task SomeoneElsesHorde_IsNotTheirsToFight()
    {
        var region = Overrun();
        var instance = await SeedAsync(region);
        await _db.AddPlayerSaveAsync(instance, TestIds.Rival, TestSave.ToJson(TestSave.Roster(TestSave.Character(Hero))));
        var first = HordeRules.HeldSites(RegionGenerator.Generate(region))[0];
        var request = await StandAtAsync(instance, first.SiteId);
        var party = await _db.PlayerParties.SingleAsync(p => p.Id == request.PartyId);
        party.UserId = TestIds.Rival;
        await _db.SaveChangesAsync();

        var (begun, _, _) = await Pve.BeginHordeAsync(instance, TestIds.Rival, request);

        Assert.Equal(PveError.NotPveTarget, begun.Error);
    }

    // -----------------------------------------------------------------
    // The gates
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheRoadsOutAreHeld_ButInsideTheCompanyGoesWhereItLikes()
    {
        var home = Overrun("r1");
        home.Hex = new HexCoord(0, 0);
        var next = TestWorld.Region("r2");
        next.Hex = new HexCoord(1, 0);
        var instance = await SeedAsync(home, next);
        var save = TestSave.Roster(TestSave.Character("hero-1"), TestSave.Character("hero-2"));
        save.ActiveCharacterIds = new List<string> { "hero-1", "hero-2" };
        var stored = await _db.PlayerGameData.SingleAsync(p => p.GameInstanceId == instance && p.UserId == TestIds.Player);
        stored.GameData = TestSave.ToJson(save);
        await _db.SaveChangesAsync();

        var (_, list) = await Parties.ListAsync(instance, TestIds.Player);
        var company = list!.Parties[0];
        var inside = RegionGenerator.Generate(home).Sites.First(s => s.SiteId != company.SiteId).SiteId;
        var outside = RegionGenerator.Generate(next).Sites[0].SiteId;

        var (leaving, _) = await Parties.TravelAsync(instance, TestIds.Player, company.Id, new PartyTravelRequest(outside, Contract));
        Assert.Equal(PartyError.NoRoute, leaving.Error);
        Assert.Equal(HordeRules.RoadsHeld, leaving.Message);

        var (staying, _) = await Parties.TravelAsync(instance, TestIds.Player, company.Id, new PartyTravelRequest(inside, Contract));
        Assert.True(staying.Succeeded, staying.Message);
    }

    [Fact]
    public void OverrunLandEarnsNothing_UntilReclaimed()
    {
        var region = Overrun();
        Assert.Equal(0, GoldRules.RateFor(region));
        Assert.Equal(0, SeasonScoreRules.RateFor(region));

        region.Horde!.ReclaimedAtUtcTicks = DateTime.UtcNow.Ticks;
        Assert.True(GoldRules.RateFor(region) > 0);
        Assert.True(SeasonScoreRules.RateFor(region) > 0);
    }

    [Fact]
    public async Task AHeldResourceSite_CannotBeWorked()
    {
        var region = Overrun();
        var instance = await SeedAsync(region);
        var hiring = new HiringService(_db, Wallet, Gold, new FakeSessionLog());

        Assert.Contains(RegionGenerator.Generate(region).Sites, s => s.Type == LocationType.ResourceNode);
        var sites = await hiring.SitesAsync(instance, TestIds.Player);

        Assert.Empty(sites);
    }

    // -----------------------------------------------------------------
    // Packs on the roads (phase 1b)
    // -----------------------------------------------------------------

    [Fact]
    public void PacksStandOnTheRoads_ClearOfTheSites_TheSameForEveryReader()
    {
        var region = Overrun();
        var layout = RegionGenerator.Generate(region);
        var packs = HordeRules.Packs(region);
        var again = HordeRules.Packs(region);
        var roads = RegionRoadNetwork.Build(layout, region.Seed, _ => false);

        Assert.InRange(packs.Count, 4, HordeRules.PackCount);
        Assert.Equal(packs.Select(p => (p.Id, p.Cell, p.Size, p.Level)), again.Select(p => (p.Id, p.Cell, p.Size, p.Level)));
        foreach (var pack in packs)
        {
            Assert.True(roads.IsRoad(pack.Cell), $"{pack.Id} is off the road");
            Assert.All(layout.Sites, s => Assert.True(Math.Max(Math.Abs(s.Cell.X - pack.Cell.X), Math.Abs(s.Cell.Y - pack.Cell.Y)) > HordeRules.PackClearOfSites));
            Assert.InRange(pack.Size, HordeRules.PackSmallest, HordeRules.PackLargest);
        }
        Assert.Equal(1 + HordeRules.NearLevelsAbove, packs.First().Level);
        Assert.Equal(1 + HordeRules.FarLevelsAbove, packs.Last().Level);
        Assert.Equal(HordeRules.HeldSites(layout).Count + packs.Count, HordeRules.Total(layout, region.Seed));
    }

    [Fact]
    public async Task ABrokenPack_IsBrokenOnce_AndCounts()
    {
        var region = Overrun();
        var instance = await SeedAsync(region);
        var pack = HordeRules.Packs(region)[0];
        var keep = TestWorld.SiteOfType(region, LocationType.Castle);
        var request = (await StandAtAsync(instance, keep.SiteId)) with { SiteId = HordeRules.PackSiteId(pack.Id) };

        var (begun, runId, encounter) = await Pve.BeginPackAsync(instance, TestIds.Player, request);
        Assert.True(begun.Succeeded, begun.Message);
        Assert.Equal(pack.Size, encounter!.Size);
        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(1);
        await _db.SaveChangesAsync();
        var (claimed, response, world) = await Pve.ClaimAsync(instance, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));
        Assert.True(claimed.Succeeded, claimed.Message);
        var row = await _db.WorldViewGameData.SingleAsync(w => w.GameInstanceId == instance);
        row.GameData = world!.ToJsonString();
        await _db.SaveChangesAsync();

        Assert.Equal(1, response!.Horde!.Freed);
        Assert.Contains(pack.Id, (await StoredAsync(instance, "r1")).Horde!.Broken);
        var (again, _, _) = await Pve.BeginPackAsync(instance, TestIds.Player, request);
        Assert.Equal(PveError.NotPveTarget, again.Error);
    }

    [Fact]
    public void TheLairWaitsForThePacksToo()
    {
        var region = Overrun();
        var layout = RegionGenerator.Generate(region);
        foreach (var site in HordeRules.HeldSites(layout).Where(s => !HordeRules.IsLair(layout, s.SiteId)))
            region.Horde!.Freed.Add(site.SiteId);

        Assert.False(HordeRules.LairOpen(region, layout));
        Assert.Equal(HordeRules.Packs(region).Count, HordeRules.LeftBeforeLair(region, layout));
        foreach (var pack in HordeRules.Packs(region)) region.Horde!.Broken.Add(pack.Id);
        Assert.True(HordeRules.LairOpen(region, layout));
    }
}
