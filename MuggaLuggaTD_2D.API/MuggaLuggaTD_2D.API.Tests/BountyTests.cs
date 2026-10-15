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
/// Bounties, phase 1 (Active Content A): two wanted champions a day walk a seeded path for 36 hours; the
/// first player to beat one claims it, a later winner gets half the gold, and a loser waits two hours.
/// </summary>
public class BountyTests : IDisposable
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
    private BountyService Bounties => new(_db, _content, NullLogger<BountyService>.Instance);

    private WorldPveService Pve => new(_db, _content, Wallet, Gold,
        new TavernService(_db, _content, Wallet, Gold, _log, NullLogger<TavernService>.Instance),
        NullLogger<WorldPveService>.Instance, Items, bounties: Bounties);

    /// <summary>A small realm whose season began three days ago, so champions roam; the player and the rival each have a hero.</summary>
    private async Task RealmAsync()
    {
        var instance = await _db.AddInstanceAsync();
        instance.SeasonStartedAt = DateTime.UtcNow.AddDays(-3);
        _realm = instance.Id;
        await _db.AddWorldAsync(_realm, TestWorld.Blob(Map().ToArray()));
        foreach (var user in new[] { TestIds.Player, TestIds.Rival })
        {
            _db.Users.Add(new ApplicationUser { Id = user, UserName = user, DisplayName = user });
            await _db.AddPlayerSaveAsync(_realm, user, TestSave.ToJson(TestSave.Roster(TestSave.Character(Hero))));
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>A ring of seven: a capital in the middle, six regions round it.</summary>
    private static List<WorldRegionData> Map()
    {
        var capital = TestWorld.Region("r0", 0, 0, LocationOwnership.Player, TestIds.Player, tier: 1);
        capital.IsCapital = true;
        var map = new List<WorldRegionData> { capital };
        int n = 1;
        foreach (var hex in new HexCoord(0, 0).Neighbours())
            map.Add(TestWorld.Region($"r{n++}", hex.Q, hex.R, tier: 1, biome: BiomeType.Forest));
        return map;
    }

    private long LiveSlot() => BountyRules.LiveSlots(DateTime.UtcNow, DateTime.UtcNow.AddDays(-3)).Last();

    private async Task<PveBeginRequest> CompanyInAsync(string regionId, long slot, string userId)
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
        party.RegionId = regionId;
        party.SiteId = $"{regionId}:1";
        await _db.SaveChangesAsync();
        return new PveBeginRequest(BountyRules.SiteId(slot), SharedContract.Version, null, party.Id);
    }

    private async Task<(PveOutcome Outcome, Guid RunId, BountyEncounterDto? Encounter)> BeginAsync(string userId, long slot, string regionId = "r2") =>
        await Pve.BeginBountyAsync(_realm, userId, await CompanyInAsync(regionId, slot, userId));

    private async Task<PveClaimResponse> ClaimAsync(string userId, Guid runId)
    {
        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow.AddMinutes(-3);
        await _db.SaveChangesAsync();
        var (claimed, response, world) = await Pve.ClaimAsync(_realm, userId, userId, new PveClaimRequest(runId, SharedContract.Version));
        Assert.True(claimed.Succeeded, claimed.Message);
        Assert.Null(world);
        return response!;
    }

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void TwoADay_EachRoamsThirtySixHours_NoneFromBeforeTheSeason()
    {
        var noon = new DateTime(2026, 10, 15, 13, 0, 0, DateTimeKind.Utc);
        var live = BountyRules.LiveSlots(noon, noon.AddDays(-10));
        Assert.Equal(3, live.Count);
        Assert.All(live, s => Assert.True(noon < BountyRules.EscapesAt(s) && noon >= BountyRules.SlotStart(s)));
        Assert.Equal(new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc), BountyRules.SlotStart(live.Last()));

        // A season begun at 11:00 has seen one slot start: noon's.
        Assert.Single(BountyRules.LiveSlots(noon, noon.AddHours(-2)));
        Assert.True(BountyRules.TryParseSiteId(BountyRules.SiteId(live[0]), out long back) && back == live[0]);
        Assert.False(BountyRules.IsSiteId("r3:1"));
    }

    [Fact]
    public void AChampion_WalksNeighbours_NeverACapital_TheSameEveryTime()
    {
        var map = Map();
        for (long slot = 1000; slot < 1020; slot++)
        {
            var champion = BountyRules.Champion("realm", slot, map, new FakeGameContent().EnemyPeoples)!;
            Assert.Equal(BountyRules.PathLength, champion.Path.Count);
            Assert.DoesNotContain("r0", champion.Path);
            for (int i = 1; i < champion.Path.Count; i++)
            {
                var a = map.Single(r => r.RegionId == champion.Path[i - 1]).Hex;
                var b = map.Single(r => r.RegionId == champion.Path[i]).Hex;
                Assert.Equal(1, HexCoord.Distance(a, b));
            }
            Assert.Contains(champion.People, new[] { "Wolf", "Troll" });
            Assert.Equal(2, champion.Mutators.Distinct().Count());
            Assert.False(string.IsNullOrWhiteSpace(champion.Name));
            Assert.StartsWith(champion.People + " ", champion.Title);

            var again = BountyRules.Champion("realm", slot, map, new FakeGameContent().EnemyPeoples)!;
            Assert.Equal(champion.Path, again.Path);
            Assert.Equal(champion.Name, again.Name);
        }
    }

    [Fact]
    public void AChampion_HasTeeth()
    {
        Assert.Equal(3, BountyRules.TierIn(1));
        Assert.Equal(4, BountyRules.TierIn(3));
        Assert.Equal(4, BountyRules.TierIn(4));
        Assert.Equal(RiftRules.SiteLevel(3) + 2, BountyRules.LevelOf(3));
        Assert.Equal(80, SeasonScoreRules.PointsFor(SeasonDeed.BountyClaimed));
        Assert.Equal(300, BountyRules.Bounty(100));
        Assert.Equal(150, BountyRules.Consolation(300));
    }

    // -----------------------------------------------------------------
    // The hunt
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheFirstWin_ClaimsIt_ALaterWinnerGetsHalf_AndNobodyGoesInAfter()
    {
        await RealmAsync();
        long slot = LiveSlot();
        Assert.Null(await Bounties.DebugPinAsync(_realm, "r2"));

        // Both go in before anyone has won.
        var (mine, myRun, encounter) = await BeginAsync(TestIds.Player, slot);
        Assert.True(mine.Succeeded, mine.Message);
        Assert.Equal(3, encounter!.Tier);
        Assert.True(encounter.Gold > 0);
        var (theirs, theirRun, _) = await BeginAsync(TestIds.Rival, slot);
        Assert.True(theirs.Succeeded, theirs.Message);

        var first = await ClaimAsync(TestIds.Player, myRun);
        Assert.True(first.Bounty!.First);
        Assert.Equal(encounter.Gold, first.Bounty.Gold);
        Assert.Contains(first.Bounty.ChestRarity, new ItemRarityTypes?[] { ItemRarityTypes.Rare, ItemRarityTypes.Legendary });
        Assert.Equal("r2", first.Bounty.RegionId);

        var late = await ClaimAsync(TestIds.Rival, theirRun);
        Assert.False(late.Bounty!.First);
        Assert.Equal(BountyRules.Consolation(encounter.Gold), late.Bounty.Gold);
        Assert.Null(late.Bounty.ChestRarity);
        Assert.Empty(late.Bounty.Chest);
        Assert.Equal(TestIds.Player, late.Bounty.ClaimedByName);

        var row = await _db.Bounties.AsNoTracking().SingleAsync();
        Assert.Equal(TestIds.Player, row.ClaimedByUserId);

        // Claimed: nobody else goes in.
        _db.PveRuns.RemoveRange(_db.PveRuns.Where(r => r.UserId == TestIds.Rival));
        await _db.SaveChangesAsync();
        var (after, _, _) = await BeginAsync(TestIds.Rival, slot);
        Assert.Equal(PveError.NotPveTarget, after.Error);

        // And the board shows who.
        var board = await Bounties.ViewAsync(_realm, TestIds.Rival);
        Assert.DoesNotContain(board.Live, b => b.Slot == slot);
        var fallen = board.Recent.Single(b => b.Slot == slot);
        Assert.Equal(TestIds.Player, fallen.ClaimedByName);
        Assert.False(fallen.ClaimedByMe);
    }

    [Fact]
    public async Task AFall_IsInTheWarLog_AndLettersGoToTheOtherHunters()
    {
        await RealmAsync();
        long slot = LiveSlot();
        await Bounties.DebugPinAsync(_realm, "r2");
        var (_, myRun, _) = await BeginAsync(TestIds.Player, slot);
        await BeginAsync(TestIds.Rival, slot);
        var won = (await ClaimAsync(TestIds.Player, myRun)).Bounty!;

        var hub = new FakeHubContext();
        var clock = new FakeClock(DateTime.UtcNow);
        var letters = new LetterService(_db, hub, NullLogger<LetterService>.Instance, clock);
        var warLog = new WarLogService(_db, hub, NullLogger<WarLogService>.Instance, clock, letters);
        var announcing = new BountyService(_db, _content, NullLogger<BountyService>.Instance, hub, warLog, letters);
        await announcing.AnnounceClaimedAsync(_realm, TestIds.Player,
            new BountyClaimedNotification(won.Slot, won.Name, won.Title, "Mira", won.RegionId));

        var line = await _db.WarLog.AsNoTracking().SingleAsync(e => e.Kind == nameof(WarLogKind.BountyClaimed));
        Assert.Equal($"{won.Name}|{won.Title}", line.Detail);
        Assert.Equal("r2", line.RegionId);

        var letter = await _db.Letters.AsNoTracking().SingleAsync(l => l.Kind == nameof(LetterKind.BountyClaimed));
        Assert.Equal(TestIds.Rival, letter.UserId);
        Assert.Equal(LetterCategory.Quests, LetterRules.CategoryOf(LetterKind.BountyClaimed));
    }

    [Fact]
    public async Task OneAttempt_EveryTwoHours()
    {
        await RealmAsync();
        long slot = LiveSlot();
        await Bounties.DebugPinAsync(_realm, "r2");

        Assert.True((await BeginAsync(TestIds.Player, slot)).Outcome.Succeeded);
        var (again, _, _) = await BeginAsync(TestIds.Player, slot);
        Assert.Equal(PveError.SiteLocked, again.Error);

        var mine = (await Bounties.ViewAsync(_realm, TestIds.Player)).Live.Single(b => b.Slot == slot);
        Assert.NotNull(mine.MyNextAttemptAt);
        Assert.Null((await Bounties.ViewAsync(_realm, TestIds.Rival)).Live.Single(b => b.Slot == slot).MyNextAttemptAt);
    }

    [Fact]
    public async Task YouFightItWhereItStands()
    {
        await RealmAsync();
        long slot = LiveSlot();
        await Bounties.DebugPinAsync(_realm, "r2");

        var (elsewhere, _, _) = await BeginAsync(TestIds.Player, slot, regionId: "r4");
        Assert.Equal(PveError.NoCompanyThere, elsewhere.Error);

        var board = await Bounties.ViewAsync(_realm, TestIds.Player);
        Assert.Equal("r2", board.Live.Single(b => b.Slot == slot).RegionId);
    }

    [Fact]
    public async Task AChampionFromBeforeTheSeason_OrStillToCome_IsNotThere()
    {
        await RealmAsync();
        long before = BountyRules.SlotOf(DateTime.UtcNow.AddDays(-4));
        var (old, _, _) = await BeginAsync(TestIds.Player, before);
        Assert.Equal(PveError.LocationNotFound, old.Error);

        long future = BountyRules.SlotOf(DateTime.UtcNow) + 1;
        var (soon, _, _) = await BeginAsync(TestIds.Player, future);
        Assert.Equal(PveError.NotPveTarget, soon.Error);

        var board = await Bounties.ViewAsync(_realm, TestIds.Player);
        Assert.InRange(board.Live.Count, 2, 3);
        Assert.All(board.Live, b => Assert.NotEqual("r0", b.RegionId));
    }
}
