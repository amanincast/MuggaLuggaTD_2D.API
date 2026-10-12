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
/// Rifts, phase 7 (polish). A rift rolls from all eight mutators; the Rift Heart's Rift Lord brings a third that is never
/// one already on, and has a name fixed by its rift; a sealed Great Rift leaves a scar for the season; its top three are
/// its Wardens on the standings.
/// </summary>
public class RiftPolishTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private readonly FakeHubContext _hub = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ARift_RollsFromAllEightMutators()
    {
        Assert.Equal(8, RiftRules.RolledMutators.Length);
        Assert.Equal(Enum.GetValues<RiftMutator>().Length, RiftRules.RolledMutators.Distinct().Count());

        var seen = Enumerable.Range(0, 400)
            .SelectMany(n => RiftRules.Mutators($"rift-{n}", RiftKind.Great, 2))
            .ToHashSet();
        Assert.Equal(8, seen.Count);
    }

    [Fact]
    public void TheRiftLord_WaitsInTheHeartAlone_AndBringsAThirdMutator()
    {
        Assert.True(RiftRules.HasRiftLord(RiftKind.Great, RiftRules.HeartIndex));
        Assert.False(RiftRules.HasRiftLord(RiftKind.Great, 0));
        Assert.False(RiftRules.HasRiftLord(RiftKind.Great, 1));
        Assert.False(RiftRules.HasRiftLord(RiftKind.Lesser, 0));

        for (int n = 0; n < 200; n++)
        {
            string id = $"rift-{n}";
            var heart = RiftRules.Mutators(id, RiftKind.Great, RiftRules.HeartIndex);
            Assert.Equal(2, heart.Count);
            Assert.DoesNotContain(RiftRules.LordMutator(id), heart);
            Assert.Equal(RiftRules.LordMutator(id), RiftRules.LordMutator(id.ToUpperInvariant()));
        }
    }

    [Fact]
    public void TheRiftLord_IsNamedForItsRift()
    {
        string name = Naming.ForRiftLord("rift-7");
        Assert.StartsWith("The ", name);
        Assert.Contains(" of ", name);
        Assert.Equal(name, Naming.ForRiftLord("rift-7"));
        Assert.True(Enumerable.Range(0, 50).Select(n => Naming.ForRiftLord($"rift-{n}")).Distinct().Count() > 10);
    }

    private async Task<Guid> RealmWithRiftsAsync()
    {
        var instance = await _db.AddInstanceAsync();
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(TestWorld.Region("r1"), TestWorld.Region("r2", 1, 0), TestWorld.Region("r3", 2, 0)));

        Rift Add(string region, RiftKind kind, RiftStage stage, int number)
        {
            var rift = new Rift
            {
                GameInstanceId = instance.Id, SeasonNumber = instance.SeasonNumber, Kind = kind, EventNumber = number,
                RegionId = region, Stage = stage, OmenAt = DateTime.UtcNow.AddDays(-3), OpensAt = DateTime.UtcNow.AddDays(-3),
                ClosesAt = DateTime.UtcNow.AddDays(-3).AddHours(4), BreachMax = 1200, Breach = 0,
                SealedAt = stage == RiftStage.Sealed ? DateTime.UtcNow.AddDays(-3) : null,
            };
            _db.Rifts.Add(rift);
            return rift;
        }

        var sealedGreat = Add("r1", RiftKind.Great, RiftStage.Sealed, 1);
        Add("r2", RiftKind.Great, RiftStage.Erupted, 2);
        Add("r3", RiftKind.Lesser, RiftStage.Sealed, 1);
        var other = Add("r2", RiftKind.Great, RiftStage.Sealed, 3);

        // Five fought the first: the top three by Breach are its Wardens. Two fought the second.
        int breach = 500;
        foreach (var user in new[] { "a", "b", "c", "d", "e" })
            _db.RiftContributions.Add(new RiftContribution { RiftId = sealedGreat.Id, UserId = user, Breach = breach -= 80 });
        _db.RiftContributions.Add(new RiftContribution { RiftId = other.Id, UserId = "a", Breach = 900 });
        _db.RiftContributions.Add(new RiftContribution { RiftId = other.Id, UserId = "e", Breach = 300 });
        await _db.SaveChangesAsync();
        return instance.Id;
    }

    [Fact]
    public async Task ASealedGreatRift_LeavesAScar_ForTheSeason()
    {
        var realm = await RealmWithRiftsAsync();
        var rifts = new RiftService(_db, new FakeGameContent(), new ItemLedgerService(_db, _log, NullLogger<ItemLedgerService>.Instance),
            _log, NullLogger<RiftService>.Instance, _hub, new RealmPresence());

        var view = await rifts.ViewAsync(realm, "a");

        // Long past the day the "recent" list keeps, the scars are still there; erupted and Lesser rifts leave none.
        Assert.Empty(view.Recent);
        Assert.Equal(new[] { "r1", "r2" }, view.Scars!.OrderBy(s => s));
    }

    [Fact]
    public async Task ASealedGreatRiftsTopThree_AreItsWardens()
    {
        var realm = await RealmWithRiftsAsync();
        var season = (await _db.GameInstances.SingleAsync(g => g.Id == realm)).SeasonNumber;

        var wardens = await RiftService.WardensAsync(_db, realm, season);

        Assert.Equal(2, wardens["a"]);
        Assert.Equal(1, wardens["b"]);
        Assert.Equal(1, wardens["c"]);
        Assert.False(wardens.ContainsKey("d"));
        Assert.Equal(1, wardens["e"]);
    }
}
