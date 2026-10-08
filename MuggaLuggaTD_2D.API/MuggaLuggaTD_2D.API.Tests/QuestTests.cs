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
/// Quests (<c>docs/design/quests.md</c>, Mike 2026-10-07): the seeded board, what a deed counts for, the clamp
/// on a reported kill tally, the chest, and the service's doors (take, hand in, a fresh set).
/// </summary>
public class QuestTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();

    private readonly FakeGameContent _content = new()
    {
        DroppableItems = new[] { new ItemTemplate { ItemName = "Cinder Blade", ItemType = ItemTypes.Weapon } }
    };

    private static readonly DateTime Now = new(2026, 10, 8, 12, 30, 0, DateTimeKind.Utc);

    public void Dispose() => _db.Dispose();

    private MaterialWalletService Wallet => new(_db, _log, NullLogger<MaterialWalletService>.Instance);
    private GoldService Gold => new(_db, _log, NullLogger<GoldService>.Instance);
    private ItemLedgerService Items => new(_db, _log, NullLogger<ItemLedgerService>.Instance);

    private QuestService Quests(DateTime? at = null) =>
        new(_db, _content, Items, Gold, Wallet, _log) { Clock = () => at ?? Now };

    private static IReadOnlyDictionary<BiomeType, IReadOnlyList<string>> Peoples => new FakeGameContent().EnemyPeoples;

    /// <summary>
    /// The player holds r0; r1 borders it and r3 is a rival's, also bordering; r2 is far off in the fog.
    /// </summary>
    private static WorldRegionData[] World() => new[]
    {
        TestWorld.OwnedBy(TestIds.Player, "r0"),
        TestWorld.Region("r1", q: 1, r: 0, biome: BiomeType.Forest),
        TestWorld.Region("r2", q: 6, r: 0),
        TestWorld.Region("r3", q: 0, r: 1, ownership: LocationOwnership.Player, ownerUserId: TestIds.Rival)
    };

    private static List<QuestOffer> BoardOf(long hour = 1000, int set = 0, WorldRegionData[]? world = null) =>
        QuestRules.Board("realm", TestIds.Player, hour, set, world ?? World(), Peoples, new RunTuning());

    // -----------------------------------------------------------------
    // The board
    // -----------------------------------------------------------------

    [Fact]
    public void TheBoardIsTheSameEveryTimeItIsAskedFor()
    {
        var a = BoardOf();
        var b = BoardOf();

        Assert.Equal(a.Select(o => (o.Id, o.Kind, o.Target, o.Count, o.ChestTier)), b.Select(o => (o.Id, o.Kind, o.Target, o.Count, o.ChestTier)));
    }

    [Fact]
    public void OnlyVillagesThePlayerCanSeeGiveQuests_AndNotARivalsVillages()
    {
        var board = BoardOf();
        var villages = board.Where(o => o.GiverId != QuestRules.HallGiver).ToList();

        Assert.NotEmpty(villages);
        Assert.All(villages, o => Assert.Contains(o.RegionId, new[] { "r0", "r1" }));
        Assert.All(villages, o => Assert.Equal(SiteSpec.RegionIdOf(o.GiverId), o.RegionId));
        Assert.Equal(QuestRules.HallOffers, board.Count(o => o.GiverId == QuestRules.HallGiver));
        Assert.All(board.Where(o => o.GiverId == QuestRules.HallGiver), o => Assert.Null(o.RegionId));
    }

    [Fact]
    public void AVillageGivesTheSameQuestWhenMoreLandComesIntoSight()
    {
        var before = BoardOf();
        var wider = World().Append(TestWorld.OwnedBy(TestIds.Player, "r5")).ToArray();
        wider[^1].Hex = new HexCoord(6, 1);
        var after = BoardOf(world: wider);

        foreach (var offer in before.Where(o => o.GiverId != QuestRules.HallGiver))
        {
            var same = after.FirstOrDefault(o => o.Id == offer.Id);
            if (same == null) continue; // displaced by a higher-scoring village, which is allowed
            Assert.Equal((offer.Kind, offer.Target, offer.Count, offer.ChestTier), (same.Kind, same.Target, same.Count, same.ChestTier));
        }
    }

    [Fact]
    public void ANewHourOrSetIsANewBoard()
    {
        var now = BoardOf(hour: 1000).Select(o => o.Id).ToList();
        Assert.Empty(now.Intersect(BoardOf(hour: 1001).Select(o => o.Id)));
        Assert.Empty(now.Intersect(BoardOf(set: 1).Select(o => o.Id)));
    }

    [Fact]
    public void ASlayQuestAsksOnlyForAPeopleThatLivesInItsRegion()
    {
        var world = World();
        for (long hour = 0; hour < 60; hour++)
        {
            foreach (var offer in BoardOf(hour: hour).Where(o => o.Kind == QuestKind.Slay))
            {
                var biome = world.Single(r => r.RegionId == offer.RegionId).Biome;
                Assert.Contains(offer.Target, QuestRules.PeoplesOf(biome, Peoples));
                Assert.True(offer.Count >= 5);
            }
        }
    }

    [Fact]
    public void EveryKindOfQuestTurnsUpOverADay()
    {
        var kinds = Enumerable.Range(0, 24).SelectMany(h => BoardOf(hour: h)).Select(o => o.Kind).Distinct().ToList();
        Assert.Contains(QuestKind.Slay, kinds);
        Assert.Contains(QuestKind.Clear, kinds);
        Assert.Contains(QuestKind.Ambush, kinds);
        Assert.Contains(QuestKind.Gather, kinds);
    }

    [Fact]
    public void AVillagesAskAreMixed_NotAllOfOneKind()
    {
        var kinds = Enumerable.Range(0, 300).SelectMany(h => BoardOf(hour: h)).Where(o => o.GiverId != QuestRules.HallGiver)
            .GroupBy(o => o.Kind).ToDictionary(g => g.Key, g => g.Count());
        int total = kinds.Values.Sum();
        foreach (var kind in new[] { QuestKind.Slay, QuestKind.Clear, QuestKind.Gather })
            Assert.InRange(kinds.GetValueOrDefault(kind) / (double)total, 0.2, 0.5);
    }

    [Fact]
    public void WanderingGiversOfferBesideTheVillages_EachAskingAfterItsCalling()
    {
        var board = BoardOf();
        var npcs = board.Where(o => QuestRules.IsNpcGiver(o.GiverId)).ToList();

        Assert.Equal(QuestRules.NpcOffers, npcs.Count);
        Assert.All(npcs, o => Assert.Equal(SiteSpec.RegionIdOf(o.GiverId), o.RegionId));
        Assert.All(npcs, o => Assert.Contains(o.RegionId, new[] { "r0", "r1" }));
        Assert.All(board.Where(o => !QuestRules.IsNpcGiver(o.GiverId)), o => Assert.Null(o.Calling));

        var asks = new Dictionary<string, QuestKind>
        {
            [QuestRules.Hunter] = QuestKind.Slay, [QuestRules.Pilgrim] = QuestKind.Clear,
            [QuestRules.Pedlar] = QuestKind.Gather, [QuestRules.Scout] = QuestKind.Ambush
        };
        var callings = new HashSet<string>();
        for (long hour = 0; hour < 100; hour++)
            foreach (var o in BoardOf(hour: hour).Where(o => QuestRules.IsNpcGiver(o.GiverId)))
            {
                Assert.Equal(asks[o.Calling], o.Kind);
                callings.Add(o.Calling);
            }
        Assert.Equal(QuestRules.Callings.Count, callings.Count);
    }

    [Fact]
    public void AScoutsAmbushCountsOnlyOnTheRoadsOfItsRegion()
    {
        var scout = new QuestOffer { Kind = QuestKind.Ambush, RegionId = "r1", Count = 1, Calling = QuestRules.Scout };
        Assert.Equal(0, QuestRules.Advance(scout, 0, new QuestDeed { RegionId = "r0", AmbushWon = true }));
        Assert.Equal(1, QuestRules.Advance(scout, 0, new QuestDeed { RegionId = "r1", AmbushWon = true }));
    }

    // -----------------------------------------------------------------
    // Deeds, kills and the chest
    // -----------------------------------------------------------------

    [Fact]
    public void AReportedTallyIsHeldToTheBiomesPeoplesAndTheRunsPlan()
    {
        var clamped = QuestRules.ClampKills(
            new Dictionary<string, int> { ["Goblin"] = 500, ["Skeleton"] = 9, ["Drakan"] = 4 },
            new[] { "Goblin", "Drakan" }, plannedEnemies: 20);

        Assert.False(clamped.ContainsKey("Skeleton"));
        Assert.Equal(30, clamped.Values.Sum()); // 20 planned, with half again for a lord and his guards
    }

    [Fact]
    public void ADeedCountsOnlyWhereTheQuestIs_AndAHallQuestCountsAnywhere()
    {
        var slay = new QuestOffer { Kind = QuestKind.Slay, RegionId = "r0", Target = "Goblin", Count = 10 };
        var elsewhere = new QuestDeed { RegionId = "r1", Kills = new() { ["Goblin"] = 6 } };
        var here = new QuestDeed { RegionId = "r0", Kills = new() { ["Goblin"] = 6 } };

        Assert.Equal(0, QuestRules.Advance(slay, 0, elsewhere));
        Assert.Equal(6, QuestRules.Advance(slay, 0, here));
        Assert.Equal(10, QuestRules.Advance(slay, 6, here)); // never past the count

        var hallClear = new QuestOffer { Kind = QuestKind.Clear, Count = 3 };
        Assert.Equal(1, QuestRules.Advance(hallClear, 0, new QuestDeed { RegionId = "r7", ClearedSiteId = "r7:2" }));

        var oneSite = new QuestOffer { Kind = QuestKind.Clear, RegionId = "r0", Target = "r0:4", Count = 1 };
        Assert.Equal(0, QuestRules.Advance(oneSite, 0, new QuestDeed { RegionId = "r0", ClearedSiteId = "r0:5" }));
        Assert.Equal(1, QuestRules.Advance(oneSite, 0, new QuestDeed { RegionId = "r0", ClearedSiteId = "r0:4" }));

        var ambush = new QuestOffer { Kind = QuestKind.Ambush, Count = 1 };
        Assert.Equal(1, QuestRules.Advance(ambush, 0, new QuestDeed { RegionId = "r4", AmbushWon = true }));
    }

    [Theory]
    [InlineData(ItemRarityTypes.Uncommon)]
    [InlineData(ItemRarityTypes.Rare)]
    [InlineData(ItemRarityTypes.Legendary)]
    public void AChestHoldsOnePieceOfItsTier_AndTheRestLower(ItemRarityTypes tier)
    {
        var random = new Random(7);
        for (int i = 0; i < 50; i++)
        {
            var pieces = QuestRules.RollChest(_content.DroppableItems.ToList(), random, 8, tier);
            Assert.Equal(QuestRules.ChestPieces, pieces.Count);
            Assert.Equal(tier, pieces[0].Rarity);
            Assert.All(pieces.Skip(1), p => Assert.True(p.Rarity < tier || p.Rarity == ItemRarityTypes.Common));
        }
    }

    // -----------------------------------------------------------------
    // The service
    // -----------------------------------------------------------------

    [Fact]
    public async Task TakingAQuestTakesItOffTheBoard_UpToTheLimit()
    {
        var realm = await SeedAsync();
        var service = Quests();

        var (_, board) = await service.BoardAsync(realm, TestIds.Player);
        Assert.True(board!.Offers.Count > QuestRules.MaxActive);

        foreach (var offer in board.Offers.Take(QuestRules.MaxActive))
            Assert.True((await service.AcceptAsync(realm, TestIds.Player, offer.Id)).Outcome.Succeeded);

        var (refused, _) = await service.AcceptAsync(realm, TestIds.Player, board.Offers[QuestRules.MaxActive].Id);
        Assert.Equal(QuestError.TooManyActive, refused.Error);

        var (again, _) = await service.AcceptAsync(realm, TestIds.Player, board.Offers[0].Id);
        Assert.Equal(QuestError.OfferNotFound, again.Error);

        var (_, after) = await service.BoardAsync(realm, TestIds.Player);
        Assert.Equal(QuestRules.MaxActive, after!.Active.Count);
        Assert.DoesNotContain(after.Offers, o => board.Offers.Take(QuestRules.MaxActive).Any(t => t.Id == o.Id));
        Assert.Contains(board.Offers[0].GiverId, after.Seen);
    }

    [Fact]
    public async Task ATakenQuestOutlivesTheHour_WhileTheUntakenOffersTurnOver()
    {
        var realm = await SeedAsync();
        var (_, board) = await Quests().BoardAsync(realm, TestIds.Player);
        await Quests().AcceptAsync(realm, TestIds.Player, board!.Offers[0].Id);

        var (_, later) = await Quests(Now.AddHours(1)).BoardAsync(realm, TestIds.Player);

        Assert.Single(later!.Active);
        Assert.Equal(board.Offers[0].Id, later.Active[0].Offer.Id);
        Assert.Empty(later.Offers.Select(o => o.Id).Intersect(board.Offers.Select(o => o.Id)));
        Assert.Empty(later.Seen);
    }

    [Fact]
    public async Task AFinishedQuestPaysItsChestGoldAndMaterials_Once()
    {
        var realm = await SeedAsync();
        var quest = await TakeAsync(realm, new QuestOffer
        {
            Id = "x.0.r0:1", GiverId = "r0:1", RegionId = "r0", Kind = QuestKind.Slay, Target = "Goblin", Count = 10,
            Level = 6, ChestTier = ItemRarityTypes.Rare, Gold = 120,
            Materials = new() { new MaterialGrant { MaterialName = "Lesser Essence", Quantity = 4 } }
        });

        var (notYet, _) = await Quests().HandInAsync(realm, TestIds.Player, quest.Id);
        Assert.Equal(QuestError.NotDone, notYet.Error);

        await Quests().RecordAsync(realm, TestIds.Player, new QuestDeed { RegionId = "r0", Kills = new() { ["Goblin"] = 12 } });

        var (paid, response) = await Quests().HandInAsync(realm, TestIds.Player, quest.Id);
        Assert.True(paid.Succeeded, paid.Message);
        Assert.Equal(QuestRules.ChestPieces, response!.Items.Count);
        Assert.Equal(ItemRarityTypes.Rare, response.Items[0].Rarity);
        Assert.Equal(120, response.Gold);
        Assert.Equal(QuestRules.ChestPieces, await _db.ItemGrants.CountAsync(g => g.UserId == TestIds.Player));
        Assert.Equal(4, (await _db.PlayerMaterials.SingleAsync(m => m.MaterialName == "Lesser Essence")).Quantity);

        var (twice, _) = await Quests().HandInAsync(realm, TestIds.Player, quest.Id);
        Assert.Equal(QuestError.QuestNotFound, twice.Error);
    }

    [Fact]
    public async Task AGatherQuestIsHandedInFromTheWallet()
    {
        var realm = await SeedAsync();
        var quest = await TakeAsync(realm, new QuestOffer
        {
            Id = "x.0.r0:1", GiverId = "r0:1", RegionId = "r0", Kind = QuestKind.Gather, Target = "Timber", Count = 15,
            Level = 4, ChestTier = ItemRarityTypes.Uncommon, Gold = 50
        });

        await Wallet.GrantAsync(realm, TestIds.Player, new[] { new MaterialGrant { MaterialName = "Timber", Quantity = 10 } }, "test");
        var (short_, _) = await Quests().HandInAsync(realm, TestIds.Player, quest.Id);
        Assert.Equal(QuestError.NotEnoughToBring, short_.Error);

        await Wallet.GrantAsync(realm, TestIds.Player, new[] { new MaterialGrant { MaterialName = "Timber", Quantity = 8 } }, "test");
        var (paid, _) = await Quests().HandInAsync(realm, TestIds.Player, quest.Id);
        Assert.True(paid.Succeeded, paid.Message);
        Assert.Equal(3, (await _db.PlayerMaterials.SingleAsync(m => m.MaterialName == "Timber")).Quantity);
    }

    [Fact]
    public async Task HandingInEveryOfferOnTheBoardBringsAFreshSetAtOnce()
    {
        var realm = await SeedAsync();
        var (_, board) = await Quests().BoardAsync(realm, TestIds.Player);
        var all = board!.Offers.ToList();

        // Enough of everything a Gather quest could ask for.
        await Wallet.GrantAsync(realm, TestIds.Player,
            all.Where(o => o.Kind == QuestKind.Gather).Select(o => new MaterialGrant { MaterialName = o.Target, Quantity = o.Count }).ToList(), "test");

        QuestBoardResponse? last = null;
        foreach (var batch in all.Chunk(QuestRules.MaxActive))
        {
            foreach (var offer in batch) await Quests().AcceptAsync(realm, TestIds.Player, offer.Id);
            await Quests().DebugFinishAsync(realm, TestIds.Player);
            var (_, active) = await Quests().BoardAsync(realm, TestIds.Player);
            foreach (var quest in active!.Active)
            {
                var (handed, response) = await Quests().HandInAsync(realm, TestIds.Player, quest.Id);
                Assert.True(handed.Succeeded, handed.Message);
                last = response!.Board;
            }
        }

        Assert.Equal(1, last!.Set);
        Assert.NotEmpty(last.Offers);
        Assert.Empty(last.Offers.Select(o => o.Id).Intersect(all.Select(o => o.Id)));
    }

    [Fact]
    public async Task APveClaimCountsItsClearAndItsKillsTowardQuests()
    {
        var realm = await SeedAsync();
        var region = TestWorld.OwnedBy(TestIds.Player, "r0");
        string dungeon = TestWorld.DungeonIn(region);
        var slay = await TakeAsync(realm, new QuestOffer { Id = "x.0.a", GiverId = "r0:1", RegionId = "r0", Kind = QuestKind.Slay, Target = "Goblin", Count = 999 });
        var clear = await TakeAsync(realm, new QuestOffer { Id = "x.0.b", GiverId = "r0:1", RegionId = "r0", Kind = QuestKind.Clear, Target = dungeon, Count = 1 });

        var pve = new WorldPveService(_db, _content, Wallet, Gold,
            new TavernService(_db, _content, Wallet, Gold, _log, NullLogger<TavernService>.Instance),
            NullLogger<WorldPveService>.Instance, Items, quests: Quests());
        _db.PlayerParties.Add(new PlayerParty
        {
            GameInstanceId = realm, UserId = TestIds.Player, Name = "The Vanguard",
            CharacterIdsJson = MarchingArmy.WriteIds(new List<string> { "hero" }),
            State = CompanyState.Idle, RegionId = "r0", SiteId = dungeon
        });
        await _db.SaveChangesAsync();
        var party = await _db.PlayerParties.SingleAsync();

        var (began, runId) = await pve.BeginAsync(realm, TestIds.Player, new PveBeginRequest(dungeon, SharedContract.Version, null, party.Id));
        Assert.True(began.Succeeded, began.Message);
        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow.AddMinutes(-2);
        await _db.SaveChangesAsync();

        var (claimed, _, _) = await pve.ClaimAsync(realm, TestIds.Player, "Mike",
            new PveClaimRequest(runId, SharedContract.Version, new Dictionary<string, int> { ["Goblin"] = 100000, ["Skeleton"] = 5 }));
        Assert.True(claimed.Succeeded, claimed.Message);

        var planned = RunRewardCalculator.PlannedEnemies(TestWorld.SiteOfType(region, LocationType.Dungeon).Tier, _content.RunTuning, LocationType.Dungeon);
        var rows = await _db.PlayerQuests.AsNoTracking().ToListAsync();
        Assert.Equal((int)Math.Ceiling(planned * QuestRules.KillSlack), rows.Single(q => q.Id == slay.Id).Progress);
        Assert.NotNull(rows.Single(q => q.Id == clear.Id).DoneAt);
    }

    // -----------------------------------------------------------------

    private async Task<Guid> SeedAsync()
    {
        var instance = await _db.AddInstanceAsync();
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(World()));
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, TestSave.ToJson(TestSave.Roster(TestSave.Character("hero"))));
        return instance.Id;
    }

    /// <summary>A quest taken as if from the board, with exactly the objective a test needs.</summary>
    private async Task<PlayerQuest> TakeAsync(Guid realm, QuestOffer offer)
    {
        var quest = new PlayerQuest { GameInstanceId = realm, UserId = TestIds.Player, OfferId = offer.Id, Offer = offer, AcceptedAt = Now };
        _db.PlayerQuests.Add(quest);
        await _db.SaveChangesAsync();
        return quest;
    }
}
