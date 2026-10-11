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
/// Rifts, phase 5: Rift contracts (Invasions spec R7). A Lesser Rift's lord posts a Sell (the taker pays) or a Hire
/// (the lord pays); the first taker holds the right to close it for 30 minutes. The server holds the gold until the
/// holder closes the rift, and hands it back on every other ending.
/// </summary>
public class RiftContractTests : IDisposable
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
        return new RiftService(_db, _content, Items, _log, NullLogger<RiftService>.Instance, _hub, new RealmPresence(), warLog, letters,
            gold: Gold);
    }

    private WorldPveService Pve => new(_db, _content, Wallet, Gold,
        new TavernService(_db, _content, Wallet, Gold, _log, NullLogger<TavernService>.Instance),
        NullLogger<WorldPveService>.Instance, Items, rifts: Rifts());

    /// <summary>The player holds r1, and its Lesser Rift; the rival holds r3. Each has 1,000 gold.</summary>
    private async Task<Rift> RealmAsync(RiftStage stage = RiftStage.Open)
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
        foreach (var user in new[] { TestIds.Player, TestIds.Rival })
            await Gold.CreditAsync(_realm, user, 1000, "test");

        var now = DateTime.UtcNow;
        var rift = new Rift
        {
            GameInstanceId = _realm, SeasonNumber = 1, Kind = RiftKind.Lesser, EventNumber = 1, RegionId = "r1",
            TargetUserId = TestIds.Player, Stage = stage,
            OmenAt = now.AddMinutes(-30), OpensAt = now.AddMinutes(-10), ClosesAt = now.AddHours(1),
            InvadedAt = stage == RiftStage.Invaded ? now.AddMinutes(-5) : null, BreachMax = 1, Breach = 1,
        };
        _db.Rifts.Add(rift);
        await _db.SaveChangesAsync();
        return rift;
    }

    private Task<long> GoldOf(string userId) => Gold.BalanceAsync(_realm, userId);

    private Task<RiftContract> ContractAsync(Guid riftId) =>
        _db.RiftContracts.AsNoTracking().OrderByDescending(c => c.PostedAt).FirstAsync(c => c.RiftId == riftId);

    private async Task<PveBeginRequest> CompanyAtAsync(string standsAt, string siteId, string userId)
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

    private async Task<PveOutcome> BeginAsync(Rift rift, string userId) =>
        (await Pve.BeginRiftAsync(_realm, userId, await CompanyAtAsync("r1:1", RiftRules.SiteId(rift.Id.ToString(), 0), userId))).Outcome;

    /// <summary>Wins the rift's fight; returns the run's own gold, which is paid apart from any contract.</summary>
    private async Task<long> WinAsync(Rift rift, string userId)
    {
        var (began, runId, _) = await Pve.BeginRiftAsync(_realm, userId,
            await CompanyAtAsync("r1:1", RiftRules.SiteId(rift.Id.ToString(), 0), userId));
        Assert.True(began.Succeeded, began.Message);
        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow.AddMinutes(-3);
        await _db.SaveChangesAsync();
        var (claimed, response, _) = await Pve.ClaimAsync(_realm, userId, userId, new PveClaimRequest(runId, SharedContract.Version));
        Assert.True(claimed.Succeeded, claimed.Message);
        return response!.Gold;
    }

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void WhileAHoldStands_OnlyItsHolderMayClose_ThenTheLordAgain()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(RiftRules.MayCloseLesser("lord", "lord", null, null, now));
        Assert.False(RiftRules.MayCloseLesser("other", "lord", null, null, now));
        Assert.True(RiftRules.MayCloseLesser("taker", "lord", "taker", now.AddMinutes(5), now));
        Assert.False(RiftRules.MayCloseLesser("lord", "lord", "taker", now.AddMinutes(5), now));
        Assert.True(RiftRules.MayCloseLesser("lord", "lord", "taker", now.AddMinutes(-1), now));
        Assert.False(RiftRules.MayCloseLesser("taker", "lord", "taker", now.AddMinutes(-1), now));

        Assert.Equal("taker", RiftRules.RefundTo(RiftContractKind.Sell, "lord", "taker"));
        Assert.Equal("lord", RiftRules.RefundTo(RiftContractKind.Hire, "lord", "taker"));
        Assert.Equal("lord", RiftRules.PaysTo(RiftContractKind.Sell, "lord", "taker"));
        Assert.Equal("taker", RiftRules.PaysTo(RiftContractKind.Hire, "lord", "taker"));
    }

    // -----------------------------------------------------------------
    // Posting
    // -----------------------------------------------------------------

    [Fact]
    public async Task OnlyTheLord_PostsOnALesserRift_AtAFairPrice_OneAtATime()
    {
        var rift = await RealmAsync();
        Assert.Equal(RiftContractError.NotYours,
            (await Rifts().PostContractAsync(_realm, TestIds.Rival, rift.Id, RiftContractKind.Sell, 300)).Error);
        Assert.Equal(RiftContractError.BadPrice,
            (await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Sell, RiftRules.ContractMinPrice - 1)).Error);
        Assert.Equal(RiftContractError.InsufficientGold,
            (await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Hire, 5000)).Error);
        Assert.Equal(1000, await GoldOf(TestIds.Player));

        Assert.True((await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Sell, 300)).Succeeded);
        Assert.Equal(RiftContractError.AlreadyPosted,
            (await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Hire, 300)).Error);

        // Everyone else in the realm hears of it.
        var letters = await _db.Letters.Where(l => l.Kind == nameof(LetterKind.RiftContractPosted)).ToListAsync();
        Assert.DoesNotContain(letters, l => l.UserId == TestIds.Player);
        Assert.Equal("Sell:300", letters.Single(l => l.UserId == TestIds.Rival).Detail);

        var view = (await Rifts().ViewAsync(_realm, TestIds.Rival)).Live.Single();
        Assert.Equal(RiftContractKind.Sell, view.Contract!.Kind);
        Assert.Equal(300, view.Contract.Price);
        Assert.False(view.Contract.Mine);
    }

    [Fact]
    public async Task AGreatRift_TakesNoContract()
    {
        var lesser = await RealmAsync();
        var great = new Rift
        {
            GameInstanceId = _realm, SeasonNumber = 1, Kind = RiftKind.Great, EventNumber = 1, RegionId = "r2", Stage = RiftStage.Open,
            OmenAt = lesser.OmenAt, OpensAt = lesser.OpensAt, ClosesAt = lesser.ClosesAt, BreachMax = 100, Breach = 100,
        };
        _db.Rifts.Add(great);
        await _db.SaveChangesAsync();
        Assert.Equal(RiftContractError.CannotPost,
            (await Rifts().PostContractAsync(_realm, TestIds.Player, great.Id, RiftContractKind.Sell, 300)).Error);
    }

    // -----------------------------------------------------------------
    // Sell and Hire
    // -----------------------------------------------------------------

    [Fact]
    public async Task ASell_TheTakerPays_HoldsTheRift_AndClosingItPaysTheLord()
    {
        var rift = await RealmAsync(RiftStage.Invaded);
        await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Sell, 300);

        Assert.Equal(RiftContractError.OwnContract, (await Rifts().TakeContractAsync(_realm, TestIds.Player, rift.Id)).Error);
        Assert.True((await Rifts().TakeContractAsync(_realm, TestIds.Rival, rift.Id)).Succeeded);
        Assert.Equal(700, await GoldOf(TestIds.Rival));
        Assert.Equal(300, (await ContractAsync(rift.Id)).Escrow);
        Assert.Single(_db.Letters.Where(l => l.UserId == TestIds.Player && l.Kind == nameof(LetterKind.RiftContractTaken)));

        // The lord's hands are tied while the hold stands.
        Assert.Equal(PveError.NotPveTarget, (await BeginAsync(rift, TestIds.Player)).Error);

        long runGold = await WinAsync(rift, TestIds.Rival);
        var contract = await ContractAsync(rift.Id);
        Assert.Equal(RiftContractStatus.Fulfilled, contract.Status);
        Assert.Equal(0, contract.Escrow);
        Assert.Equal(1300, await GoldOf(TestIds.Player));
        Assert.Equal(700 + runGold, await GoldOf(TestIds.Rival));

        // The closer gets the chest.
        var share = await _db.RiftContributions.SingleAsync(c => c.RiftId == rift.Id);
        Assert.Equal(TestIds.Rival, share.UserId);
        Assert.NotNull(share.ChestRarity);
        Assert.Equal("Sell:300", (await _db.Letters.SingleAsync(l => l.Kind == nameof(LetterKind.RiftContractPaid))).Detail);
    }

    [Fact]
    public async Task AHire_TheLordPaysUpFront_AndTheCloserIsPaid()
    {
        var rift = await RealmAsync();
        await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Hire, 400);
        Assert.Equal(600, await GoldOf(TestIds.Player));

        Assert.True((await Rifts().TakeContractAsync(_realm, TestIds.Rival, rift.Id)).Succeeded);
        Assert.Equal(1000, await GoldOf(TestIds.Rival));

        long runGold = await WinAsync(rift, TestIds.Rival);
        Assert.Equal(1000 + runGold + 400, await GoldOf(TestIds.Rival));
        Assert.Equal(600, await GoldOf(TestIds.Player));
        Assert.Equal(RiftContractStatus.Fulfilled, (await ContractAsync(rift.Id)).Status);
    }

    [Fact]
    public async Task ATakerWhoCannotPay_LeavesItUp()
    {
        var rift = await RealmAsync();
        await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Sell, 2000);
        Assert.Equal(RiftContractError.InsufficientGold, (await Rifts().TakeContractAsync(_realm, TestIds.Rival, rift.Id)).Error);

        var contract = await ContractAsync(rift.Id);
        Assert.Equal(RiftContractStatus.Posted, contract.Status);
        Assert.Null(contract.HolderUserId);
        Assert.Equal(1000, await GoldOf(TestIds.Rival));
    }

    // -----------------------------------------------------------------
    // Every other ending hands the gold back
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheLord_WithdrawsAnUntakenHire_AndHasTheFeeBack_ButNotATakenOne()
    {
        var rift = await RealmAsync();
        await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Hire, 400);
        Assert.True((await Rifts().WithdrawContractAsync(_realm, TestIds.Player, rift.Id)).Succeeded);
        Assert.Equal(1000, await GoldOf(TestIds.Player));
        Assert.Equal(RiftContractStatus.Withdrawn, (await ContractAsync(rift.Id)).Status);

        await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Hire, 400);
        await Rifts().TakeContractAsync(_realm, TestIds.Rival, rift.Id);
        Assert.Equal(RiftContractError.Held, (await Rifts().WithdrawContractAsync(_realm, TestIds.Player, rift.Id)).Error);
    }

    [Fact]
    public async Task AHoldThatRunsOut_GoesBackUp_AndTheTakerHasTheirPriceBack()
    {
        var rift = await RealmAsync();
        await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Sell, 300);
        await Rifts().TakeContractAsync(_realm, TestIds.Rival, rift.Id);

        var row = await _db.RiftContracts.FirstAsync(c => c.RiftId == rift.Id);
        row.HeldUntil = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();
        await Rifts().AdvanceAsync(_realm);

        var contract = await ContractAsync(rift.Id);
        Assert.Equal(RiftContractStatus.Posted, contract.Status);
        Assert.Null(contract.HolderUserId);
        Assert.Equal(0, contract.Escrow);
        Assert.Equal(1000, await GoldOf(TestIds.Rival));
        Assert.Equal("Sell:300:lapsed",
            (await _db.Letters.SingleAsync(l => l.UserId == TestIds.Rival && l.Kind == nameof(LetterKind.RiftContractEnded))).Detail);

        // And the lord may close it again.
        Assert.True((await BeginAsync(rift, TestIds.Player)).Succeeded);
    }

    [Fact]
    public async Task AHolderAlreadyFighting_KeepsTheHoldPastItsTime()
    {
        var rift = await RealmAsync();
        await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Sell, 300);
        await Rifts().TakeContractAsync(_realm, TestIds.Rival, rift.Id);
        Assert.True((await BeginAsync(rift, TestIds.Rival)).Succeeded);

        var row = await _db.RiftContracts.FirstAsync(c => c.RiftId == rift.Id);
        row.HeldUntil = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();
        await Rifts().AdvanceAsync(_realm);

        Assert.Equal(RiftContractStatus.Taken, (await ContractAsync(rift.Id)).Status);
    }

    [Fact]
    public async Task TheLordClosingItThemselves_EndsAnUntakenHire_AndTheFeeComesBack()
    {
        var rift = await RealmAsync();
        await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Hire, 400);
        long runGold = await WinAsync(rift, TestIds.Player);

        Assert.Equal(RiftContractStatus.Void, (await ContractAsync(rift.Id)).Status);
        Assert.Equal(1000 + runGold, await GoldOf(TestIds.Player));
    }

    [Fact]
    public async Task TheLandChangingHands_EndsTheContract_AndTheGoldGoesBack()
    {
        var rift = await RealmAsync();
        await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Sell, 300);
        await Rifts().TakeContractAsync(_realm, TestIds.Rival, rift.Id);

        var world = await _db.WorldViewGameData.SingleAsync(w => w.GameInstanceId == _realm);
        var node = System.Text.Json.Nodes.JsonNode.Parse(world.GameData);
        WorldRegionBlob.CaptureRegion(WorldRegionBlob.FindRegion(node, "r1")!, TestIds.Owner, TestIds.Owner);
        world.GameData = node!.ToJsonString();
        await _db.SaveChangesAsync();
        await Rifts().AdvanceAsync(_realm);

        Assert.Equal(RiftContractStatus.Void, (await ContractAsync(rift.Id)).Status);
        Assert.Equal(1000, await GoldOf(TestIds.Rival));
    }

    [Fact]
    public async Task TheSeasonsEnd_HandsBackEveryHeldPurse_AndClearsTheContracts()
    {
        var rift = await RealmAsync();
        await Rifts().PostContractAsync(_realm, TestIds.Player, rift.Id, RiftContractKind.Hire, 400);

        await RiftService.ResetRealmAsync(_db, _realm);
        await _db.SaveChangesAsync();
        await RiftService.ExpireContractsAsync(_db, Gold, _log, _realm);

        Assert.Empty(_db.RiftContracts);
        Assert.Equal(1000, await GoldOf(TestIds.Player));
    }
}
