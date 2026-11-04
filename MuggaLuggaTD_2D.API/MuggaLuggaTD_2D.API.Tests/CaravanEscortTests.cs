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
/// Caravans, phase 2 (Active Content F; Mike 2026-10-16): the Escort quest on the board, a company setting out with
/// the caravan from its village, the guaranteed ambush fought as the caravan fight with the wagons still standing, and
/// delivery paying by the share that got through. A loss strikes the quest; a flight brings the caravan home to try
/// again.
/// </summary>
public class CaravanEscortTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();

    private readonly FakeGameContent _content = new()
    {
        DroppableItems = new[] { new ItemTemplate { ItemName = "Cinder Blade", ItemType = ItemTypes.Weapon } }
    };

    public void Dispose() => _db.Dispose();

    private static string Contract => SharedContract.Version;

    private GoldService Gold => new(_db, _log, NullLogger<GoldService>.Instance);
    private MaterialWalletService Wallet => new(_db, _log, NullLogger<MaterialWalletService>.Instance);
    private ItemLedgerService Items => new(_db, _log, NullLogger<ItemLedgerService>.Instance);
    private TavernService Tavern => new(_db, _content, Wallet, Gold, _log, NullLogger<TavernService>.Instance);

    private Random _dice = new FixedDice(0.999);

    private PartyService Parties =>
        new(_db, Tavern, _content, Wallet, Gold, _log, NullLogger<PartyService>.Instance, Items) { Dice = _dice };

    private QuestService Quests => new(_db, _content, Items, Gold, Wallet, _log);

    /// <summary>Always rolls the same: 0 strikes at the earliest and always rolls a second ambush, 0.999 the latest and never.</summary>
    private sealed class FixedDice : Random
    {
        private readonly double _value;
        public FixedDice(double value) => _value = value;
        public override double NextDouble() => _value;
        protected override double Sample() => _value;
        public override int Next(int maxValue) => 0;
        public override int Next(int minValue, int maxValue) => minValue;
    }

    /// <summary>
    /// Three regions in a row: the player's capital r0, then r1 and r2. An Escort quest in the log from r0's village to
    /// r2's, and the first company standing at r0's village, ready to set out.
    /// </summary>
    private async Task<(Guid Instance, PartyDto Company, PlayerQuest Quest, string From, string To)> SeedAsync(int hexes = 2)
    {
        var instance = await _db.AddInstanceAsync();
        var r0 = TestWorld.OwnedBy(TestIds.Player, "r0");
        r0.IsCapital = true;
        var row = new List<WorldRegionData> { r0 };
        for (int q = 1; q <= Math.Max(hexes, 2); q++) row.Add(TestWorld.Region("r" + q, q: q, r: 0));
        var r2 = row[hexes];
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(row.ToArray()));
        var save = TestSave.Roster(TestSave.Character("hero-1"), TestSave.Character("hero-2"));
        save.ActiveCharacterIds = new List<string> { "hero-1", "hero-2" };
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, TestSave.ToJson(save));

        string from = TestWorld.SettlementIn(r0), to = TestWorld.SettlementIn(r2);
        var quest = new PlayerQuest
        {
            GameInstanceId = instance.Id, UserId = TestIds.Player, OfferId = "1.0." + from,
            Offer = new QuestOffer
            {
                Id = "1.0." + from, GiverId = from, RegionId = "r0", Kind = QuestKind.Escort, Target = "Grain", Count = 30,
                DestinationSiteId = to, Level = 5, ChestTier = ItemRarityTypes.Uncommon, Gold = 300,
                Materials = new List<MaterialGrant>
                {
                    new() { MaterialName = QuestRules.Essence, Quantity = 3 },
                    new() { MaterialName = "Grain", Quantity = 6 }
                }
            }
        };
        _db.PlayerQuests.Add(quest);
        await _db.SaveChangesAsync();

        var (_, listed) = await Parties.ListAsync(instance.Id, TestIds.Player);
        var company = listed!.Parties[0];
        var party = await _db.PlayerParties.SingleAsync(p => p.Id == company.Id);
        party.SiteId = from;
        await _db.SaveChangesAsync();
        return (instance.Id, company, quest, from, to);
    }

    private PartyEscortRequest Escort(Guid questId) => new(questId, Contract);

    private static AmbushOrderRequest Order => new(Contract);

    /// <summary>Walks the company on to <paramref name="share"/> of its journey.</summary>
    private async Task WalkToAsync(Guid partyId, double share)
    {
        var party = await _db.PlayerParties.SingleAsync(p => p.Id == partyId);
        var took = party.ArrivesAt!.Value - party.DepartedAt!.Value;
        party.DepartedAt = DateTime.UtcNow - TimeSpan.FromTicks((long)(took.Ticks * share));
        party.ArrivesAt = party.DepartedAt + took;
        await _db.SaveChangesAsync();
    }

    private async Task BackdateRunAsync(Guid runId, double seconds = 120)
    {
        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow - TimeSpan.FromSeconds(seconds);
        await _db.SaveChangesAsync();
    }

    /// <summary>Sets out, walks into the first ambush and opens its fight.</summary>
    private async Task<(Guid Instance, PartyDto Company, PlayerQuest Quest, string From, string To, AmbushFightResponse Fight)> AmbushedAsync(int hexes = 2)
    {
        var (instance, company, quest, from, to) = await SeedAsync(hexes);
        var (sent, _) = await Parties.EscortAsync(instance, TestIds.Player, company.Id, Escort(quest.Id));
        Assert.True(sent.Succeeded, sent.Message);
        await WalkToAsync(company.Id, CaravanRules.FirstAmbushTo + 0.02);
        var (fought, opened) = await Parties.FightAmbushAsync(instance, TestIds.Player, company.Id, Order);
        Assert.True(fought.Succeeded, fought.Message);
        return (instance, company, quest, from, to, opened!);
    }

    // -----------------------------------------------------------------
    // Setting out
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheCaravanWaitsAtItsVillage()
    {
        var (instance, company, quest, _, _) = await SeedAsync();
        var party = await _db.PlayerParties.SingleAsync(p => p.Id == company.Id);
        party.SiteId = TestWorld.KeepIn(TestWorld.OwnedBy(TestIds.Player, "r0"));
        await _db.SaveChangesAsync();

        var (outcome, _) = await Parties.EscortAsync(instance, TestIds.Player, company.Id, Escort(quest.Id));

        Assert.Equal(PartyError.NotAtTheVillage, outcome.Error);
    }

    [Fact]
    public async Task ACaravanSetsOut_Slower_AndIsAlwaysAmbushed()
    {
        var (instance, company, quest, _, to) = await SeedAsync();

        var (outcome, response) = await Parties.EscortAsync(instance, TestIds.Player, company.Id, Escort(quest.Id));

        Assert.True(outcome.Succeeded, outcome.Message);
        var dto = response!.Parties[0];
        Assert.Equal(CompanyState.Travelling, dto.State);
        Assert.Equal(to, dto.Journey!.ToSiteId);
        Assert.Equal(new CaravanDto(quest.Id, CaravanRules.Wagons), dto.Caravan);

        // The ambush is certain, early in the walk; nobody is told yet.
        var stored = await _db.PlayerParties.SingleAsync(p => p.Id == company.Id);
        Assert.InRange(stored.AmbushAt!.Value, CaravanRules.FirstAmbushFrom, CaravanRules.FirstAmbushTo);
        Assert.Null(dto.Ambush);

        // At the caravan's pace: the same road walked alone is quicker.
        var caravanTook = stored.ArrivesAt!.Value - stored.DepartedAt!.Value;
        stored.State = CompanyState.Idle;
        stored.SiteId = stored.FromSiteId;
        stored.CaravanQuestId = null;
        await _db.SaveChangesAsync();
        var (_, alone) = await Parties.TravelAsync(instance, TestIds.Player, company.Id, new PartyTravelRequest(to, Contract));
        var walked = alone!.Parties[0].Journey!;
        Assert.True(caravanTook > (walked.ArrivesAt - walked.DepartedAt) * 1.3, $"caravan {caravanTook} vs alone {walked.ArrivesAt - walked.DepartedAt}");
    }

    [Fact]
    public async Task OneCaravan_OneCompany()
    {
        var (instance, company, quest, from, _) = await SeedAsync();
        await Parties.EscortAsync(instance, TestIds.Player, company.Id, Escort(quest.Id));

        // A second company at the village cannot take the same caravan.
        _db.PlayerParties.Add(new PlayerParty
        {
            GameInstanceId = instance, UserId = TestIds.Player, Name = "Second", CharacterIdsJson = "[\"hero-2\"]",
            SortOrder = 1, RegionId = "r0", SiteId = from
        });
        await _db.SaveChangesAsync();
        var second = await _db.PlayerParties.SingleAsync(p => p.Name == "Second");

        var (outcome, _) = await Parties.EscortAsync(instance, TestIds.Player, second.Id, Escort(quest.Id));

        Assert.Equal(PartyError.CaravanTaken, outcome.Error);
    }

    // -----------------------------------------------------------------
    // The ambush and the fight
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheAmbushIsTheCaravanFight_WithEveryWagon()
    {
        var (_, _, _, _, _, fight) = await AmbushedAsync();

        Assert.Equal(CaravanRules.Wagons, fight.Ambush.CaravanWagons);
        Assert.Equal(CaravanRules.Tier, fight.Ambush.Tier);
        Assert.Equal(CaravanRules.Waves, fight.Ambush.Waves);
    }

    [Fact]
    public async Task ACaravanFightCannotBeWonBeforeItsSetupCouldHavePassed()
    {
        var (instance, company, _, _, _, fight) = await AmbushedAsync();
        await BackdateRunAsync(fight.RunId, CaravanRules.SetupSeconds);

        var (outcome, _) = await Parties.ClaimAmbushAsync(instance, TestIds.Player, company.Id,
            new AmbushClaimRequest(fight.RunId, true, Contract, null, 3, true));

        Assert.Equal(PartyError.RunTooFast, outcome.Error);
    }

    [Fact]
    public async Task TheSurvivorsGoOn_AndDeliveryPaysTheirShare()
    {
        var (instance, company, quest, _, to, fight) = await AmbushedAsync();
        await BackdateRunAsync(fight.RunId);

        // Won with two wagons of three - and a report of more than it had is held to what it had.
        var (outcome, claim) = await Parties.ClaimAmbushAsync(instance, TestIds.Player, company.Id,
            new AmbushClaimRequest(fight.RunId, true, Contract, null, 2, false));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(claim!.Won);
        Assert.Equal(CompanyState.Travelling, claim.Parties.Parties[0].State);
        Assert.Equal(2, claim.Parties.Parties[0].Caravan!.Wagons);

        // Arrived: the quest reads as done when the log is next read, with two wagons through.
        await WalkToAsync(company.Id, 1.01);
        var (_, board) = await Quests.BoardAsync(instance, TestIds.Player);
        var entry = Assert.Single(board!.Active);
        Assert.True(entry.Done);
        Assert.Equal(2, entry.Progress);
        var party = await _db.PlayerParties.AsNoTracking().SingleAsync(p => p.Id == company.Id);
        Assert.Null(party.CaravanQuestId);
        Assert.Equal(to, party.SiteId);

        var (handed, paid) = await Quests.HandInAsync(instance, TestIds.Player, quest.Id);
        Assert.True(handed.Succeeded, handed.Message);
        Assert.Equal(CaravanRules.DeliveredGold(300, 2, false), paid!.Gold);
        Assert.Equal(200, paid.Gold);
        Assert.Equal(4, paid.Materials.Single(m => m.MaterialName == "Grain").Quantity);
        Assert.Equal(2, paid.Materials.Single(m => m.MaterialName == QuestRules.Essence).Quantity);
        Assert.NotEmpty(paid.Items);
    }

    [Fact]
    public async Task APerfectDefencePaysTheBonus()
    {
        var (instance, company, quest, _, _, fight) = await AmbushedAsync();
        await BackdateRunAsync(fight.RunId);
        await Parties.ClaimAmbushAsync(instance, TestIds.Player, company.Id,
            new AmbushClaimRequest(fight.RunId, true, Contract, null, 3, true));
        await WalkToAsync(company.Id, 1.01);

        var (_, paid) = await Quests.HandInAsync(instance, TestIds.Player, quest.Id);

        Assert.Equal((long)Math.Round(300 * (1 + CaravanRules.PerfectBonus)), paid!.Gold);
    }

    [Fact]
    public async Task ALostCaravanStrikesTheQuest_AndPaysNothing()
    {
        var (instance, company, quest, from, _, fight) = await AmbushedAsync();
        await BackdateRunAsync(fight.RunId);

        // "Won" with no wagon standing is a loss: the cargo is gone.
        var (outcome, claim) = await Parties.ClaimAmbushAsync(instance, TestIds.Player, company.Id,
            new AmbushClaimRequest(fight.RunId, true, Contract, null, 0, false));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.False(claim!.Won);
        Assert.Equal(0, claim.Gold);
        Assert.Empty(await _db.PlayerQuests.ToListAsync());
        var dto = claim.Parties.Parties[0];
        Assert.Equal(CompanyState.Returning, dto.State);
        Assert.Equal(from, dto.Journey!.ToSiteId);
        Assert.Null(dto.Caravan);
    }

    [Fact]
    public async Task FleeingBringsTheCaravanHome_ToSetOutAgain()
    {
        var (instance, company, quest, from, _) = await SeedAsync();
        await Parties.EscortAsync(instance, TestIds.Player, company.Id, Escort(quest.Id));
        await WalkToAsync(company.Id, CaravanRules.FirstAmbushTo + 0.02);
        await Parties.ListAsync(instance, TestIds.Player);

        var (fled, response) = await Parties.FleeAmbushAsync(instance, TestIds.Player, company.Id, Order);
        Assert.True(fled.Succeeded, fled.Message);
        Assert.Equal(CompanyState.Returning, response!.Parties[0].State);
        Assert.NotNull(response.Parties[0].Caravan);

        await WalkToAsync(company.Id, 1.01);
        var (_, home) = await Parties.ListAsync(instance, TestIds.Player);
        Assert.Equal(CompanyState.Idle, home!.Parties[0].State);
        Assert.Equal(from, home.Parties[0].SiteId);
        Assert.Null(home.Parties[0].Caravan);
        var kept = await _db.PlayerQuests.SingleAsync();
        Assert.Null(kept.DoneAt);

        var (again, _) = await Parties.EscortAsync(instance, TestIds.Player, company.Id, Escort(quest.Id));
        Assert.True(again.Succeeded, again.Message);
    }

    [Fact]
    public async Task AThreeHexEscort_IsStruckTwice_AndTheSecondFightHasOnlyTheSurvivors()
    {
        _dice = new FixedDice(0.0);
        var (instance, company, _, _, _, fight) = await AmbushedAsync(hexes: 3);
        var stored = await _db.PlayerParties.AsNoTracking().SingleAsync(p => p.Id == company.Id);
        Assert.Equal(CaravanRules.SecondAmbushFrom, stored.SecondAmbushAt!.Value, 3);

        await BackdateRunAsync(fight.RunId);
        await Parties.ClaimAmbushAsync(instance, TestIds.Player, company.Id,
            new AmbushClaimRequest(fight.RunId, true, Contract, null, 1, false));

        await WalkToAsync(company.Id, CaravanRules.SecondAmbushFrom + 0.05);
        var (_, listed) = await Parties.ListAsync(instance, TestIds.Player);
        var halted = listed!.Parties[0];
        Assert.Equal(CompanyState.Ambushed, halted.State);
        Assert.Equal(1, halted.Ambush!.CaravanWagons);
    }

    [Fact]
    public async Task ATwoHexEscort_IsStruckOnce()
    {
        _dice = new FixedDice(0.0);
        var (instance, company, quest, _, _) = await SeedAsync(hexes: 2);

        await Parties.EscortAsync(instance, TestIds.Player, company.Id, Escort(quest.Id));

        var stored = await _db.PlayerParties.AsNoTracking().SingleAsync(p => p.Id == company.Id);
        Assert.NotNull(stored.AmbushAt);
        Assert.Null(stored.SecondAmbushAt);
    }

    [Fact]
    public async Task GivingUpTheQuest_LetsTheCaravanGo()
    {
        var (instance, company, quest, _, _) = await SeedAsync();
        await Parties.EscortAsync(instance, TestIds.Player, company.Id, Escort(quest.Id));

        await Quests.AbandonAsync(instance, TestIds.Player, quest.Id);

        var party = await _db.PlayerParties.AsNoTracking().SingleAsync(p => p.Id == company.Id);
        Assert.Null(party.CaravanQuestId);
        Assert.Null(party.SecondAmbushAt);
    }

    [Fact]
    public async Task AnAutoCompanyCannotTakeACaravan()
    {
        var (instance, company, quest, _, _) = await SeedAsync();
        var party = await _db.PlayerParties.SingleAsync(p => p.Id == company.Id);
        party.AutoMode = true;
        await _db.SaveChangesAsync();

        var (outcome, _) = await Parties.EscortAsync(instance, TestIds.Player, company.Id, Escort(quest.Id));

        Assert.Equal(PartyError.Busy, outcome.Error);
    }

    // -----------------------------------------------------------------
    // The board and the rules
    // -----------------------------------------------------------------

    [Fact]
    public void VillagesOfferEscorts_ToAVillageOneToThreeHexesAway()
    {
        var r0 = TestWorld.OwnedBy(TestIds.Player, "r0");
        var world = new[]
        {
            r0, TestWorld.Region("r1", q: 1, r: 0), TestWorld.Region("r2", q: 2, r: 0), TestWorld.Region("r9", q: 9, r: 0)
        };
        var byId = world.ToDictionary(r => r.RegionId);
        var escorts = Enumerable.Range(0, 300)
            .SelectMany(h => QuestRules.Board("realm", TestIds.Player, 5000 + h, 0, world, new FakeGameContent().EnemyPeoples, new RunTuning()))
            .Where(o => o.Kind == QuestKind.Escort)
            .ToList();

        Assert.NotEmpty(escorts);
        foreach (var offer in escorts)
        {
            Assert.False(string.IsNullOrEmpty(offer.Target));
            Assert.True(offer.Count > 0);
            var to = byId[SiteSpec.RegionIdOf(offer.DestinationSiteId)!];
            var from = byId[offer.RegionId];
            int hexes = HexCoord.Distance(from.Hex, to.Hex);
            Assert.InRange(hexes, QuestRules.EscortNearest, QuestRules.EscortFarthest);
            // The cargo grows with the distance: 20 + 10 x tier (2) + 0-10, then x1, x1.25 or x1.5.
            Assert.InRange(offer.Count, (int)Math.Round(40 * CaravanRules.EscortCargoFactor(hexes)), (int)Math.Round(50 * CaravanRules.EscortCargoFactor(hexes)));
            Assert.Equal(LocationType.NeutralHome, RegionGenerator.Generate(to).FindSite(offer.DestinationSiteId)!.Type);
            Assert.Contains(offer.Materials, m => m.MaterialName == offer.Target);
        }
    }

    [Fact]
    public void TheRoadIsStruckOnce_AndAThreeHexOneTwice()
    {
        foreach (int hexes in new[] { 1, 2 })
        {
            var once = CaravanRules.RollAmbushes(hexes, new FixedDice(0.999));
            Assert.InRange(once.First, CaravanRules.FirstAmbushFrom, CaravanRules.FirstAmbushTo);
            Assert.Null(once.Second);
        }

        var twice = CaravanRules.RollAmbushes(3, new FixedDice(0.999));
        Assert.NotNull(twice.Second);
        Assert.True(twice.Second > twice.First);
    }

    [Fact]
    public void AFartherEscortPaysMore()
    {
        Assert.Equal(1.5, CaravanRules.EscortGoldFactor(1));
        Assert.Equal(2.0, CaravanRules.EscortGoldFactor(2));
        Assert.Equal(2.5, CaravanRules.EscortGoldFactor(3));
        Assert.Equal(1.0, CaravanRules.EscortCargoFactor(1));
        Assert.Equal(1.25, CaravanRules.EscortCargoFactor(2));
        Assert.Equal(1.5, CaravanRules.EscortCargoFactor(3));
    }

    [Fact]
    public void DeliveryPaysByTheWagons()
    {
        Assert.Equal(100, CaravanRules.DeliveredGold(300, 1, false));
        Assert.Equal(300, CaravanRules.DeliveredGold(300, 3, false));
        Assert.Equal(375, CaravanRules.DeliveredGold(300, 3, true));
        Assert.Equal(0, CaravanRules.DeliveredGold(300, 0, false));
        var goods = CaravanRules.DeliveredMaterials(new[] { new MaterialGrant { MaterialName = "Grain", Quantity = 1 } }, 1);
        Assert.Empty(goods);   // a third of one rounds to none
    }
}
