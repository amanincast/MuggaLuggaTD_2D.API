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
/// Reclaiming phase 2a-ii (Mike 2026-11-12): a cleansed wild keep is claimed only from your border (row P2-9), and wild
/// land claimed beyond your seat's ring needs a steward, a hero of the warlord's level less two at its keep (row P2-10).
/// Without one it is unruly: it earns nothing, and no more wild land may be claimed.
/// </summary>
public class StewardTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeGameContent _content = new();

    private GoldService Gold => new(_db, new FakeSessionLog(), NullLogger<GoldService>.Instance);
    private MaterialWalletService Wallet => new(_db, new FakeSessionLog(), NullLogger<MaterialWalletService>.Instance);
    private TavernService Tavern => new(_db, _content, Wallet, Gold, new FakeSessionLog(), NullLogger<TavernService>.Instance);
    private ItemLedgerService Items => new(_db, new FakeSessionLog(), NullLogger<ItemLedgerService>.Instance);
    private WorldPveService Pve => new(_db, _content, Wallet, Gold, Tavern, NullLogger<WorldPveService>.Instance, Items);
    private WorldGarrisonService Garrisons => new(_db, _content, Gold, new FakeSessionLog(), NullLogger<WorldGarrisonService>.Instance,
        new WarLogService(_db, new FakeHubContext(), NullLogger<WarLogService>.Instance, new FakeClock()));

    private static string Contract => SharedContract.Version;
    private const string Low = "hero-low", High = "hero-high";

    public void Dispose() => _db.Dispose();

    // The seat at (0,0); a held region beside it at (1,0); wild land at (2,0) and (3,0), and (0,1) by the seat.
    private static WorldRegionData Seat()
    {
        var seat = TestWorld.OwnedBy(TestIds.Player, "r0", tier: 1);
        seat.IsCapital = true;
        return seat;
    }

    private static WorldRegionData Held(string id, int q, int r)
    {
        var held = TestWorld.Region(id, q, r, LocationOwnership.Player, TestIds.Player, tier: 1);
        return held;
    }

    private static WorldRegionData Cleansed(string id, int q, int r, int tier = 2)
    {
        var wild = TestWorld.Region(id, q, r, tier: tier);
        wild.Horde = HordeRules.NewWildHorde(tier);
        wild.Horde.ReclaimedAtUtcTicks = DateTime.UtcNow.Ticks;
        return wild;
    }

    private async Task<Guid> SeedAsync(long highLevel, params WorldRegionData[] regions)
    {
        var instance = await _db.AddInstanceAsync(TestIds.Owner, TestIds.Owner);
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(regions));
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player,
            TestSave.ToJson(TestSave.Roster(TestSave.Character(Low, 2), TestSave.Character(High, highLevel))));
        return instance.Id;
    }

    private async Task<WorldRegionData> StoredAsync(Guid instance, string regionId)
    {
        var row = await _db.WorldViewGameData.AsNoTracking().SingleAsync(w => w.GameInstanceId == instance);
        return WorldRegionBlob.ReadAllRegions(JsonNode.Parse(row.GameData)).Single(r => r.RegionId == regionId);
    }

    private async Task<List<WorldRegionData>> AllAsync(Guid instance)
    {
        var row = await _db.WorldViewGameData.AsNoTracking().SingleAsync(w => w.GameInstanceId == instance);
        return WorldRegionBlob.ReadAllRegions(JsonNode.Parse(row.GameData));
    }

    private async Task<PveBeginRequest> AtKeepAsync(Guid instance, WorldRegionData region)
    {
        var keep = TestWorld.KeepIn(region);
        var party = await _db.PlayerParties.FirstOrDefaultAsync(p => p.GameInstanceId == instance && p.UserId == TestIds.Player);
        if (party == null)
        {
            party = new PlayerParty
            {
                GameInstanceId = instance, UserId = TestIds.Player, Name = "The Vanguard",
                CharacterIdsJson = MarchingArmy.WriteIds(new List<string> { Low }),
            };
            _db.PlayerParties.Add(party);
        }
        party.State = CompanyState.Idle;
        party.RegionId = region.RegionId;
        party.SiteId = keep;
        await _db.SaveChangesAsync();
        return new PveBeginRequest(keep, Contract, null, party.Id);
    }

    private async Task<PveOutcome> BeginKeepAsync(Guid instance, WorldRegionData region) =>
        (await Pve.BeginAsync(instance, TestIds.Player, await AtKeepAsync(instance, region))).Outcome;

    private async Task ClaimKeepAsync(Guid instance, WorldRegionData region)
    {
        var (begun, runId) = await Pve.BeginAsync(instance, TestIds.Player, await AtKeepAsync(instance, region));
        Assert.True(begun.Succeeded, begun.Message);
        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(2);
        await _db.SaveChangesAsync();
        var (claimed, _, world) = await Pve.ClaimAsync(instance, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));
        Assert.True(claimed.Succeeded, claimed.Message);
        var row = await _db.WorldViewGameData.SingleAsync(w => w.GameInstanceId == instance);
        row.GameData = world!.ToJsonString();
        await _db.SaveChangesAsync();
    }

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 9)]
    [InlineData(3, 14)]
    [InlineData(4, 19)]
    public void AStewardStandsTwoLevelsUnderTheWarlord(int tier, int level) => Assert.Equal(level, StewardRules.LevelFor(tier));

    [Fact]
    public void TheSeatOverseesTheRegionsTouchingIt()
    {
        var regions = new List<WorldRegionData> { Seat(), Cleansed("r1", 0, 1), Cleansed("r2", 2, 0) };
        Assert.False(StewardRules.WouldNeedSteward(regions[1], TestIds.Player, regions));
        Assert.True(StewardRules.WouldNeedSteward(regions[2], TestIds.Player, regions));
    }

    [Fact]
    public void UnrulyLand_EarnsNothing_UntilItsStewardStandsThere()
    {
        var region = Held("r1", 2, 0);
        region.StewardNeeded = true;
        Assert.True(StewardRules.IsUnruly(region));
        Assert.Equal(0, GoldRules.RateFor(region));
        Assert.Equal(0, SeasonScoreRules.RateFor(region));

        region.StewardId = High;
        Assert.True(StewardRules.IsUnruly(region), "appointed, but not standing there");
        region.SiteOverrides[TestWorld.KeepIn(region)] = new SiteOverride { GarrisonCharacterIds = new List<string> { High } };
        Assert.False(StewardRules.IsUnruly(region));
        Assert.True(GoldRules.RateFor(region) > 0);
    }

    [Fact]
    public void TheStrongestHeroOfTheLevel_IsChosen()
    {
        var region = Cleansed("r2", 2, 0, tier: 2);
        var levels = new Dictionary<string, int> { [Low] = 2, [High] = 12, ["mid"] = 9 };
        Assert.Equal(High, StewardRules.ChooseSteward(region, levels.Keys, id => levels[id]));
        Assert.Null(StewardRules.ChooseSteward(region, new[] { Low }, id => levels[id]));
    }

    // -----------------------------------------------------------------
    // Claiming
    // -----------------------------------------------------------------

    [Fact]
    public async Task WildLand_IsClaimedOnlyFromYourBorder()
    {
        var far = Cleansed("r3", 3, 0);
        var instance = await SeedAsync(20, Seat(), far);
        var begun = await BeginKeepAsync(instance, far);
        Assert.Equal(PveError.NotPveTarget, begun.Error);
        Assert.Equal(StewardRules.NotBordering, begun.Message);
    }

    [Fact]
    public async Task LandTouchingTheSeat_NeedsNoSteward()
    {
        var near = Cleansed("r1", 0, 1);
        var instance = await SeedAsync(1, Seat(), near);
        await ClaimKeepAsync(instance, near);

        var stored = await StoredAsync(instance, "r1");
        Assert.True(stored.IsOwnedByPlayer(TestIds.Player));
        Assert.False(stored.StewardNeeded);
        Assert.False(StewardRules.IsUnruly(stored));
    }

    [Fact]
    public async Task LandBeyondTheRing_NeedsAHeroStrongEnough_FreeToStewardIt()
    {
        var beyond = Cleansed("r2", 2, 0, tier: 2);
        var instance = await SeedAsync(8, Seat(), Held("r1", 1, 0), beyond);
        var begun = await BeginKeepAsync(instance, beyond);
        Assert.Equal(PveError.NotPveTarget, begun.Error);
        Assert.Contains("level 9", begun.Message);
    }

    [Fact]
    public async Task AClaimBeyondTheRing_IsUnruly_UntilItsStewardIsStationed_AndBlocksTheNext()
    {
        var beyond = Cleansed("r2", 2, 0, tier: 2);
        var next = Cleansed("r4", 1, -1, tier: 1);   // beside the seat: no steward of its own needed
        var instance = await SeedAsync(12, Seat(), Held("r1", 1, 0), beyond, next);
        await ClaimKeepAsync(instance, beyond);

        var stored = await StoredAsync(instance, "r2");
        Assert.True(stored.StewardNeeded);
        Assert.True(StewardRules.IsUnruly(stored));
        var blocked = await BeginKeepAsync(instance, next);
        Assert.Equal(PveError.NotPveTarget, blocked.Error);
        Assert.Contains("has no steward", blocked.Message);

        // Stationing a hero under the level appoints nobody; the strong one is the steward.
        var keep = TestWorld.KeepIn(beyond);
        var (weak, weakResponse, weakWorld) = await Garrisons.SetAsync(instance, TestIds.Player, new GarrisonRequest(keep, new List<string> { Low }, Contract));
        Assert.True(weak.Succeeded, weak.Message);
        Assert.Null(weakResponse!.StewardId);
        var (set, response, world) = await Garrisons.SetAsync(instance, TestIds.Player, new GarrisonRequest(keep, new List<string> { Low, High }, Contract));
        Assert.True(set.Succeeded, set.Message);
        Assert.Equal(High, response!.StewardId);
        var row = await _db.WorldViewGameData.SingleAsync(w => w.GameInstanceId == instance);
        row.GameData = world!.ToJsonString();
        await _db.SaveChangesAsync();

        Assert.False(StewardRules.IsUnruly(await StoredAsync(instance, "r2")));
        Assert.Null(StewardRules.FirstUnruly(TestIds.Player, await AllAsync(instance)));
        Assert.Null(StewardRules.WhyNotClaim(next, TestIds.Player, await AllAsync(instance)));   // with its steward home, the next may be claimed
    }
}
