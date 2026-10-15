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
/// Treasure maps (Active Content D): a rare find points to a cache near its holder's land; a company at rest there
/// fights its guardians; the win opens it once, for a chest and twice a clear's gold; a player holds at most three.
/// </summary>
public class TreasureMapTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
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
    private TreasureMapService Maps => new(_db, _log, NullLogger<TreasureMapService>.Instance);

    private WorldPveService Pve => new(_db, _content, Wallet, Gold,
        new TavernService(_db, _content, Wallet, Gold, _log, NullLogger<TavernService>.Instance),
        NullLogger<WorldPveService>.Instance, Items, maps: Maps);

    private async Task RealmAsync()
    {
        var instance = await _db.AddInstanceAsync();
        _realm = instance.Id;
        await _db.AddWorldAsync(_realm, TestWorld.Blob(Map().ToArray()));
        _db.Users.Add(new ApplicationUser { Id = TestIds.Player, UserName = TestIds.Player, DisplayName = TestIds.Player });
        await _db.AddPlayerSaveAsync(_realm, TestIds.Player, TestSave.ToJson(TestSave.Roster(TestSave.Character(Hero))));
        await _db.SaveChangesAsync();
    }

    /// <summary>A capital of the player's in the middle, six regions round it, and one far off.</summary>
    private static List<WorldRegionData> Map()
    {
        var capital = TestWorld.Region("r0", 0, 0, LocationOwnership.Player, TestIds.Player, tier: 1);
        capital.IsCapital = true;
        var map = new List<WorldRegionData> { capital };
        int n = 1;
        foreach (var hex in new HexCoord(0, 0).Neighbours())
            map.Add(TestWorld.Region($"r{n++}", hex.Q, hex.R, tier: 1, biome: BiomeType.Forest));
        map.Add(TestWorld.Region("far", 9, 0, tier: 1));
        return map;
    }

    private async Task<PveBeginRequest> CompanyInAsync(string regionId, Guid mapId)
    {
        var party = await _db.PlayerParties.FirstOrDefaultAsync(p => p.GameInstanceId == _realm && p.UserId == TestIds.Player);
        if (party == null)
        {
            party = new PlayerParty
            {
                GameInstanceId = _realm, UserId = TestIds.Player, Name = "The Vanguard",
                CharacterIdsJson = MarchingArmy.WriteIds(new List<string> { Hero }),
            };
            _db.PlayerParties.Add(party);
        }
        party.State = CompanyState.Idle;
        party.RegionId = regionId;
        party.SiteId = $"{regionId}:1";
        await _db.SaveChangesAsync();
        return new PveBeginRequest(TreasureMapRules.SiteId(mapId), SharedContract.Version, null, party.Id);
    }

    private async Task<PveClaimResponse?> ClaimAsync(Guid runId)
    {
        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow.AddMinutes(-3);
        await _db.SaveChangesAsync();
        var (claimed, response, _) = await Pve.ClaimAsync(_realm, TestIds.Player, TestIds.Player, new PveClaimRequest(runId, SharedContract.Version));
        return claimed.Succeeded ? response : null;
    }

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void ACachesSiteId_ReadsBackItsMap()
    {
        var id = Guid.NewGuid();
        Assert.True(TreasureMapRules.TryParseSiteId(TreasureMapRules.SiteId(id), out var back));
        Assert.Equal(id, back);
        Assert.False(TreasureMapRules.IsSiteId("bounty:12"));
    }

    [Fact]
    public void OnlyDungeonsAndPortals_FindMaps()
    {
        Assert.Equal(TreasureMapRules.DungeonChance, TreasureMapRules.ChanceFrom(LocationType.Dungeon));
        Assert.Equal(TreasureMapRules.PortalChance, TreasureMapRules.ChanceFrom(LocationType.Portal));
        Assert.Equal(0, TreasureMapRules.ChanceFrom(LocationType.Camp));
        Assert.True(TreasureMapRules.DungeonChance > TreasureMapRules.PortalChance);
    }

    [Fact]
    public void AMap_PointsNearItsHoldersLand_NeverIntoACapital_AndAlwaysTheSamePlace()
    {
        var eligible = TreasureMapRules.Eligible(Map(), TestIds.Player);
        Assert.DoesNotContain(eligible, r => r.IsCapital);
        Assert.DoesNotContain(eligible, r => r.RegionId == "far");
        Assert.Equal(6, eligible.Count);

        var id = Guid.NewGuid();
        Assert.Equal(TreasureMapRules.PickRegion(id, eligible)!.RegionId, TreasureMapRules.PickRegion(id, eligible)!.RegionId);
        var picks = Enumerable.Range(0, 60).Select(_ => TreasureMapRules.PickRegion(Guid.NewGuid(), eligible)!.RegionId).Distinct().Count();
        Assert.True(picks > 1);

        var layout = RegionGenerator.Generate(eligible[0]);
        var landmark = TreasureMapRules.Landmark(id, layout);
        Assert.NotNull(landmark);
        Assert.True(landmark!.IsFightable);
        Assert.Equal(landmark.SiteId, TreasureMapRules.Landmark(id, layout)!.SiteId);
        Assert.Equal((Math.Max(1, landmark.Tier), Math.Max(1, landmark.Level) + 1), TreasureMapRules.Terms(landmark));
    }

    // -----------------------------------------------------------------
    // Finding and holding
    // -----------------------------------------------------------------

    [Fact]
    public async Task AFind_IsMadeUnderTheChance_AndAPlayerHoldsThreeAtMost()
    {
        await RealmAsync();
        Assert.Null(await Maps.TryFindAsync(_realm, TestIds.Player, LocationType.Dungeon, "r1:2", 0.5));
        Assert.Null(await Maps.TryFindAsync(_realm, TestIds.Player, LocationType.Camp, "r1:2", 0.0));

        for (int i = 0; i < TreasureMapRules.MostHeld; i++)
            Assert.NotNull(await Maps.TryFindAsync(_realm, TestIds.Player, LocationType.Dungeon, "r1:2", 0.0));
        Assert.Null(await Maps.TryFindAsync(_realm, TestIds.Player, LocationType.Dungeon, "r1:2", 0.0));

        var view = await Maps.ViewAsync(_realm, TestIds.Player);
        Assert.Equal(TreasureMapRules.MostHeld, view.Held.Count);
        Assert.All(view.Held, m => Assert.NotEqual("r0", m.RegionId));
        Assert.Empty(await Maps.ViewAsync(_realm, TestIds.Rival).ContinueWith(t => t.Result.Held));
    }

    // -----------------------------------------------------------------
    // The cache
    // -----------------------------------------------------------------

    [Fact]
    public async Task OnlyACompanyAtRestWhereTheMapPoints_MayDig()
    {
        await RealmAsync();
        var map = (await Maps.GrantAsync(_realm, TestIds.Player, "debug", null, regionId: "r2"))!;

        var elsewhere = await Pve.BeginCacheAsync(_realm, TestIds.Player, await CompanyInAsync("r3", map.Id));
        Assert.Equal(PveError.NoCompanyThere, elsewhere.Outcome.Error);

        var here = await Pve.BeginCacheAsync(_realm, TestIds.Player, await CompanyInAsync("r2", map.Id));
        Assert.True(here.Outcome.Succeeded, here.Outcome.Message);
        Assert.Equal("r2", here.Encounter!.RegionId);
        Assert.Equal(map.Level, here.Encounter.Level);

        var stranger = await Pve.BeginCacheAsync(_realm, TestIds.Rival, await CompanyInAsync("r2", map.Id));
        Assert.False(stranger.Outcome.Succeeded);
    }

    [Fact]
    public async Task AWin_OpensTheCacheOnce_ForAChestAndTwiceAClearsGold()
    {
        await RealmAsync();
        var map = (await Maps.GrantAsync(_realm, TestIds.Player, "debug", null, regionId: "r2"))!;

        var first = await Pve.BeginCacheAsync(_realm, TestIds.Player, await CompanyInAsync("r2", map.Id));
        var second = await Pve.BeginCacheAsync(_realm, TestIds.Player, await CompanyInAsync("r2", map.Id));
        var won = await ClaimAsync(first.RunId);

        Assert.NotNull(won!.Cache);
        Assert.True(won.Cache!.ChestRarity >= ItemRarityTypes.Rare);
        Assert.NotEmpty(won.Cache.Chest);
        Assert.Equal(won.Gold, won.Cache.Gold);
        Assert.Equal(TreasureMapRules.CacheGold(GoldRules.GoldForClear(won.Experience)), won.Gold);
        Assert.Equal(won.GoldBalance, await Gold.BalanceAsync(_realm, TestIds.Player));

        // The second run, opened while the map was still held, finds the cache empty.
        Assert.Null(await ClaimAsync(second.RunId));
        var view = await Maps.ViewAsync(_realm, TestIds.Player);
        Assert.DoesNotContain(view.Held, m => m.Id == map.Id);
        Assert.Contains(view.Opened, m => m.Id == map.Id);

        // And it cannot be dug again.
        var again = await Pve.BeginCacheAsync(_realm, TestIds.Player, await CompanyInAsync("r2", map.Id));
        Assert.False(again.Outcome.Succeeded);
    }
}
