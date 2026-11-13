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
/// Reclaiming phase 2a-i, wild land (Mike 2026-11-12): every neutral region starts overrun by a wild horde sized and
/// levelled by its tier. Anyone may break its warbands; the warlord in its strongest site waits for them all, and its
/// fall cleanses the region for everyone, with a chest for each player who broke two or more. Until then its sites and
/// its keep are locked, and factions leave it alone.
/// </summary>
public class WildHordeTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeGameContent _content = new();

    private GoldService Gold => new(_db, new FakeSessionLog(), NullLogger<GoldService>.Instance);
    private MaterialWalletService Wallet => new(_db, new FakeSessionLog(), NullLogger<MaterialWalletService>.Instance);
    private TavernService Tavern => new(_db, _content, Wallet, Gold, new FakeSessionLog(), NullLogger<TavernService>.Instance);
    private ItemLedgerService Items => new(_db, new FakeSessionLog(), NullLogger<ItemLedgerService>.Instance);
    private WorldPveService Pve => new(_db, _content, Wallet, Gold, Tavern, NullLogger<WorldPveService>.Instance, Items);

    private static string Contract => SharedContract.Version;

    public void Dispose() => _db.Dispose();

    private static WorldRegionData Wild(string regionId = "r5", int tier = 2)
    {
        var region = TestWorld.Region(regionId, q: 2, r: 0, tier: tier);
        region.Horde = HordeRules.NewWildHorde(tier);
        return region;
    }

    private async Task<Guid> SeedAsync(params WorldRegionData[] regions)
    {
        var instance = await _db.AddInstanceAsync(TestIds.Owner, TestIds.Owner);
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(regions));
        foreach (var user in new[] { TestIds.Player, TestIds.Rival })
            await _db.AddPlayerSaveAsync(instance.Id, user, TestSave.ToJson(TestSave.Roster(TestSave.Character(HeroOf(user)))));
        return instance.Id;
    }

    private static string HeroOf(string user) => user == TestIds.Player ? "hero" : "hero-rival";

    private async Task<WorldRegionData> StoredAsync(Guid instance, string regionId)
    {
        var row = await _db.WorldViewGameData.AsNoTracking().SingleAsync(w => w.GameInstanceId == instance);
        return WorldRegionBlob.ReadAllRegions(JsonNode.Parse(row.GameData)).Single(r => r.RegionId == regionId);
    }

    /// <summary>The user's company, standing at <paramref name="siteId"/> in the wild region.</summary>
    private async Task<PlayerParty> StandAtAsync(Guid instance, string user, string siteId)
    {
        var party = await _db.PlayerParties.FirstOrDefaultAsync(p => p.GameInstanceId == instance && p.UserId == user);
        if (party == null)
        {
            party = new PlayerParty
            {
                GameInstanceId = instance, UserId = user, Name = "The Vanguard",
                CharacterIdsJson = MarchingArmy.WriteIds(new List<string> { HeroOf(user) }),
            };
            _db.PlayerParties.Add(party);
        }
        party.State = CompanyState.Idle;
        party.RegionId = SiteSpec.RegionIdOf(siteId);
        party.SiteId = siteId;
        await _db.SaveChangesAsync();
        return party;
    }

    private async Task<PveClaimResponse> WinAsync(Guid instance, string user, Guid runId)
    {
        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(2);
        await _db.SaveChangesAsync();
        var (claimed, response, world) = await Pve.ClaimAsync(instance, user, user, new PveClaimRequest(runId, Contract));
        Assert.True(claimed.Succeeded, claimed.Message);
        var row = await _db.WorldViewGameData.SingleAsync(w => w.GameInstanceId == instance);
        row.GameData = world!.ToJsonString();
        await _db.SaveChangesAsync();
        return response!;
    }

    private async Task<PveClaimResponse> BreakAsync(Guid instance, string user, WorldRegionData region, HordeRules.Pack pack)
    {
        var keep = TestWorld.SiteOfType(region, LocationType.Castle);
        var party = await StandAtAsync(instance, user, keep.SiteId);
        var (begun, runId, _) = await Pve.BeginPackAsync(instance, user,
            new PveBeginRequest(HordeRules.PackSiteId(pack.Id), Contract, null, party.Id));
        Assert.True(begun.Succeeded, begun.Message);
        return await WinAsync(instance, user, runId);
    }

    private async Task<(PveOutcome Begun, Guid RunId, HordeEncounterDto? Encounter)> BeginLairAsync(Guid instance, string user, WorldRegionData region)
    {
        var lair = HordeRules.LairOf(region, RegionGenerator.Generate(region))!;
        var party = await StandAtAsync(instance, user, lair.SiteId);
        return await Pve.BeginHordeAsync(instance, user, new PveBeginRequest(HordeRules.SiteId(lair.SiteId), Contract, null, party.Id));
    }

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(1, 4, 2, 5, 6)]
    [InlineData(2, 6, 7, 10, 11)]
    [InlineData(3, 8, 12, 15, 16)]
    [InlineData(4, 10, 17, 20, 21)]
    public void AWildHorde_IsSizedAndLevelledByTier(int tier, int warbands, int lowest, int highest, int warlord)
    {
        var region = Wild(tier: tier);
        var layout = RegionGenerator.Generate(region);
        var packs = HordeRules.Packs(region);
        var lair = HordeRules.LairOf(region, layout)!;

        Assert.True(HordeRules.IsWild(region));
        Assert.Equal(warbands, packs.Count(p => !HordeRules.IsGuard(p.Id)));
        Assert.Single(packs, p => HordeRules.IsGuard(p.Id));
        var ramp = packs.Where(p => !HordeRules.IsGuard(p.Id)).Select(p => p.Level).ToList();
        Assert.Equal(lowest, ramp.Min());
        Assert.Equal(highest, ramp.Max());
        Assert.Equal(warlord, HordeRules.LevelAt(region, layout, lair.SiteId));
        Assert.Equal(warlord - 1, packs.Single(p => HordeRules.IsGuard(p.Id)).Level);
        Assert.Equal(warbands + 2, HordeRules.Total(region, layout));
    }

    [Fact]
    public void AWildWarlord_SitsInTheStrongestSite()
    {
        var region = Wild(tier: 2);
        var layout = RegionGenerator.Generate(region);
        Assert.Contains(layout.Sites, s => s.Type == LocationType.Dungeon);
        Assert.Equal(LocationType.Dungeon, HordeRules.LairOf(region, layout)!.Type);
    }

    [Fact]
    public void OnlyUnownedFactionFreeLand_IsWild()
    {
        Assert.True(HordeRules.IsWildLand(TestWorld.Region("r1")));
        Assert.False(HordeRules.IsWildLand(TestWorld.OwnedBy(TestIds.Player, "r2")));
        var faction = TestWorld.Region("r3");
        faction.Faction = FactionId.Ashkin;
        Assert.False(HordeRules.IsWildLand(faction));
    }

    [Fact]
    public void AnyoneMayFightIt_AndTheWarlordWaitsForItsWarbandsOnly()
    {
        var region = Wild();
        var layout = RegionGenerator.Generate(region);
        var lair = HordeRules.LairOf(region, layout)!;
        var other = HordeRules.HeldSites(layout).First(s => s.SiteId != lair.SiteId);

        Assert.Null(HordeRules.WhyNotBreak(region, HordeRules.Packs(region)[0].Id, TestIds.Player));
        Assert.Null(HordeRules.WhyNotBreak(region, HordeRules.Packs(region)[0].Id, TestIds.Rival));
        Assert.Equal(HordeRules.WildSiteHeld, HordeRules.WhyNotFree(region, layout, other.SiteId, TestIds.Player));
        StringAssert(HordeRules.WhyNotFree(region, layout, lair.SiteId, TestIds.Player), "warbands are broken");

        foreach (var pack in HordeRules.Packs(region)) region.Horde!.Broken.Add(pack.Id);
        Assert.Null(HordeRules.WhyNotFree(region, layout, lair.SiteId, TestIds.Rival));
    }

    [Fact]
    public void OverrunWildLand_LocksItsSitesAndKeep_UntilCleansed()
    {
        var region = Wild();
        var layout = RegionGenerator.Generate(region);
        var keep = layout.Sites.Single(s => s.Type == LocationType.Castle);
        Assert.True(HordeRules.KeepLocked(region, keep));
        Assert.All(layout.Sites.Where(HordeRules.CanBeHeld), s => Assert.True(HordeRules.IsHeld(region, s)));

        region.Horde!.ReclaimedAtUtcTicks = DateTime.UtcNow.Ticks;
        Assert.True(HordeRules.IsCleansed(region));
        Assert.False(HordeRules.KeepLocked(region, keep));
        Assert.All(layout.Sites, s => Assert.False(HordeRules.IsHeld(region, s)));
    }

    [Fact]
    public void FactionsLeaveOverrunLandAlone()
    {
        var region = Wild(tier: 3);
        var regions = new List<WorldRegionData> { region };
        Assert.False(FactionGrowthRules.IsExpandable(region, regions));
        region.Horde!.ReclaimedAtUtcTicks = 1;
        Assert.True(FactionGrowthRules.IsExpandable(region, regions));
    }

    [Fact]
    public void AWildWarlord_EasesForEachPlayerSeparately()
    {
        var region = Wild();
        region.Horde!.Stakes.Add(new HordeStake { UserId = TestIds.Player, LairTries = 4 });
        Assert.Equal(2, HordeRules.LairEase(region, nextFight: true, userId: TestIds.Player));
        Assert.Equal(0, HordeRules.LairEase(region, nextFight: true, userId: TestIds.Rival));
    }

    [Fact]
    public void TheWildHordeAndItsStakes_SurviveTheBlob()
    {
        var region = Wild();
        var world = TestWorld.Blob(region);
        var node = WorldRegionBlob.FindRegion(world, region.RegionId)!;
        var packs = HordeRules.Packs(region);
        WorldRegionBlob.BreakHordePack(node, packs[0].Id, TestIds.Player);
        WorldRegionBlob.BreakHordePack(node, packs[1].Id, TestIds.Player);
        WorldRegionBlob.BreakHordePack(node, packs[2].Id, TestIds.Rival);
        WorldRegionBlob.TryLair(node, TestIds.Rival);

        var read = WorldRegionBlob.ReadRegion(node);
        Assert.True(read.Horde!.Wild);
        Assert.Equal(2, HordeRules.StakeOf(read, TestIds.Player)!.Broken);
        Assert.Equal(1, HordeRules.StakeOf(read, TestIds.Rival)!.LairTries);
        Assert.Equal(0, read.Horde.LairTries);
        Assert.Equal(new[] { TestIds.Player }, HordeRules.Stakeholders(read));
    }

    // -----------------------------------------------------------------
    // The world and the fights
    // -----------------------------------------------------------------

    [Fact]
    public async Task ANewWorld_OverrunsEveryNeutralRegion_ByTier()
    {
        var instance = await _db.AddInstanceAsync(TestIds.Owner, TestIds.Owner);
        var provisioning = new WorldProvisioningService(_db, NullLogger<WorldProvisioningService>.Instance);
        await provisioning.EnsureWorldAsync(instance.Id);

        var row = await _db.WorldViewGameData.AsNoTracking().SingleAsync(w => w.GameInstanceId == instance.Id);
        var regions = WorldRegionBlob.ReadAllRegions(JsonNode.Parse(row.GameData));
        var wild = regions.Where(r => r.Ownership == LocationOwnership.Neutral && r.Faction == FactionId.None && !r.IsCapital).ToList();
        Assert.NotEmpty(wild);
        Assert.All(wild, r =>
        {
            Assert.True(HordeRules.IsWild(r) && HordeRules.IsOverrun(r), r.RegionId);
            Assert.Equal(HordeRules.WildBaseLevel(r.Tier), r.Horde!.BaseLevel);
        });
        Assert.All(regions.Where(r => r.Faction != FactionId.None && r.Faction != FactionId.Player), r => Assert.Null(r.Horde));
        Assert.False(HordeRules.IsWild(regions.Single(r => r.OwnerUserId == TestIds.Owner)), "a seat's horde is its own");
    }

    [Fact]
    public async Task AWildSiteAndKeep_CannotBeFoughtWhileOverrun()
    {
        var region = Wild();
        var instance = await SeedAsync(region);
        var dungeon = TestWorld.DungeonIn(region);
        var keep = TestWorld.KeepIn(region);

        foreach (var site in new[] { dungeon, keep })
        {
            var party = await StandAtAsync(instance, TestIds.Player, site);
            var (begun, _) = await Pve.BeginAsync(instance, TestIds.Player, new PveBeginRequest(site, Contract, null, party.Id));
            Assert.Equal(PveError.NotPveTarget, begun.Error);
        }
    }

    [Fact]
    public async Task TheWarlordsFall_CleansesTheRegion_AndPaysEverySharerAChest()
    {
        var region = Wild(tier: 3);
        var instance = await SeedAsync(region);
        var packs = HordeRules.Packs(region);

        // The player breaks two (a stake), the rival one (no stake) and the rest; then the rival kills the warlord.
        await BreakAsync(instance, TestIds.Player, region, packs[0]);
        await BreakAsync(instance, TestIds.Player, region, packs[1]);
        foreach (var pack in packs.Skip(2).Take(packs.Count - 3)) await BreakAsync(instance, TestIds.Rival, region, pack);
        var (blocked, _, _) = await BeginLairAsync(instance, TestIds.Rival, await StoredAsync(instance, region.RegionId));
        Assert.Equal(PveError.NotPveTarget, blocked.Error);
        await BreakAsync(instance, TestIds.Player, region, packs[^1]);

        var (begun, runId, encounter) = await BeginLairAsync(instance, TestIds.Rival, await StoredAsync(instance, region.RegionId));
        Assert.True(begun.Succeeded, begun.Message);
        Assert.True(encounter!.IsLair);
        var response = await WinAsync(instance, TestIds.Rival, runId);

        var win = response.Horde!;
        Assert.True(win.Cleansed);
        Assert.False(win.Reclaimed);
        var stored = await StoredAsync(instance, region.RegionId);
        Assert.True(HordeRules.IsCleansed(stored));
        Assert.Equal(LocationOwnership.Neutral, stored.Ownership);
        Assert.Contains(TestIds.Player, win.Sharers);
        Assert.Contains(TestIds.Rival, win.Sharers);
        Assert.True(win.Shared);
        Assert.Equal(Enums.ItemRarityTypes.Rare, win.ChestRarity);


        // Cleansed land is open: its dungeon is an ordinary fight again, and its keep may be claimed.
        var dungeon = TestWorld.DungeonIn(region);
        var party = await StandAtAsync(instance, TestIds.Player, dungeon);
        var (open, _) = await Pve.BeginAsync(instance, TestIds.Player, new PveBeginRequest(dungeon, Contract, null, party.Id));
        Assert.True(open.Succeeded, open.Message);
    }

    [Fact]
    public async Task AKillerWithNoStake_IsPaidTheFight_ButNoChest()
    {
        var region = Wild(tier: 1);
        var instance = await SeedAsync(region);
        var world = TestWorld.Blob(region);
        var node = WorldRegionBlob.FindRegion(world, region.RegionId)!;
        foreach (var pack in HordeRules.Packs(region)) WorldRegionBlob.BreakHordePack(node, pack.Id, TestIds.Player);
        var row = await _db.WorldViewGameData.SingleAsync(w => w.GameInstanceId == instance);
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();

        var (begun, runId, _) = await BeginLairAsync(instance, TestIds.Rival, await StoredAsync(instance, region.RegionId));
        Assert.True(begun.Succeeded, begun.Message);
        var win = (await WinAsync(instance, TestIds.Rival, runId)).Horde!;

        Assert.True(win.Cleansed);
        Assert.False(win.Shared);
        Assert.Null(win.ChestRarity);
        Assert.Equal(new[] { TestIds.Player }, win.Sharers);
    }

    private static void StringAssert(string? actual, string contains)
    {
        Assert.NotNull(actual);
        Assert.Contains(contains, actual);
    }
}
