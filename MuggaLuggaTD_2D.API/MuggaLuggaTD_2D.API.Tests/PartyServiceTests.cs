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
/// Companies (<c>docs/design/parties-and-travel.md</c>, Unity repo, phase 1): how many a player may
/// lead, who may join one, and what wins over membership.
///
/// <para>What is pinned is that a company is a standing arrangement and never a way round a
/// commitment: a garrisoned, captive or sieging character is in no company and fights no run.</para>
/// </summary>
public class PartyServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeGameContent _content = new();

    private GoldService Gold => new(_db, new FakeSessionLog(), NullLogger<GoldService>.Instance);

    private MaterialWalletService Wallet =>
        new(_db, new FakeSessionLog(), NullLogger<MaterialWalletService>.Instance);

    private TavernService Tavern =>
        new(_db, _content, Wallet, Gold, new FakeSessionLog(), NullLogger<TavernService>.Instance);

    private PartyService Service =>
        new(_db, Tavern, _content, Wallet, Gold, new FakeSessionLog(), NullLogger<PartyService>.Instance) { Dice = _dice };

    private WorldGarrisonService Garrison => new(
        _db, _content, Gold, new FakeSessionLog(), NullLogger<WorldGarrisonService>.Instance);

    private WorldPveService Pve =>
        new(_db, _content, Wallet, Gold, Tavern, NullLogger<WorldPveService>.Instance);

    private static string Contract => SharedContract.Version;

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// A realm where the player holds one region (their capital) and has six characters, the first
    /// three of them in the save's active party.
    /// </summary>
    private async Task<(Guid Instance, string Keep)> SeedAsync(int purchasedSlots = 0)
    {
        var instance = await _db.AddInstanceAsync();
        var region = TestWorld.OwnedBy(TestIds.Player, "r1");
        region.IsCapital = true;
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(region));

        var save = TestSave.Roster(Enumerable.Range(1, 6).Select(i => TestSave.Character($"hero-{i}")).ToArray());
        save.ActiveCharacterIds = new List<string> { "hero-1", "hero-2", "hero-3" };
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, TestSave.ToJson(save));

        if (purchasedSlots > 0)
        {
            _db.TavernStates.Add(new TavernState
            {
                GameInstanceId = instance.Id, UserId = TestIds.Player, PurchasedRosterSlots = purchasedSlots
            });
            await _db.SaveChangesAsync();
        }

        return (instance.Id, TestWorld.KeepIn(region));
    }

    private static PartyCreateRequest Form(params string[] ids) => new(null, null, ids.ToList(), Contract);

    private static PartyUpdateRequest Man(params string[] ids) => new(null, null, ids.ToList(), Contract);

    // -----------------------------------------------------------------
    // The first company
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheFirstCompanyIsThePartyThePlayerAlreadyHad()
    {
        // Nothing changes for a player who never forms a second: their active party is their company.
        var (instance, keep) = await SeedAsync();

        var (outcome, response) = await Service.ListAsync(instance, TestIds.Player);

        Assert.True(outcome.Succeeded, outcome.Message);
        var first = Assert.Single(response!.Parties);
        Assert.Equal(CompanyRules.DefaultName(1), first.Name);
        Assert.Equal(new[] { "hero-1", "hero-2", "hero-3" }, first.CharacterIds);
        Assert.Equal(CompanyState.Idle, first.State);
        Assert.Equal("r1", first.RegionId);
        Assert.Equal(keep, first.SiteId);
    }

    [Fact]
    public async Task TheFirstCompanyIsFormedOnce()
    {
        var (instance, _) = await SeedAsync();

        await Service.ListAsync(instance, TestIds.Player);
        await Service.ListAsync(instance, TestIds.Player);

        Assert.Single(await _db.PlayerParties.ToListAsync());
    }

    [Fact]
    public async Task TheFirstCompanyLeavesOutAnyoneAlreadyGarrisoned()
    {
        var (instance, keep) = await SeedAsync();
        var (_, _, world) = await Garrison.SetAsync(instance, TestIds.Player,
            new GarrisonRequest(keep, new List<string> { "hero-2" }, Contract));
        await PersistAsync(instance, world);

        var (_, response) = await Service.ListAsync(instance, TestIds.Player);

        Assert.Equal(new[] { "hero-1", "hero-3" }, response!.Parties[0].CharacterIds);
        Assert.Contains(response.Commitments, c => c.CharacterId == "hero-2" && c.Reason == "garrisoned");
    }

    // -----------------------------------------------------------------
    // How many
    // -----------------------------------------------------------------

    [Fact]
    public async Task APlayerLeadsOneCompanyForEveryFourRosterSlots()
    {
        // One region held: a cap of 11, so two companies. The third is refused.
        var (instance, _) = await SeedAsync();

        var (second, response) = await Service.CreateAsync(instance, TestIds.Player, Form("hero-4"));
        Assert.True(second.Succeeded, second.Message);
        Assert.Equal(2, response!.MaxParties);

        var (third, _) = await Service.CreateAsync(instance, TestIds.Player, Form("hero-5"));
        Assert.Equal(PartyError.TooManyCompanies, third.Error);
        Assert.Equal(2, await _db.PlayerParties.CountAsync());
    }

    [Fact]
    public async Task BoughtRosterSlotsBuyCompaniesToo()
    {
        // 11 + 6 bought = 17, so four.
        var (instance, _) = await SeedAsync(purchasedSlots: 6);

        var (_, response) = await Service.ListAsync(instance, TestIds.Player);

        Assert.Equal(4, response!.MaxParties);
    }

    // -----------------------------------------------------------------
    // Who may join
    // -----------------------------------------------------------------

    [Fact]
    public async Task ACharacterMarchesWithOneCompanyAtATime()
    {
        var (instance, _) = await SeedAsync();

        var (outcome, _) = await Service.CreateAsync(instance, TestIds.Player, Form("hero-1"));

        Assert.Equal(PartyError.InAnotherCompany, outcome.Error);
        Assert.Single(await _db.PlayerParties.ToListAsync());
    }

    [Fact]
    public async Task OnlyThePlayersOwnCharactersCanJoin()
    {
        var (instance, _) = await SeedAsync();
        var (_, list) = await Service.ListAsync(instance, TestIds.Player);

        var (outcome, _) = await Service.UpdateAsync(instance, TestIds.Player, list!.Parties[0].Id, Man("someone-else"));

        Assert.Equal(PartyError.NotYourCharacter, outcome.Error);
    }

    [Fact]
    public async Task ACompanyIsAtMostFour()
    {
        var (instance, _) = await SeedAsync();
        var (_, list) = await Service.ListAsync(instance, TestIds.Player);

        var (outcome, _) = await Service.UpdateAsync(instance, TestIds.Player, list!.Parties[0].Id,
            Man("hero-1", "hero-2", "hero-3", "hero-4", "hero-5"));

        Assert.Equal(PartyError.TooManyMembers, outcome.Error);
    }

    [Fact]
    public async Task AGarrisonedCharacterCannotJoinACompany()
    {
        var (instance, keep) = await SeedAsync();
        var (_, _, world) = await Garrison.SetAsync(instance, TestIds.Player,
            new GarrisonRequest(keep, new List<string> { "hero-4" }, Contract));
        await PersistAsync(instance, world);

        var (outcome, _) = await Service.CreateAsync(instance, TestIds.Player, Form("hero-4"));

        Assert.Equal(PartyError.CharacterCommitted, outcome.Error);
    }

    [Fact]
    public async Task StationingAGarrisonTakesItsMembersOutOfTheirCompany()
    {
        // A company can be what's assigned to a garrison's defence; the garrison wins, and the company
        // is left with whoever was not stationed.
        var (instance, keep) = await SeedAsync();
        await Service.ListAsync(instance, TestIds.Player);

        await Garrison.SetAsync(instance, TestIds.Player,
            new GarrisonRequest(keep, new List<string> { "hero-1", "hero-2" }, Contract));

        var party = await _db.PlayerParties.SingleAsync();
        Assert.Equal(new[] { "hero-3" }, MarchingArmy.ReadIds(party.CharacterIdsJson));
    }

    [Fact]
    public async Task ACompanyCanBeRenamedAndRecoloured()
    {
        var (instance, _) = await SeedAsync();
        var (_, list) = await Service.ListAsync(instance, TestIds.Player);

        var (outcome, response) = await Service.UpdateAsync(instance, TestIds.Player, list!.Parties[0].Id,
            new PartyUpdateRequest("  The Iron Oath ", "#AA3322", null, Contract));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("The Iron Oath", response!.Parties[0].Name);
        Assert.Equal("#aa3322", response.Parties[0].Banner);
        Assert.Equal(3, response.Parties[0].CharacterIds.Count);
    }

    [Fact]
    public async Task ABannerMustBeAColour()
    {
        var (instance, _) = await SeedAsync();
        var (_, list) = await Service.ListAsync(instance, TestIds.Player);

        var (outcome, _) = await Service.UpdateAsync(instance, TestIds.Player, list!.Parties[0].Id,
            new PartyUpdateRequest(null, "red; drop table", null, Contract));

        Assert.Equal(PartyError.BadBanner, outcome.Error);
    }

    // -----------------------------------------------------------------
    // Disbanding
    // -----------------------------------------------------------------

    [Fact]
    public async Task APlayerAlwaysKeepsOneCompany()
    {
        var (instance, _) = await SeedAsync();
        var (_, list) = await Service.ListAsync(instance, TestIds.Player);

        var (outcome, _) = await Service.DisbandAsync(instance, TestIds.Player, list!.Parties[0].Id);

        Assert.Equal(PartyError.LastCompany, outcome.Error);
    }

    [Fact]
    public async Task ACompanyAwayFromHomeCannotBeDisbanded()
    {
        var (instance, _) = await SeedAsync();
        var (_, formed) = await Service.CreateAsync(instance, TestIds.Player, Form("hero-4"));
        var second = await _db.PlayerParties.SingleAsync(p => p.Id == formed!.Parties[1].Id);
        second.State = CompanyState.Travelling;
        await _db.SaveChangesAsync();

        var (outcome, _) = await Service.DisbandAsync(instance, TestIds.Player, second.Id);

        Assert.Equal(PartyError.Busy, outcome.Error);
    }

    [Fact]
    public async Task DisbandingFreesItsMembers()
    {
        var (instance, _) = await SeedAsync();
        var (_, formed) = await Service.CreateAsync(instance, TestIds.Player, Form("hero-4"));

        var (outcome, response) = await Service.DisbandAsync(instance, TestIds.Player, formed!.Parties[1].Id);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Single(response!.Parties);

        var (again, _) = await Service.CreateAsync(instance, TestIds.Player, Form("hero-4"));
        Assert.True(again.Succeeded, again.Message);
    }

    // -----------------------------------------------------------------
    // Travel (1.33.0)
    // -----------------------------------------------------------------

    [Fact]
    public async Task ACompanyIsSentDownTheRoadAndTakesOneToFiveMinutes()
    {
        var (instance, keep) = await SeedWithDungeonAsync();
        var first = await FirstAsync(instance);

        var (outcome, response) = await Service.TravelAsync(instance, TestIds.Player, first.Id, Travel(DungeonId));

        Assert.True(outcome.Succeeded, outcome.Message);
        var dto = response!.Parties[0];
        Assert.Equal(CompanyState.Travelling, dto.State);
        Assert.Null(dto.SiteId);
        Assert.NotNull(dto.Journey);
        Assert.Equal(keep, dto.Journey!.FromSiteId);
        Assert.Equal(DungeonId, dto.Journey.ToSiteId);

        var took = dto.Journey.ArrivesAt - dto.Journey.DepartedAt;
        Assert.InRange(took.TotalSeconds, TravelRules.MinimumJourney.TotalSeconds - 1, TravelRules.MaximumJourney.TotalSeconds + 1);

        // The route runs from the keep's cell to the dungeon's, and its clock reads the same total.
        var region = TestWorld.ReadRegion(await _db.ReadWorldAsync(instance), "r1");
        var layout = RegionGenerator.Generate(region);
        var from = layout.FindSite(keep)!.Cell;
        var to = layout.FindSite(DungeonId)!.Cell;
        Assert.Equal(new[] { from.X, from.Y }, dto.Journey.Cells[0]);
        Assert.Equal(new[] { to.X, to.Y }, dto.Journey.Cells[^1]);
        Assert.Equal(dto.Journey.Cells.Count, dto.Journey.Seconds.Count);
        Assert.InRange(dto.Journey.Seconds[^1], took.TotalSeconds - 1, took.TotalSeconds + 1);
    }

    [Fact]
    public async Task ACompanyArrivesWhenItsTimeIsUp_NoticedByTheNextRead()
    {
        var (instance, _) = await SeedWithDungeonAsync();
        var first = await FirstAsync(instance);
        await Service.TravelAsync(instance, TestIds.Player, first.Id, Travel(DungeonId));
        await BackdateJourneyAsync(first.Id);

        var (_, response) = await Service.ListAsync(instance, TestIds.Player);

        var dto = response!.Parties[0];
        Assert.Equal(CompanyState.Idle, dto.State);
        Assert.Equal(DungeonId, dto.SiteId);
        Assert.Null(dto.Journey);
    }

    [Fact]
    public async Task ACompanyOnTheRoadCannotBeSentAgainOrRemanned()
    {
        var (instance, _) = await SeedWithDungeonAsync();
        var first = await FirstAsync(instance);
        await Service.TravelAsync(instance, TestIds.Player, first.Id, Travel(DungeonId));

        var (again, _) = await Service.TravelAsync(instance, TestIds.Player, first.Id, Travel(DungeonId));
        var (reman, _) = await Service.UpdateAsync(instance, TestIds.Player, first.Id, Man("hero-1"));
        var (rename, _) = await Service.UpdateAsync(instance, TestIds.Player, first.Id,
            new PartyUpdateRequest("On The Road", null, null, Contract));

        Assert.Equal(PartyError.Busy, again.Error);
        Assert.Equal(PartyError.Busy, reman.Error);
        Assert.True(rename.Succeeded, rename.Message);
    }

    [Fact]
    public async Task ACompanyCanCrossIntoANeighbouringRegion_ArrivingByItsRoad()
    {
        // Until marching between regions is walked on the map (phase 4), a crossing is a minute of
        // travel and then the company is seen coming in by the road facing home.
        var instance = await _db.AddInstanceAsync();
        var home = TestWorld.OwnedBy(TestIds.Player, "r1");
        home.IsCapital = true;
        var next = TestWorld.Region("r2", q: 1, r: 0);
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(home, next));
        var save = TestSave.Roster(TestSave.Character("hero-1"));
        save.ActiveCharacterIds = new List<string> { "hero-1" };
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, TestSave.ToJson(save));
        var first = await FirstAsync(instance.Id);
        var target = TestWorld.DungeonIn(next);

        var (outcome, response) = await Service.TravelAsync(instance.Id, TestIds.Player, first.Id, Travel(target));

        Assert.True(outcome.Succeeded, outcome.Message);
        var dto = response!.Parties[0];
        Assert.Equal("r2", dto.RegionId);
        Assert.Equal(target, dto.Journey!.ToSiteId);
        Assert.True(dto.Journey.Seconds[0] > 0, "the crossing comes before the first cell");
        Assert.InRange((dto.Journey.ArrivesAt - dto.Journey.DepartedAt).TotalMinutes, 0.99, 5.01);

        // r2 lies east of r1, so the company comes in from r2's west side.
        Assert.Equal(RegionSide.West, TravelRules.SideFacing(next.Hex, home.Hex));
    }

    [Fact]
    public async Task ACompanyIsAlreadyWhereItStands()
    {
        var (instance, keep) = await SeedWithDungeonAsync();
        var first = await FirstAsync(instance);

        var (outcome, _) = await Service.TravelAsync(instance, TestIds.Player, first.Id, Travel(keep));

        Assert.Equal(PartyError.AlreadyThere, outcome.Error);
    }

    // -----------------------------------------------------------------
    // Who fights a run: the company standing there
    // -----------------------------------------------------------------

    [Fact]
    public async Task YouFightWhereYouStand()
    {
        // The company stands at the keep; the dungeon is down the road.
        var (instance, _) = await SeedWithDungeonAsync();
        var first = await FirstAsync(instance);

        var (outcome, _) = await Pve.BeginAsync(instance, TestIds.Player, Enter(first.Id));

        Assert.Equal(PveError.NoCompanyThere, outcome.Error);
        Assert.Empty(await _db.PveRuns.ToListAsync());
    }

    [Fact]
    public async Task ACompanyStillOnTheRoadCannotFight()
    {
        var (instance, _) = await SeedWithDungeonAsync();
        var first = await FirstAsync(instance);
        await Service.TravelAsync(instance, TestIds.Player, first.Id, Travel(DungeonId));

        var (outcome, _) = await Pve.BeginAsync(instance, TestIds.Player, Enter(first.Id));

        Assert.Equal(PveError.NoCompanyThere, outcome.Error);
    }

    [Fact]
    public async Task ACompanyThatHasArrivedFights_AndTheRunRecordsWhoWentIn()
    {
        var (instance, _) = await SeedWithDungeonAsync();
        var first = await FirstAsync(instance);
        await Service.TravelAsync(instance, TestIds.Player, first.Id, Travel(DungeonId));
        await BackdateJourneyAsync(first.Id);

        var (outcome, _) = await Pve.BeginAsync(instance, TestIds.Player, Enter(first.Id));

        Assert.True(outcome.Succeeded, outcome.Message);
        var run = await _db.PveRuns.SingleAsync();
        Assert.Equal(new[] { "hero-1", "hero-2", "hero-3" }, MarchingArmy.ReadIds(run.FighterIdsJson));
    }

    [Fact]
    public async Task ARunNeedsACompany()
    {
        var (instance, _) = await SeedWithDungeonAsync();

        var (outcome, _) = await Pve.BeginAsync(instance, TestIds.Player,
            new PveBeginRequest(DungeonId, Contract, new List<string> { "hero-1" }));

        Assert.Equal(PveError.NoCompanyThere, outcome.Error);
    }

    [Fact]
    public async Task ASiegeArmyCannotSlipOffToRunADungeon()
    {
        // Before 1.32.0 a run asked nothing about who was in it, so an army locked into a siege could
        // clear dungeons in the meantime - and a company is no way round that.
        var (instance, _) = await SeedWithDungeonAsync();
        var first = await StandAtDungeonAsync(instance);
        _db.Sieges.Add(new Siege
        {
            GameInstanceId = instance, AttackerUserId = TestIds.Player, DefenderUserId = TestIds.Rival,
            RegionId = "r9", ArmyCharacterIdsJson = MarchingArmy.WriteIds(new[] { "hero-1" }),
            State = SiegeState.Mustering, MusterEndsAt = DateTime.UtcNow.AddHours(8),
        });
        await _db.SaveChangesAsync();

        var (outcome, _) = await Pve.BeginAsync(instance, TestIds.Player, Enter(first.Id));

        Assert.Equal(PveError.FightersUnavailable, outcome.Error);
        Assert.Empty(await _db.PveRuns.ToListAsync());
    }

    [Fact]
    public async Task AGarrisonedHeroStaysBehindWhenTheirCompanyFights()
    {
        // Stationing takes a hero out of their company, so the company fights without them.
        var (instance, keep) = await SeedWithDungeonAsync();
        var (_, _, world) = await Garrison.SetAsync(instance, TestIds.Player,
            new GarrisonRequest(keep, new List<string> { "hero-2" }, Contract));
        await PersistAsync(instance, world);
        var first = await StandAtDungeonAsync(instance);

        var (outcome, _) = await Pve.BeginAsync(instance, TestIds.Player, Enter(first.Id));

        Assert.True(outcome.Succeeded, outcome.Message);
        var run = await _db.PveRuns.SingleAsync();
        Assert.Equal(new[] { "hero-1", "hero-3" }, MarchingArmy.ReadIds(run.FighterIdsJson));
    }

    // -----------------------------------------------------------------
    // Ambushes (1.34.0)
    // -----------------------------------------------------------------

    /// <summary>A Random that always rolls the same: 0 ambushes every road (at its earliest), 0.999 none.</summary>
    private sealed class FixedDice : Random
    {
        private readonly double _value;
        public FixedDice(double value) => _value = value;
        public override double NextDouble() => _value;
        protected override double Sample() => _value;
        public override int Next(int maxValue) => 0;
        public override int Next(int minValue, int maxValue) => minValue;
    }

    private Random _dice = new FixedDice(0.999);

    private async Task<(Guid Instance, string Keep, PartyDto Company)> AmbushedAsync()
    {
        _dice = new FixedDice(0.0);
        var (instance, keep) = await SeedWithDungeonAsync();
        var first = await FirstAsync(instance);
        await Service.TravelAsync(instance, TestIds.Player, first.Id, Travel(DungeonId));

        // Half-way along: past the ambush, which strikes a quarter of the way.
        var party = await _db.PlayerParties.SingleAsync(p => p.Id == first.Id);
        var took = party.ArrivesAt!.Value - party.DepartedAt!.Value;
        party.DepartedAt = DateTime.UtcNow - took / 2;
        party.ArrivesAt = party.DepartedAt + took;
        await _db.SaveChangesAsync();

        var (_, response) = await Service.ListAsync(instance, TestIds.Player);
        return (instance, keep, response!.Parties[0]);
    }

    private static AmbushOrderRequest Order => new(Contract);

    private async Task BackdateAmbushRunAsync(Guid runId)
    {
        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow - TimeSpan.FromMinutes(2);
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnAmbushHaltsTheCompanyPartWayAlongItsRoad()
    {
        var (_, _, company) = await AmbushedAsync();

        Assert.Equal(CompanyState.Ambushed, company.State);
        Assert.NotNull(company.Journey);
        Assert.NotNull(company.Journey!.HaltedAt);
        Assert.NotNull(company.Ambush);
        Assert.Equal(AmbushRules.SkirmishTier, company.Ambush!.Tier);
        Assert.True(company.Ambush.Waves > 0);
    }

    [Fact]
    public async Task AnAmbushIsRolledOnDeparture_AndNobodyIsToldAheadOfTime()
    {
        var (instance, _) = await SeedWithDungeonAsync();
        var first = await FirstAsync(instance);
        _dice = new FixedDice(0.0);

        var (_, response) = await Service.TravelAsync(instance, TestIds.Player, first.Id, Travel(DungeonId));

        // Rolled, stored - and not sent: the client learns of an ambush when it strikes.
        var stored = await _db.PlayerParties.SingleAsync(p => p.Id == first.Id);
        Assert.NotNull(stored.AmbushAt);
        var dto = response!.Parties[0];
        Assert.Equal(CompanyState.Travelling, dto.State);
        Assert.Null(dto.Journey!.HaltedAt);
        Assert.Null(dto.Ambush);
    }

    [Fact]
    public async Task AQuietRollLeavesTheRoadQuiet()
    {
        var (instance, _) = await SeedWithDungeonAsync();
        var first = await FirstAsync(instance);

        await Service.TravelAsync(instance, TestIds.Player, first.Id, Travel(DungeonId));

        Assert.Null((await _db.PlayerParties.SingleAsync(p => p.Id == first.Id)).AmbushAt);
    }

    [Fact]
    public async Task AnAmbushedCompanyCannotBeSentOnOrFightAtASite()
    {
        var (instance, _, company) = await AmbushedAsync();

        var (sent, _) = await Service.TravelAsync(instance, TestIds.Player, company.Id, Travel(DungeonId));
        var (entered, _) = await Pve.BeginAsync(instance, TestIds.Player, Enter(company.Id));

        Assert.Equal(PartyError.Busy, sent.Error);
        Assert.Equal(PveError.NoCompanyThere, entered.Error);
    }

    [Fact]
    public async Task FleeingWalksTheCompanyBackToWhereItSetOut()
    {
        var (instance, keep, company) = await AmbushedAsync();

        var (outcome, response) = await Service.FleeAmbushAsync(instance, TestIds.Player, company.Id, Order);

        Assert.True(outcome.Succeeded, outcome.Message);
        var dto = response!.Parties[0];
        Assert.Equal(CompanyState.Returning, dto.State);
        Assert.Equal(keep, dto.Journey!.ToSiteId);
        Assert.Equal(0, dto.Journey.Seconds[0]);
        Assert.True(dto.Journey.ArrivesAt > DateTime.UtcNow);

        await BackdateJourneyAsync(company.Id);
        var (_, after) = await Service.ListAsync(instance, TestIds.Player);
        Assert.Equal(CompanyState.Idle, after!.Parties[0].State);
        Assert.Equal(keep, after.Parties[0].SiteId);
    }

    [Fact]
    public async Task WinningAnAmbushPays_AndTheCompanyMarchesOnWithoutBeingStoppedTwice()
    {
        var (instance, _, company) = await AmbushedAsync();
        var (fought, opened) = await Service.FightAmbushAsync(instance, TestIds.Player, company.Id, Order);
        Assert.True(fought.Succeeded, fought.Message);
        await BackdateAmbushRunAsync(opened!.RunId);

        var (outcome, claim) = await Service.ClaimAmbushAsync(instance, TestIds.Player, company.Id,
            new AmbushClaimRequest(opened.RunId, true, Contract));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(claim!.Won);
        Assert.True(claim.Experience > 0);
        Assert.Equal(GoldRules.GoldForClear(claim.Experience), claim.Gold);
        Assert.Equal(CompanyState.Travelling, claim.Parties.Parties[0].State);

        var stored = await _db.PlayerParties.SingleAsync(p => p.Id == company.Id);
        Assert.Null(stored.AmbushAt);
        Assert.True(stored.ArrivesAt > DateTime.UtcNow, "the halt is added to the journey");

        await BackdateJourneyAsync(company.Id);
        var (_, after) = await Service.ListAsync(instance, TestIds.Player);
        Assert.Equal(CompanyState.Idle, after!.Parties[0].State);
        Assert.Equal(DungeonId, after.Parties[0].SiteId);

        // The run is spent.
        var (again, _) = await Service.ClaimAmbushAsync(instance, TestIds.Player, company.Id,
            new AmbushClaimRequest(opened.RunId, true, Contract));
        Assert.False(again.Succeeded);
    }

    [Fact]
    public async Task LosingAnAmbushPaysNothing_AndTurnsTheCompanyBack()
    {
        var (instance, keep, company) = await AmbushedAsync();
        var (_, opened) = await Service.FightAmbushAsync(instance, TestIds.Player, company.Id, Order);

        var (outcome, claim) = await Service.ClaimAmbushAsync(instance, TestIds.Player, company.Id,
            new AmbushClaimRequest(opened!.RunId, false, Contract));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.False(claim!.Won);
        Assert.Equal(0, claim.Experience);
        Assert.Equal(0, claim.Gold);
        Assert.Equal(CompanyState.Returning, claim.Parties.Parties[0].State);
        Assert.Equal(keep, claim.Parties.Parties[0].Journey!.ToSiteId);
    }

    [Fact]
    public async Task AnAmbushWonImplausiblyFastIsRefused()
    {
        var (instance, _, company) = await AmbushedAsync();
        var (_, opened) = await Service.FightAmbushAsync(instance, TestIds.Player, company.Id, Order);

        var (outcome, _) = await Service.ClaimAmbushAsync(instance, TestIds.Player, company.Id,
            new AmbushClaimRequest(opened!.RunId, true, Contract));

        Assert.Equal(PartyError.RunTooFast, outcome.Error);
        Assert.Equal(CompanyState.Ambushed, (await _db.PlayerParties.SingleAsync(p => p.Id == company.Id)).State);
    }

    [Fact]
    public async Task OnlyAnAmbushedCompanyCanFightOrFlee()
    {
        var (instance, _) = await SeedWithDungeonAsync();
        var first = await FirstAsync(instance);

        var (fight, _) = await Service.FightAmbushAsync(instance, TestIds.Player, first.Id, Order);
        var (flee, _) = await Service.FleeAmbushAsync(instance, TestIds.Player, first.Id, Order);

        Assert.Equal(PartyError.NotAmbushed, fight.Error);
        Assert.Equal(PartyError.NotAmbushed, flee.Error);
    }

    [Fact]
    public void AmbushOddsRiseWithTierUnheldLandAndLength_AndAreCapped()
    {
        var minute = TimeSpan.FromMinutes(1);
        double home = AmbushRules.ChanceFor(1, true, minute);
        Assert.True(AmbushRules.ChanceFor(3, true, minute) > home);
        Assert.True(AmbushRules.ChanceFor(1, false, minute) > home);
        Assert.True(AmbushRules.ChanceFor(1, true, TimeSpan.FromMinutes(5)) > home);
        Assert.Equal(AmbushRules.MaximumChance, AmbushRules.ChanceFor(4, false, TimeSpan.FromMinutes(5)));
        Assert.Equal(AmbushRisk.Low, AmbushRules.RiskOf(home));
        Assert.Equal(AmbushRisk.High, AmbushRules.RiskOf(AmbushRules.MaximumChance));
    }

    [Fact]
    public void TheRoadBackIsTheRoadWalked_Reversed()
    {
        var cells = new List<int[]> { new[] { 0, 0 }, new[] { 1, 0 }, new[] { 2, 0 }, new[] { 3, 0 } };
        var seconds = new List<double> { 0, 10, 25, 40 };

        var (back, times) = AmbushRules.RouteBack(cells, seconds, 2.4);

        Assert.Equal(new[] { 2, 1, 0 }, back.Select(c => c[0]));
        Assert.Equal(new double[] { 0, 15, 25 }, times);
    }

    // -----------------------------------------------------------------
    // The rules on their own
    // -----------------------------------------------------------------

    [Fact]
    public void ARouteKeepsToTheRoads_AndProgressFollowsTheClock()
    {
        var region = TestWorld.OwnedBy(TestIds.Player, "r1");
        var layout = RegionGenerator.Generate(region);
        var roads = RegionRoadNetwork.Build(layout, region.Seed);
        var keep = layout.FindSite(TestWorld.KeepIn(region))!.Cell;
        var dungeon = layout.FindSite(TestWorld.DungeonIn(region))!.Cell;

        var journey = TravelRules.Plan(roads, keep, dungeon);

        Assert.NotNull(journey);
        int onRoad = journey!.Cells.Count(roads.IsRoad);
        Assert.True(onRoad >= journey.Cells.Count * 0.6, $"only {onRoad} of {journey.Cells.Count} cells on the road");

        var departed = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        double last = journey.Cells.Count - 1;
        Assert.Equal(0, TravelRules.Progress(journey.CumulativeSeconds, departed, departed));
        Assert.Equal(last, TravelRules.Progress(journey.CumulativeSeconds, departed, departed + journey.Duration), 3);
        double mid = TravelRules.Progress(journey.CumulativeSeconds, departed, departed + journey.Duration / 2);
        Assert.InRange(mid, 0.5, last - 0.5);
    }

    [Fact]
    public void EverySiteIsJoinedToTheRoads_AcrossManyRegions()
    {
        for (int i = 0; i < 40; i++)
        {
            var region = TestWorld.Region($"r{i}", tier: 1 + i % 4);
            var layout = RegionGenerator.Generate(region);
            var roads = RegionRoadNetwork.Build(layout, region.Seed);
            foreach (var site in layout.Sites)
                Assert.True(roads.IsRoad(site.Cell) || layout.Sites.Count == 1,
                    $"{site.SiteId} ({site.Type}) is not on a road in region {region.RegionId}");
        }
    }

    // -----------------------------------------------------------------

    private static PartyTravelRequest Travel(string siteId) => new(siteId, Contract);

    private PveBeginRequest Enter(Guid partyId) => new(DungeonId, Contract, null, partyId);

    private async Task<PartyDto> FirstAsync(Guid instance)
    {
        var (_, response) = await Service.ListAsync(instance, TestIds.Player);
        return response!.Parties[0];
    }

    /// <summary>Puts the journey in the past, as if its minutes had been walked.</summary>
    private async Task BackdateJourneyAsync(Guid partyId)
    {
        var party = await _db.PlayerParties.SingleAsync(p => p.Id == partyId);
        var took = party.ArrivesAt!.Value - party.DepartedAt!.Value;
        party.DepartedAt = DateTime.UtcNow - took - TimeSpan.FromSeconds(1);
        party.ArrivesAt = DateTime.UtcNow - TimeSpan.FromSeconds(1);
        await _db.SaveChangesAsync();
    }

    /// <summary>Sends the first company to the dungeon and lets it arrive.</summary>
    private async Task<PartyDto> StandAtDungeonAsync(Guid instance)
    {
        var first = await FirstAsync(instance);
        var (sent, _) = await Service.TravelAsync(instance, TestIds.Player, first.Id, Travel(DungeonId));
        Assert.True(sent.Succeeded, sent.Message);
        await BackdateJourneyAsync(first.Id);
        return first;
    }

    // -----------------------------------------------------------------

    private string DungeonId = string.Empty;

    /// <summary><see cref="SeedAsync"/>, remembering a dungeon in the player's region to fight in.</summary>
    private async Task<(Guid Instance, string Keep)> SeedWithDungeonAsync()
    {
        var seeded = await SeedAsync();
        var region = TestWorld.ReadRegion(await _db.ReadWorldAsync(seeded.Instance), "r1");
        DungeonId = TestWorld.DungeonIn(region);
        return seeded;
    }

    /// <summary>Writes back the world a service returned, as its controller does.</summary>
    private async Task PersistAsync(Guid instance, System.Text.Json.Nodes.JsonNode? world)
    {
        Assert.NotNull(world);
        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == instance);
        row.GameData = world!.ToJsonString();
        await _db.SaveChangesAsync();
    }
}
