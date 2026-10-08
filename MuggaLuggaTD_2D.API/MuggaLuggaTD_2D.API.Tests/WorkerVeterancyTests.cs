using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Workers who grow (Workers spec, 2026-10-08, with Mike's changes: perks rolled by tier, promotion at
/// 5 and 10). What is pinned: levels come from hours at work and lift the rate; each roll level rolls
/// once, deterministically per (worker, level, season), and is stored and revealed until seen; the
/// grades follow the tier's weights; promotion is about even and adds a trait; the perks do what they
/// say; KEEP is capped at two; dismissing frees the bed; land lost keeps the carried fraction; and a
/// season end keeps two veterans at half their level, perks above it dropped.
/// </summary>
public class WorkerVeterancyTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private Guid _realm;
    private DateTime _now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Player = TestIds.Player;

    public void Dispose() => _db.Dispose();

    private GoldService Gold => new(_db, _log, NullLogger<GoldService>.Instance);
    private MaterialWalletService Wallet => new(_db, _log, NullLogger<MaterialWalletService>.Instance);
    private HiringService Hiring => new(_db, Wallet, Gold, _log) { Dice = new Random(7), Clock = () => _now };

    private async Task<(WorldRegionData Region, SiteSpec Site, ResourceTrade Trade)> WorldAsync(int regions = 1)
    {
        _realm = (await _db.AddInstanceAsync()).Id;
        for (int i = 0; i < 200; i++)
        {
            var region = TestWorld.OwnedBy(Player, $"r{i}", tier: 2);
            var site = RegionGenerator.Generate(region).Sites.FirstOrDefault(s => s.Type == LocationType.ResourceNode);
            if (site == null) continue;
            var all = new List<WorldRegionData> { region };
            for (int n = 1; n < regions; n++)
                all.Add(TestWorld.Region($"x{n}", q: n, r: 0, ownership: LocationOwnership.Player, ownerUserId: Player));
            await _db.AddWorldAsync(_realm, TestWorld.Blob(all.ToArray()));
            return (region, site, ResourceNodeRules.TradeOf(site.SiteId, region.Biome));
        }
        throw new InvalidOperationException("no region with a resource site");
    }

    private async Task<HiredWorker> WorkerAsync(ResourceTrade trade, WorkerTier tier = WorkerTier.Local, params WorkerPerk[] perks)
    {
        var worker = new HiredWorker
        {
            GameInstanceId = _realm, UserId = Player, Name = "Tam", Trade = trade, Tier = tier,
            HomeBiome = BiomeType.Volcanic, HiredAt = _now, LastSettledAt = _now
        };
        if (perks.Length > 0)
        {
            // Perks as if rolled at levels 3, 6, 9: the worker already stands there.
            var levels = new[] { 3, 6, 9 };
            worker.Rolls = WorkerRollLog.Write(perks.Select((p, i) => new WorkerRollResult { Level = levels[i], Kind = WorkerRollKind.Perk, Perk = p }));
            worker.RollsSeen = perks.Length;
            worker.LevelRolledTo = levels[perks.Length - 1];
            worker.HoursWorked = WorkerLevelRules.HoursAt(worker.LevelRolledTo);
        }
        _db.HiredWorkers.Add(worker);
        await _db.SaveChangesAsync();
        return worker;
    }

    private static WorkerSheet Sheet(WorkerTier tier = WorkerTier.Local, int level = 1, WorkerTrait[]? traits = null, WorkerPerk[]? perks = null) => new()
    {
        Tier = tier, Trade = ResourceTrade.Miner, Traits = traits ?? Array.Empty<WorkerTrait>(),
        Perks = perks ?? Array.Empty<WorkerPerk>(), HomeBiome = BiomeType.Grassland, Level = level
    };

    private static IEnumerable<string> Ids(int count) => Enumerable.Range(0, count).Select(i => $"worker-{i}");

    // -----------------------------------------------------------------
    // Levels
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5.9, 1)]
    [InlineData(6, 2)]
    [InlineData(18, 3)]
    [InlineData(60, 5)]
    [InlineData(431.9, 9)]
    [InlineData(432, 10)]
    [InlineData(5000, 10)]
    public void TheLevelComesFromTheHours(double hours, int level) => Assert.Equal(level, WorkerLevelRules.LevelFor(hours));

    [Fact]
    public void EachLevelAddsFourPercent()
    {
        double one = WorkerLevelRules.RateAt(Sheet(level: 1), ResourceTrade.Miner, BiomeType.Grassland, 1, 0);
        Assert.Equal(HiringRules.BaseRate(WorkerTier.Local), one, 6);
        Assert.Equal(one * 1.36, WorkerLevelRules.RateAt(Sheet(level: 10), ResourceTrade.Miner, BiomeType.Grassland, 1, 0), 6);
    }

    [Fact]
    public void TheRateMatchesTheBoardsRuleForAFreshWorker()
    {
        var traits = new[] { WorkerTrait.Steady, WorkerTrait.Prospector };
        Assert.Equal(
            HiringRules.RateAt(WorkerTier.Skilled, ResourceTrade.Miner, null, traits, BiomeType.Grassland, ResourceTrade.Miner, BiomeType.Highland, 3, 1),
            WorkerLevelRules.RateAt(Sheet(WorkerTier.Skilled, traits: traits), ResourceTrade.Miner, BiomeType.Highland, 3, HiringRules.ForemanBonus), 6);
    }

    [Fact]
    public void ThePerksThatLiftTheRateDoSo()
    {
        double plain = WorkerLevelRules.RateAt(Sheet(), ResourceTrade.Miner, BiomeType.Grassland, 1, 0);
        Assert.Equal(plain * 1.08, WorkerLevelRules.RateAt(Sheet(perks: new[] { WorkerPerk.SteadyHands }), ResourceTrade.Miner, BiomeType.Grassland, 1, 0), 6);
        Assert.Equal(plain * 1.20, WorkerLevelRules.RateAt(Sheet(perks: new[] { WorkerPerk.Tireless }), ResourceTrade.Miner, BiomeType.Grassland, 1, 0), 6);
        Assert.Equal(plain * 1.12, WorkerLevelRules.RateAt(Sheet(perks: new[] { WorkerPerk.Homebody }), ResourceTrade.Miner, BiomeType.Grassland, 1, 0), 6);
        Assert.Equal(plain, WorkerLevelRules.RateAt(Sheet(perks: new[] { WorkerPerk.Homebody }), ResourceTrade.Miner, BiomeType.Desert, 1, 0), 6);
    }

    [Fact]
    public void JackOfTradesWorksASecondTrade_FullRateWhenVersatile()
    {
        var jack = Sheet(perks: new[] { WorkerPerk.JackOfTrades });
        jack.SecondTrade = ResourceTrade.Farmer;
        double main = WorkerLevelRules.RateAt(Sheet(), ResourceTrade.Miner, BiomeType.Desert, 1, 0);
        Assert.Equal(main * 0.75, WorkerLevelRules.RateAt(jack, ResourceTrade.Farmer, BiomeType.Desert, 1, 0), 6);

        jack.Traits = new[] { WorkerTrait.Versatile };
        Assert.Equal(main, WorkerLevelRules.RateAt(jack, ResourceTrade.Farmer, BiomeType.Desert, 1, 0), 6);
    }

    [Fact]
    public void AnOverseerIsAForeman_AndAForemanOverseerLiftsMore()
    {
        Assert.Equal(0.10, WorkerLevelRules.ForemanBonusOf(Sheet(perks: new[] { WorkerPerk.Overseer })), 6);
        Assert.Equal(0.10, WorkerLevelRules.ForemanBonusOf(Sheet(traits: new[] { WorkerTrait.Foreman })), 6);
        Assert.Equal(0.15, WorkerLevelRules.ForemanBonusOf(Sheet(traits: new[] { WorkerTrait.Foreman }, perks: new[] { WorkerPerk.Overseer })), 6);
        Assert.Equal(0, WorkerLevelRules.ForemanBonusOf(Sheet()), 6);
    }

    [Fact]
    public void StalwartIsHarriedByOnlyAQuarter()
    {
        var from = _now; var to = _now.AddHours(10);
        Assert.Equal(12 * 10 * 0.5, WorkerLevelRules.Gathered(12, from, to, Sheet(), "w", _ => true, null), 6);
        Assert.Equal(12 * 10 * 0.75, WorkerLevelRules.Gathered(12, from, to, Sheet(perks: new[] { WorkerPerk.Stalwart }), "w", _ => true, null), 6);
    }

    [Fact]
    public void AnOldHandEarnsMoreOnlyAfterADayAtTheSite()
    {
        var sheet = Sheet(perks: new[] { WorkerPerk.OldHand });
        var assigned = _now;
        Assert.Equal(12 * 24, WorkerLevelRules.Gathered(12, assigned, assigned.AddHours(24), sheet, "w", null, assigned), 6);
        // Half an hour either side of the mark: the second half pays the bonus.
        Assert.Equal(12 * (0.5 + 0.5 * 1.10),
            WorkerLevelRules.Gathered(12, assigned.AddHours(23.5), assigned.AddHours(24.5), sheet, "w", null, assigned), 6);
    }

    [Fact]
    public void PackhorseMakesSomeHoursLucky_AndLuckyHoursPayMore()
    {
        var pack = Sheet(perks: new[] { WorkerPerk.Packhorse });
        var lucky = Enumerable.Range(0, 3000).Count(h => WorkerLevelRules.IsLuckyHour(pack, "w", h));
        Assert.InRange(lucky, 3000 / 15 * 0.7, 3000 / 15 * 1.3);
        Assert.Equal(0, Enumerable.Range(0, 300).Count(h => WorkerLevelRules.IsLuckyHour(Sheet(), "w", h)));

        long luckyHour = Enumerable.Range(0, 3000).First(h => WorkerLevelRules.IsLuckyHour(pack, "w", h));
        var start = new DateTime(luckyHour * TimeSpan.TicksPerHour, DateTimeKind.Utc);
        Assert.Equal(12 * 2.5, WorkerLevelRules.Gathered(12, start, start.AddHours(1), pack, "w", null, null), 6);
    }

    [Fact]
    public void FindsAreCountedOnceHoweverTheSettlesFall()
    {
        var keen = Sheet(WorkerTier.Skilled, perks: new[] { WorkerPerk.KeenEye, WorkerPerk.LuckyStrike });
        var from = _now;
        var whole = WorkerLevelRules.Finds(keen, "w", from, from.AddHours(500), BiomeType.Volcanic);
        var split = new Dictionary<string, int>();
        for (double h = 0; h < 500; h += 7.3)
            foreach (var (k, v) in WorkerLevelRules.Finds(keen, "w", from.AddHours(h), from.AddHours(Math.Min(500, h + 7.3)), BiomeType.Volcanic))
                split[k] = split.GetValueOrDefault(k) + v;

        Assert.Equal(whole, split);
        Assert.InRange(whole["Minor Fire Crystal"], 500 / 12 * 0.6, 500 / 12 * 1.4);
        Assert.True(whole.ContainsKey(WorkerLevelRules.LuckyStrikeFind));
        // A Local's Keen Eye turns up essence instead.
        Assert.True(WorkerLevelRules.Finds(Sheet(perks: new[] { WorkerPerk.KeenEye }), "w", from, from.AddHours(500), BiomeType.Volcanic)
            .ContainsKey(QuestRules.Essence));
    }

    // -----------------------------------------------------------------
    // The rolls
    // -----------------------------------------------------------------

    [Fact]
    public void OnlyRollLevelsRoll()
    {
        foreach (int level in new[] { 1, 2, 4, 7, 8 })
            Assert.Null(WorkerLevelRules.Roll("w", level, 1, Sheet()));
        Assert.Equal(WorkerRollKind.Perk, WorkerLevelRules.Roll("w", 3, 1, Sheet())!.Kind);
    }

    [Fact]
    public void ARollIsTheSameForTheSameWorkerLevelAndSeason_AndFreshNextSeason()
    {
        foreach (var id in Ids(50))
            Assert.Equal(WorkerLevelRules.Roll(id, 3, 1, Sheet())!.Perk, WorkerLevelRules.Roll(id, 3, 1, Sheet())!.Perk);
        Assert.True(Ids(200).Count(id => WorkerLevelRules.Roll(id, 3, 1, Sheet())!.Perk != WorkerLevelRules.Roll(id, 3, 2, Sheet())!.Perk) > 100);
    }

    [Theory]
    [InlineData(WorkerTier.Local, 60, 30, 10)]
    [InlineData(WorkerTier.Skilled, 40, 40, 20)]
    [InlineData(WorkerTier.Master, 20, 45, 35)]
    public void TheTierWeightsTheGrade(WorkerTier tier, int minor, int major, int great)
    {
        const int n = 10_000;
        var grades = Ids(n).Select(id => WorkerLevelRules.GradeOf(WorkerLevelRules.Roll(id, 3, 1, Sheet(tier))!.Perk!.Value)).ToList();
        Assert.InRange(grades.Count(g => g == PerkGrade.Minor) * 100.0 / n, minor - 2.5, minor + 2.5);
        Assert.InRange(grades.Count(g => g == PerkGrade.Major) * 100.0 / n, major - 2.5, major + 2.5);
        Assert.InRange(grades.Count(g => g == PerkGrade.Great) * 100.0 / n, great - 2.5, great + 2.5);
    }

    [Fact]
    public void NoPerkTwice_AUsedUpGradeFallsThrough()
    {
        var allGreat = WorkerLevelRules.PerksOf(PerkGrade.Great).ToArray();
        foreach (var id in Ids(500))
        {
            var roll = WorkerLevelRules.Roll(id, 3, 1, Sheet(WorkerTier.Master, perks: allGreat))!;
            Assert.NotNull(roll.Perk);
            Assert.DoesNotContain(roll.Perk!.Value, allGreat);
        }
    }

    [Fact]
    public void PromotionIsAboutEven_AndReachesTheNewTiersTraits()
    {
        var rolls = Ids(4000).Select(id => WorkerLevelRules.Roll(id, 5, 1, Sheet())!).ToList();
        double promoted = rolls.Count(r => r.Kind == WorkerRollKind.Promotion) / 4000.0;
        Assert.InRange(promoted, 0.47, 0.53);
        Assert.All(rolls.Where(r => r.Kind == WorkerRollKind.Promotion), r =>
        {
            Assert.Equal(WorkerTier.Skilled, r.NewTier);
            Assert.NotNull(r.NewTrait); // a Local with no trait gains one at Skilled
        });
        Assert.All(rolls.Where(r => r.Kind != WorkerRollKind.Promotion), r => Assert.Equal(WorkerRollKind.NoPromotion, r.Kind));
    }

    [Fact]
    public void ALocalCanBecomeAMaster()
    {
        foreach (var id in Ids(200))
        {
            var sheet = Sheet();
            WorkerLevelRules.Apply(sheet, WorkerLevelRules.Roll(id, 5, 1, sheet));
            WorkerLevelRules.Apply(sheet, WorkerLevelRules.Roll(id, 10, 1, sheet));
            if (sheet.Tier != WorkerTier.Master) continue;
            Assert.Equal(WorkerLevelRules.TraitsFor(WorkerTier.Master), sheet.Traits.Count);
            Assert.Equal(sheet.Traits.Count, sheet.Traits.Distinct().Count());
            return;
        }
        Assert.Fail("no Local reached Master in 200 workers (expected about 50)");
    }

    [Fact]
    public void AMasterRollsABonusPerkInstead()
    {
        var rolls = Ids(2000).Select(id => WorkerLevelRules.Roll(id, 5, 1, Sheet(WorkerTier.Master))!).ToList();
        Assert.All(rolls, r => Assert.Contains(r.Kind, new[] { WorkerRollKind.BonusPerk, WorkerRollKind.NoBonusPerk }));
        Assert.InRange(rolls.Count(r => r.Kind == WorkerRollKind.BonusPerk) / 2000.0, 0.45, 0.55);
        Assert.All(rolls.Where(r => r.Kind == WorkerRollKind.BonusPerk), r => Assert.NotNull(r.Perk));
    }

    [Fact]
    public void AForcedPromotionIsHonoured()
    {
        Assert.Equal(WorkerRollKind.Promotion, WorkerLevelRules.Roll("w", 5, 1, Sheet(), forcePromotion: true)!.Kind);
        Assert.Equal(WorkerRollKind.NoPromotion, WorkerLevelRules.Roll("w", 5, 1, Sheet(), forcePromotion: false)!.Kind);
    }

    [Fact]
    public void TheRollLogRoundTrips()
    {
        var rolls = new List<WorkerRollResult>
        {
            new() { Level = 3, Kind = WorkerRollKind.Perk, Perk = WorkerPerk.JackOfTrades, NewSecondTrade = ResourceTrade.Farmer },
            new() { Level = 5, Kind = WorkerRollKind.Promotion, NewTier = WorkerTier.Skilled, NewTrait = WorkerTrait.Lucky },
            new() { Level = 10, Kind = WorkerRollKind.NoPromotion }
        };
        var back = WorkerRollLog.Parse(WorkerRollLog.Write(rolls));
        Assert.Equal(rolls.Select(r => (r.Level, r.Kind, r.Perk, r.NewTier, r.NewTrait, r.NewSecondTrade)),
            back.Select(r => (r.Level, r.Kind, r.Perk, r.NewTier, r.NewTrait, r.NewSecondTrade)));
    }

    [Fact]
    public void AByNameIsStableAndInTheTradesVoice()
    {
        var a = Naming.WorkerByName(ResourceTrade.Forester, new[] { WorkerTrait.Steady }, 42);
        Assert.Equal(a, Naming.WorkerByName(ResourceTrade.Forester, new[] { WorkerTrait.Steady }, 42));
        Assert.False(string.IsNullOrWhiteSpace(a));
        Assert.True(Enumerable.Range(0, 100).Select(i => Naming.WorkerByName(ResourceTrade.Miner, null!, (ulong)i)).Distinct().Count() > 3);
        // A trait gained by promotion leaves most by-names as they were.
        int same = Enumerable.Range(0, 300).Count(i => Naming.WorkerByName(ResourceTrade.Miner, null!, (ulong)i)
            == Naming.WorkerByName(ResourceTrade.Miner, new[] { WorkerTrait.Lucky }, (ulong)i));
        Assert.InRange(same, 180, 240);
    }

    // -----------------------------------------------------------------
    // The service
    // -----------------------------------------------------------------

    [Fact]
    public async Task HoursAccrueOnlyWhileAssigned()
    {
        var (_, site, trade) = await WorldAsync();
        var working = await WorkerAsync(trade);
        var idle = await WorkerAsync(trade);
        await Hiring.AssignAsync(_realm, Player, working.Id, site.SiteId);

        _now += TimeSpan.FromHours(10);
        await Hiring.SettlePlayerAsync(_realm, Player);

        Assert.Equal(10, working.HoursWorked, 6);
        Assert.Equal(0, idle.HoursWorked, 6);
        Assert.Equal(2, working.Level);
    }

    [Fact]
    public async Task ALevelLiftsTheRate()
    {
        var (_, site, trade) = await WorldAsync();
        var worker = await WorkerAsync(trade);
        await Hiring.AssignAsync(_realm, Player, worker.Id, site.SiteId);
        double fresh = worker.RatePerHour;

        _now += TimeSpan.FromHours(7);
        await Hiring.SettlePlayerAsync(_realm, Player);

        Assert.Equal(fresh * 1.04, worker.RatePerHour, 6);
    }

    [Fact]
    public async Task ARollLevelRollsOnce_AndIsRevealedUntilSeen()
    {
        var (_, site, trade) = await WorldAsync();
        var worker = await WorkerAsync(trade);
        await Hiring.AssignAsync(_realm, Player, worker.Id, site.SiteId);

        _now += TimeSpan.FromHours(20);
        await Hiring.SettlePlayerAsync(_realm, Player);
        var first = Assert.Single(worker.RollList);
        Assert.Equal(3, first.Level);
        Assert.NotNull(first.Perk);

        _now += TimeSpan.FromHours(1);
        await Hiring.SettlePlayerAsync(_realm, Player);
        Assert.Single(worker.RollList);
        Assert.Equal(0, worker.RollsSeen);

        await Hiring.MarkRollsSeenAsync(_realm, Player);
        Assert.Equal(1, worker.RollsSeen);
    }

    [Fact]
    public async Task ABackfilledWorkerRollsEveryLevelItCrossed()
    {
        var (_, site, trade) = await WorldAsync();
        var worker = await WorkerAsync(trade);
        await Hiring.AssignAsync(_realm, Player, worker.Id, site.SiteId);
        worker.HoursWorked = 100; // as the migration sets it: level 6
        await _db.SaveChangesAsync();

        _now += TimeSpan.FromMinutes(1);
        await Hiring.SettlePlayerAsync(_realm, Player);

        Assert.Equal(new[] { 3, 5, 6 }, worker.RollList.Select(r => r.Level));
        Assert.Equal(6, worker.LevelRolledTo);
    }

    [Fact]
    public async Task AMentorTeachesTheOthersAtTheSite()
    {
        var (_, site, trade) = await WorldAsync();
        var mentor = await WorkerAsync(trade, WorkerTier.Local, WorkerPerk.Mentor);
        var pupil = await WorkerAsync(trade);
        await Hiring.AssignAsync(_realm, Player, mentor.Id, site.SiteId);
        await Hiring.AssignAsync(_realm, Player, pupil.Id, site.SiteId);
        double mentorBefore = mentor.HoursWorked;

        _now += TimeSpan.FromHours(4);
        await Hiring.SettlePlayerAsync(_realm, Player);

        Assert.Equal(4 * WorkerLevelRules.MentorFactor, pupil.HoursWorked, 6);
        Assert.Equal(mentorBefore + 4, mentor.HoursWorked, 6); // not their own pupil
    }

    [Fact]
    public async Task KeenEyesFindsArePaidIntoTheWallet()
    {
        var (region, site, trade) = await WorldAsync();
        var worker = await WorkerAsync(trade, WorkerTier.Local, WorkerPerk.KeenEye);
        await Hiring.AssignAsync(_realm, Player, worker.Id, site.SiteId);
        var from = _now;
        var before = worker.Sheet(); // one long settle finds with the worker as they started it

        _now += TimeSpan.FromHours(96);
        await Hiring.SettlePlayerAsync(_realm, Player);

        int expected = WorkerLevelRules.Finds(before, worker.Id.ToString(), from, _now, region.Biome).GetValueOrDefault(QuestRules.Essence);
        Assert.True(expected > 0);
        Assert.Equal(expected, (await Wallet.ReadAsync(_realm, Player)).Where(m => m.MaterialName == QuestRules.Essence).Sum(m => m.Quantity));
        Assert.True(worker.LifetimeOutput > 0);
    }

    [Fact]
    public async Task KeepIsCappedAtTwo()
    {
        var (_, _, trade) = await WorldAsync();
        var a = await WorkerAsync(trade); var b = await WorkerAsync(trade); var c = await WorkerAsync(trade);

        Assert.True((await Hiring.KeepAsync(_realm, Player, a.Id, true)).Succeeded);
        Assert.True((await Hiring.KeepAsync(_realm, Player, b.Id, true)).Succeeded);
        Assert.Equal(HiringError.KeepFull, (await Hiring.KeepAsync(_realm, Player, c.Id, true)).Error);
        Assert.True((await Hiring.KeepAsync(_realm, Player, a.Id, false)).Succeeded);
        Assert.True((await Hiring.KeepAsync(_realm, Player, c.Id, true)).Succeeded);
    }

    [Fact]
    public async Task DismissingFreesTheBed()
    {
        var (_, site, trade) = await WorldAsync();
        var worker = await WorkerAsync(trade);
        await Hiring.AssignAsync(_realm, Player, worker.Id, site.SiteId);
        Assert.Equal(1, (await Hiring.BedsAsync(_realm, Player)).Used);

        Assert.True((await Hiring.DismissAsync(_realm, Player, worker.Id)).Succeeded);

        Assert.Equal(0, (await Hiring.BedsAsync(_realm, Player)).Used);
        Assert.Empty(await Hiring.WorkersAsync(_realm, Player));
    }

    [Fact]
    public async Task LosingTheLandKeepsTheCarriedFraction()
    {
        var (region, site, trade) = await WorldAsync();
        var worker = await WorkerAsync(trade);
        await Hiring.AssignAsync(_realm, Player, worker.Id, site.SiteId);

        _now += TimeSpan.FromHours(2.3); // 27.6 goods: a fraction to carry
        await Hiring.SettlePlayerAsync(_realm, Player);
        double carry = worker.Carry;
        Assert.True(carry > 0);

        region.OwnerUserId = TestIds.Rival;
        _now += TimeSpan.FromSeconds(1);
        await Hiring.SettleAllAsync(_realm, new[] { region }, _now);

        Assert.Null(worker.SiteId);
        Assert.True(worker.Carry >= carry);
    }

    [Fact]
    public async Task ASeasonEndKeepsTwoVeteransAtHalfTheirLevel_PerksAboveItDropped()
    {
        var (_, _, trade) = await WorldAsync();
        var veteran = await WorkerAsync(trade);
        var middling = await WorkerAsync(trade);
        var favourite = await WorkerAsync(trade);
        await Hiring.DebugSetLevelAsync(_realm, Player, veteran.Id, 9);
        await Hiring.DebugSetLevelAsync(_realm, Player, middling.Id, 4);
        await Hiring.KeepAsync(_realm, Player, favourite.Id, true);
        var tierAtNine = veteran.Tier;
        var traitsAtNine = veteran.Traits;
        Assert.Equal(new[] { 3, 5, 6, 9 }, veteran.RollList.Select(r => r.Level));

        await HiringService.ResetRealmAsync(_db, _realm);
        await _db.SaveChangesAsync();

        var left = await _db.HiredWorkers.Where(w => w.GameInstanceId == _realm).ToListAsync();
        Assert.Equal(new[] { favourite.Id, veteran.Id }.OrderBy(i => i), left.Select(w => w.Id).OrderBy(i => i));
        Assert.Equal(4, veteran.Level);
        Assert.Equal(WorkerLevelRules.HoursAt(4), veteran.HoursWorked, 6);
        Assert.Equal(new[] { 3 }, veteran.RollList.Select(r => r.Level));
        Assert.Equal(tierAtNine, veteran.Tier);          // promotions kept
        Assert.Equal(traitsAtNine, veteran.Traits);
        Assert.Equal(1, veteran.SeasonsServed);
        Assert.Equal(veteran.RollList.Count, veteran.RollsSeen);
        Assert.Null(veteran.SiteId);
    }

    [Fact]
    public async Task DebugHoursRollTheLevelsCrossed()
    {
        var (_, _, trade) = await WorldAsync();
        var worker = await WorkerAsync(trade);
        HiringService.DebugForcePromotion(Player, true);

        await Hiring.DebugAddHoursAsync(_realm, Player, 60);

        Assert.Equal(5, worker.Level);
        Assert.Contains(worker.RollList, r => r.Level == 5 && r.Kind == WorkerRollKind.Promotion);
        Assert.Equal(WorkerTier.Skilled, worker.Tier);
    }
}
