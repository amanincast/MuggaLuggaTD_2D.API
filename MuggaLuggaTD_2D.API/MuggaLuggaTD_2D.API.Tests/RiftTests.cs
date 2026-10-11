using Enums;
using Items.Models;
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
/// Rifts from other realms, phase 1 (Invasions spec R1–R7). What a player should be able to rely on:
/// rifts come at random but fixed times, always with their full warning; a Great Rift opens away from
/// capitals and erupts if nobody seals it; a Lesser Rift only opens on an online player's land, never a
/// capital, and invades it if they do not close it, until they do; sealing pays once.
/// </summary>
public class RiftTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private readonly FakeHubContext _hub = new();
    private DateTime _now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
    private readonly RealmPresence _presence = new();
    private Guid _realm;

    private readonly FakeGameContent _content = new()
    {
        DroppableItems = new[] { new ItemTemplate { ItemName = "Cinder Blade", ItemType = ItemTypes.Weapon } }
    };

    public RiftTests() => _presence.Clock = () => _now;

    public void Dispose() => _db.Dispose();

    private RiftService Rifts()
    {
        var clock = new FakeClock(_now);
        var letters = new LetterService(_db, _hub, NullLogger<LetterService>.Instance, clock);
        var warLog = new WarLogService(_db, _hub, NullLogger<WarLogService>.Instance, clock, letters);
        var items = new ItemLedgerService(_db, _log, NullLogger<ItemLedgerService>.Instance);
        return new RiftService(_db, _content, items, _log, NullLogger<RiftService>.Instance, _hub, _presence, warLog, letters)
        {
            Clock = () => _now
        };
    }

    /// <summary>
    /// A small map: a capital at the centre held by the player, their land in ring 2, the rival's in ring 3,
    /// and wild land in rings 1–4. The season began a day ago, and both players played today.
    /// </summary>
    private async Task RealmAsync()
    {
        var instance = await _db.AddInstanceAsync();
        instance.SeasonStartedAt = _now.AddDays(-1);
        instance.SeasonLengthDays = 30;
        _realm = instance.Id;

        var capital = TestWorld.OwnedBy(TestIds.Player, "r0");
        capital.IsCapital = true;
        await _db.AddWorldAsync(_realm, TestWorld.Blob(
            capital,
            TestWorld.Region("r1", 1, 0),
            TestWorld.Region("r2", 2, 0, LocationOwnership.Player, TestIds.Player),
            TestWorld.Region("r3", -2, 0),
            TestWorld.Region("r4", 0, -3, LocationOwnership.Player, TestIds.Rival),
            TestWorld.Region("r5", 0, 4)));

        foreach (var user in new[] { TestIds.Player, TestIds.Rival })
        {
            _db.Users.Add(new ApplicationUser { Id = user, UserName = user, DisplayName = user });
            _db.PlayerGameData.Add(new PlayerGameData { GameInstanceId = _realm, UserId = user, UpdatedAt = _now.AddHours(-1) });
        }
        await _db.SaveChangesAsync();
    }

    private async Task<List<Rift>> RiftsAsync(RiftKind kind) =>
        await _db.Rifts.AsNoTracking().Where(r => r.GameInstanceId == _realm && r.Kind == kind).ToListAsync();

    private async Task<List<string>> WarLogKindsAsync() =>
        await _db.WarLog.AsNoTracking().Where(w => w.GameInstanceId == _realm).OrderBy(w => w.RecordedTicks).Select(w => w.Kind).ToListAsync();

    private async Task<List<Letter>> LettersToAsync(string user) =>
        await _db.Letters.AsNoTracking().Where(l => l.GameInstanceId == _realm && l.UserId == user).ToListAsync();

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Fact]
    public void ARiftsTime_IsRandomButFixed_AndInItsKindsRange()
    {
        var start = _now.AddDays(-3);
        var times = Enumerable.Range(1, 40)
            .Select(n => RiftRules.DueToOpen(RiftKind.Lesser, "realm-a", n, start, _now, null) - _now)
            .ToList();

        Assert.All(times, gap => Assert.InRange(gap.TotalMinutes, RiftRules.LesserGapMinMinutes, RiftRules.LesserGapMaxMinutes - 1));
        Assert.True(times.Distinct().Count() > 10, "the gaps should vary");
        Assert.Equal(RiftRules.DueToOpen(RiftKind.Lesser, "realm-a", 7, start, _now, null),
            RiftRules.DueToOpen(RiftKind.Lesser, "REALM-A", 7, start, _now, null));
        Assert.NotEqual(RiftRules.DueToOpen(RiftKind.Great, "realm-a", 1, start, null, null),
            RiftRules.DueToOpen(RiftKind.Great, "realm-b", 1, start, null, null));
    }

    [Fact]
    public void GreatRifts_AverageFourADay_AndLesserRiftsSixToEight()
    {
        double Average(RiftKind kind) => Enumerable.Range(1, 2000)
            .Average(n => (RiftRules.DueToOpen(kind, "realm", n, _now, _now, null) - _now).TotalHours);

        Assert.InRange(24 / Average(RiftKind.Great), 3.5, 4.5);
        Assert.InRange(24 / Average(RiftKind.Lesser), 6, 8.5);
    }

    [Fact]
    public void AGreatRift_NeverOpensWhileTheLastCouldStillBeOpen()
    {
        for (int n = 1; n < 50; n++)
        {
            var closes = _now + RiftRules.GreatOpenFor;
            Assert.True(RiftRules.DueToOpen(RiftKind.Great, "realm", n, _now.AddDays(-1), _now, closes) >= closes);
        }
    }

    [Fact]
    public void AnOverdueRift_StillGetsItsWholeOmen()
    {
        var due = _now.AddHours(-5);
        Assert.Equal(_now + RiftRules.GreatOmenLead, RiftRules.OpensAt(RiftKind.Great, due, _now));
        Assert.Equal(_now + RiftRules.LesserOmenLead, RiftRules.OpensAt(RiftKind.Lesser, due, _now));

        var onTime = _now.AddHours(1);
        Assert.Equal(onTime, RiftRules.OpensAt(RiftKind.Great, onTime, _now));
    }

    [Fact]
    public void NoRift_InASeasonsFirstTwoHours_OrLastSix()
    {
        var start = _now;
        var end = _now.AddDays(7);
        Assert.False(RiftRules.InSeason(start.AddHours(1), start, end));
        Assert.True(RiftRules.InSeason(start.AddHours(3), start, end));
        Assert.False(RiftRules.InSeason(end.AddHours(-5), start, end));
        Assert.True(RiftRules.InSeason(end.AddHours(-7), start, end));
    }

    [Fact]
    public void AGreatRift_NeverOpensInOrBesideACapital_AndPrefersRingsTwoToFour()
    {
        var capital = TestWorld.OwnedBy("p", "r0");
        capital.IsCapital = true;
        var regions = new List<WorldRegionData>
        {
            capital,
            TestWorld.Region("beside", 1, 0),
            TestWorld.Region("ring2", 2, 0),
            TestWorld.Region("ring3", -3, 0),
            TestWorld.Region("ring6", 6, 0),
        };

        var ids = RiftRules.GreatCandidates(regions).Select(c => c.Region.RegionId).ToList();
        Assert.Equal(new[] { "ring2", "ring3" }, ids.OrderBy(i => i));
        for (int n = 1; n < 30; n++)
            Assert.Contains(RiftRules.PickGreat(regions, "realm", n)!.RegionId, ids);
    }

    [Fact]
    public void AGreatRift_LeansTowardLandBesideAPlayer()
    {
        var regions = new List<WorldRegionData>
        {
            TestWorld.Region("near", 2, 0),
            TestWorld.OwnedBy("p", "held"),
            TestWorld.Region("far", -3, 0),
        };
        regions[1].Hex = new HexCoord(3, 0);

        var weights = RiftRules.GreatCandidates(regions).ToDictionary(c => c.Region.RegionId, c => c.Weight);
        Assert.Equal(RiftRules.BordersPlayerWeight, weights["near"]);
        Assert.Equal(1, weights["far"]);
    }

    [Fact]
    public void ALesserRift_OnlyOpensOnAnOnlinePlayersLand_NeverACapital_OneAtATime()
    {
        var capital = TestWorld.OwnedBy("online", "cap");
        capital.IsCapital = true;
        var regions = new List<WorldRegionData>
        {
            capital,
            TestWorld.OwnedBy("online", "a"),
            TestWorld.OwnedBy("asleep", "b"),
            TestWorld.OwnedBy("busy", "c"),
            TestWorld.Region("wild"),
        };

        var ids = RiftRules.LesserCandidates(regions, new[] { "online", "busy" }, new[] { "busy" })
            .Select(r => r.RegionId).ToList();
        Assert.Equal(new[] { "a" }, ids);
    }

    [Fact]
    public void ALesserRift_PicksThePlayerFirst_SoMoreLandDoesNotBringMoreRifts()
    {
        var regions = Enumerable.Range(0, 9).Select(i => TestWorld.OwnedBy("big", "b" + i))
            .Append(TestWorld.OwnedBy("small", "s0"))
            .ToList();

        int small = Enumerable.Range(1, 400).Count(n => RiftRules.PickLesser(regions, "realm", n)!.OwnerUserId == "small");
        Assert.InRange(small, 140, 260);
    }

    [Theory]
    [InlineData(0.30, 1, ItemRarityTypes.Legendary)]
    [InlineData(0.20, 1, ItemRarityTypes.Rare)]
    [InlineData(0.02, 3, ItemRarityTypes.Rare)]
    [InlineData(0.26, 5, ItemRarityTypes.Rare)]
    [InlineData(0.10, 4, ItemRarityTypes.Magic)]
    [InlineData(0.01, 9, ItemRarityTypes.Uncommon)]
    public void AGreatRiftsChest_GrowsWithTheShare_AndTheTopThree(double share, int rank, ItemRarityTypes chest)
    {
        Assert.Equal(chest, RiftRules.GreatChestFor(share, rank));
    }

    [Fact]
    public void TheBreach_GrowsWithTheRealm()
    {
        Assert.Equal(RiftRules.LeastBreach, RiftRules.BreachMax(1));
        Assert.Equal(6000, RiftRules.BreachMax(10));
    }

    // -----------------------------------------------------------------
    // A Great Rift's life
    // -----------------------------------------------------------------

    [Fact]
    public async Task AGreatRift_IsAnnounced_Opens_AndErupts_WithTheWarLogAndItsNeighboursTold()
    {
        await RealmAsync();

        await Rifts().AdvanceAsync(_realm);
        var rift = Assert.Single(await RiftsAsync(RiftKind.Great));
        Assert.Equal(RiftStage.Omen, rift.Stage);
        Assert.Equal(_now + RiftRules.GreatOmenLead, rift.OpensAt);
        Assert.Equal(RiftRules.BreachMax(2), rift.BreachMax);
        Assert.NotEqual("r0", rift.RegionId);
        Assert.NotEqual("r1", rift.RegionId);

        // A second sweep in the same minute announces nothing more.
        await Rifts().AdvanceAsync(_realm);
        Assert.Single(await RiftsAsync(RiftKind.Great));

        _now = rift.OpensAt;
        await Rifts().AdvanceAsync(_realm);
        Assert.Equal(RiftStage.Open, (await RiftsAsync(RiftKind.Great)).Single().Stage);

        _now = rift.ClosesAt;
        await Rifts().AdvanceAsync(_realm);
        Assert.Equal(RiftStage.Erupted, (await RiftsAsync(RiftKind.Great)).Single(r => r.Id == rift.Id).Stage);

        // The first rift's own lines. The next Great Rift may be announced the moment this one ends (its gap is
        // rolled from the realm's random id), which is right, so only the first three are this rift's.
        var log = await WarLogKindsAsync();
        Assert.Equal(new[] { "RiftOmen", "RiftOpened", "RiftErupted" }, log.Where(k => k.StartsWith("Rift")).Take(3));
        Assert.Contains(_hub.Sent, s => s.Method == "RiftUpdated");
    }

    [Fact]
    public async Task AGreatRift_TellsThePlayersWhoseLandBordersIt()
    {
        await RealmAsync();
        await Rifts().AdvanceAsync(_realm);
        var rift = (await RiftsAsync(RiftKind.Great)).Single();

        var world = WorldRegionBlob.ReadAllRegions((await _db.ReadWorldAsync(_realm))!);
        var at = world.Single(r => r.RegionId == rift.RegionId).Hex;
        foreach (var user in new[] { TestIds.Player, TestIds.Rival })
        {
            bool borders = world.Any(r => r.OwnerUserId == user && HexCoord.Distance(r.Hex, at) <= 1);
            Assert.Equal(borders, (await LettersToAsync(user)).Any(l => l.Kind == nameof(LetterKind.RiftNear)));
        }
    }

    [Fact]
    public async Task OnlyOneGreatRift_IsLiveAtATime()
    {
        await RealmAsync();
        await Rifts().AdvanceAsync(_realm);

        // Long after the first was due to be followed, it is still open: no second.
        _now = _now.AddHours(1).AddMinutes(1);
        await Rifts().AdvanceAsync(_realm);
        Assert.Single(await RiftsAsync(RiftKind.Great));
    }

    [Fact]
    public async Task ARealmNobodyHasPlayedForADay_GetsNoRifts()
    {
        await RealmAsync();
        foreach (var save in _db.PlayerGameData) save.UpdatedAt = _now.AddDays(-2);
        await _db.SaveChangesAsync();

        await Rifts().AdvanceAsync(_realm);
        Assert.Empty(_db.Rifts);
    }

    [Fact]
    public async Task SealingAGreatRift_PaysEachShare_ByItsSizeAndPlace_Once()
    {
        await RealmAsync();
        var (rift, _) = await Rifts().DebugOpenAsync(_realm, TestIds.Player, RiftKind.Great, null);
        Assert.NotNull(rift);
        int whole = rift!.BreachMax;

        Assert.Equal(whole / 10, await Rifts().RecordWinAsync(rift.Id, TestIds.Rival, whole / 10));
        Assert.Equal(whole - whole / 10, await Rifts().RecordWinAsync(rift.Id, TestIds.Player, whole * 5));
        Assert.Equal(0, await Rifts().RecordWinAsync(rift.Id, TestIds.Rival, 100));

        var sealedRift = (await RiftsAsync(RiftKind.Great)).Single();
        Assert.Equal(RiftStage.Sealed, sealedRift.Stage);
        Assert.Equal(TestIds.Player, sealedRift.SealedByUserId);

        var shares = await _db.RiftContributions.AsNoTracking().ToDictionaryAsync(c => c.UserId);
        Assert.Equal(ItemRarityTypes.Legendary, shares[TestIds.Player].ChestRarity);
        Assert.Equal(ItemRarityTypes.Rare, shares[TestIds.Rival].ChestRarity);
        Assert.Single(await LettersToAsync(TestIds.Rival), l => l.Kind == nameof(LetterKind.RiftSealed));
        Assert.Single(await WarLogKindsAsync(), k => k == "RiftSealed");
    }

    // -----------------------------------------------------------------
    // A Lesser Rift's life
    // -----------------------------------------------------------------

    [Fact]
    public async Task NoLesserRift_WhenNobodyIsOnline()
    {
        await RealmAsync();
        await Rifts().AdvanceAsync(_realm);
        Assert.Empty(await RiftsAsync(RiftKind.Lesser));
    }

    [Fact]
    public async Task ALesserRift_OpensOnTheOnlinePlayersOwnLand_AndTellsThem()
    {
        await RealmAsync();
        _presence.Joined("c1", _realm, TestIds.Player);

        await Rifts().AdvanceAsync(_realm);
        var rift = Assert.Single(await RiftsAsync(RiftKind.Lesser));
        Assert.Equal("r2", rift.RegionId);
        Assert.Equal(TestIds.Player, rift.TargetUserId);
        Assert.Equal(_now + RiftRules.LesserOmenLead, rift.OpensAt);
        Assert.Contains(await LettersToAsync(TestIds.Player), l => l.Kind == nameof(LetterKind.RiftOmen));

        // A Lesser Rift's omen is the player's news, not the realm's.
        Assert.DoesNotContain(await _db.WarLog.AsNoTracking().ToListAsync(), w => w.Kind == "RiftOmen" && w.Detail == "Lesser");
    }

    [Fact]
    public async Task APlayerWhoLeftHalfAnHourAgo_IsNoLongerOnline()
    {
        await RealmAsync();
        _presence.Joined("c1", _realm, TestIds.Player);
        _presence.Left("c1");
        _now = _now.AddMinutes(31);

        await Rifts().AdvanceAsync(_realm);
        Assert.Empty(await RiftsAsync(RiftKind.Lesser));
    }

    [Fact]
    public async Task AnUnclosedLesserRift_InvadesTheRegion_UntilItIsSealed()
    {
        await RealmAsync();
        _presence.Joined("c1", _realm, TestIds.Player);
        await Rifts().AdvanceAsync(_realm);
        var rift = (await RiftsAsync(RiftKind.Lesser)).Single();

        _now = rift.ClosesAt;
        await Rifts().AdvanceAsync(_realm);
        var invaded = (await RiftsAsync(RiftKind.Lesser)).Single(r => r.Id == rift.Id);
        Assert.Equal(RiftStage.Invaded, invaded.Stage);
        Assert.Equal(rift.ClosesAt, invaded.InvadedAt);
        Assert.Contains(await LettersToAsync(TestIds.Player), l => l.Kind == nameof(LetterKind.RegionInvaded));
        Assert.Contains("RegionInvaded", await WarLogKindsAsync());

        // Days later it is still invaded (Q6: only closing it ends it), and no second rift opens for them.
        _now = _now.AddDays(2);
        await Rifts().AdvanceAsync(_realm);
        Assert.Equal(RiftStage.Invaded, (await RiftsAsync(RiftKind.Lesser)).Single(r => r.Id == rift.Id).Stage);
        Assert.Single(await RiftsAsync(RiftKind.Lesser), r => r.TargetUserId == TestIds.Player);

        // And closing it, even now, pays the full chest (Mike 2026-10-10).
        Assert.Equal(1, await Rifts().RecordWinAsync(rift.Id, TestIds.Player, 999));
        var mine = await _db.RiftContributions.AsNoTracking().SingleAsync(c => c.RiftId == rift.Id);
        Assert.Equal(RiftRules.LesserChest, mine.ChestRarity);
        Assert.Equal(RiftStage.Sealed, (await RiftsAsync(RiftKind.Lesser)).Single(r => r.Id == rift.Id).Stage);
    }

    [Fact]
    public async Task ARiftChest_OpensOnce_AndItsLetterStopsWaving()
    {
        await RealmAsync();
        var (rift, _) = await Rifts().DebugOpenAsync(_realm, TestIds.Player, RiftKind.Lesser, null);
        await Rifts().RecordWinAsync(rift!.Id, TestIds.Player, 1);

        var letters = new LetterService(_db, _hub, NullLogger<LetterService>.Instance, new FakeClock(_now));
        var sealedLetter = (await LettersToAsync(TestIds.Player)).Single(l => l.Kind == nameof(LetterKind.RiftSealed));
        Assert.Contains(sealedLetter.Id, await letters.FlagsAsync(_realm, TestIds.Player, new[] { sealedLetter }));

        var (error, chest) = await Rifts().OpenChestAsync(_realm, TestIds.Player, rift.Id);
        Assert.Equal(RiftChestError.None, error);
        Assert.Equal(ItemRarityTypes.Magic, chest!.Rarity);
        Assert.NotEmpty(chest.Items);

        Assert.Equal(RiftChestError.AlreadyOpened, (await Rifts().OpenChestAsync(_realm, TestIds.Player, rift.Id)).Error);
        Assert.Equal(RiftChestError.NoChest, (await Rifts().OpenChestAsync(_realm, TestIds.Rival, rift.Id)).Error);
        Assert.Empty(await letters.FlagsAsync(_realm, TestIds.Player, new[] { sealedLetter }));
    }

    [Fact]
    public async Task TheView_ShowsLiveRifts_AndMyPart()
    {
        await RealmAsync();
        var (great, _) = await Rifts().DebugOpenAsync(_realm, TestIds.Player, RiftKind.Great, null);
        await Rifts().RecordWinAsync(great!.Id, TestIds.Rival, 100);

        var view = await Rifts().ViewAsync(_realm, TestIds.Rival);
        var shown = Assert.Single(view.Live, v => v.Id == great.Id);
        Assert.Equal(100, shown.MyBreach);
        Assert.Equal(great.BreachMax - 100, shown.Breach);
        Assert.True(shown.Top.Single().Me);
    }

    [Fact]
    public async Task ANewSeason_TakesTheRiftsWithTheOldMap()
    {
        await RealmAsync();
        var (rift, _) = await Rifts().DebugOpenAsync(_realm, TestIds.Player, RiftKind.Great, null);
        await Rifts().RecordWinAsync(rift!.Id, TestIds.Player, 10);

        await RiftService.ResetRealmAsync(_db, _realm);
        await _db.SaveChangesAsync();
        Assert.Empty(_db.Rifts);
        Assert.Empty(_db.RiftContributions);
    }
}
