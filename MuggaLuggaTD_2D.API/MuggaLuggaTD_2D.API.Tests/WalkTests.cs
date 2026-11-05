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
/// Walking a region on foot (Exploration spec, phase 1): a walked arrival stands the company at the site like
/// a march's, never sooner than the walk could take (<see cref="WalkRules"/>), and without rolling an ambush.
/// </summary>
public class WalkTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeGameContent _content = new();

    private GoldService Gold => new(_db, new FakeSessionLog(), NullLogger<GoldService>.Instance);
    private MaterialWalletService Wallet => new(_db, new FakeSessionLog(), NullLogger<MaterialWalletService>.Instance);
    private TavernService Tavern => new(_db, _content, Wallet, Gold, new FakeSessionLog(), NullLogger<TavernService>.Instance);
    private ItemLedgerService Items => new(_db, new FakeSessionLog(), NullLogger<ItemLedgerService>.Instance);

    private PartyService Service =>
        new(_db, Tavern, _content, Wallet, Gold, new FakeSessionLog(), NullLogger<PartyService>.Instance, Items);

    public void Dispose() => _db.Dispose();

    private static PartyTravelRequest At(string siteId) => new(siteId, SharedContract.Version);

    /// <summary>A player's capital with its first company at the keep, and the region's farthest site from it on foot.</summary>
    private async Task<(Guid Instance, PartyDto Company, string Keep, string Far, double Cells)> SeedAsync()
    {
        var instance = await _db.AddInstanceAsync();
        var region = TestWorld.OwnedBy(TestIds.Player, "r1");
        region.IsCapital = true;
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(region));
        var save = TestSave.Roster(Enumerable.Range(1, 4).Select(i => TestSave.Character($"hero-{i}")).ToArray());
        save.ActiveCharacterIds = new List<string> { "hero-1", "hero-2", "hero-3" };
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, TestSave.ToJson(save));

        var (_, list) = await Service.ListAsync(instance.Id, TestIds.Player);
        var company = list!.Parties[0];
        var keep = company.SiteId!;

        var layout = RegionGenerator.Generate(region);
        var roads = RegionRoadNetwork.For(region, _ => false);
        var from = layout.FindSite(keep)!.Cell;
        var far = layout.Sites
            .Where(s => s.SiteId != keep)
            .Select(s => (s.SiteId, Cells: WalkRules.CellsBetween(roads, from, s.Cell)))
            .Where(s => s.Cells.HasValue)
            .OrderByDescending(s => s.Cells!.Value)
            .First();
        return (instance.Id, company, keep, far.SiteId, far.Cells!.Value);
    }

    private async Task BackdateAsync(Guid partyId, TimeSpan by)
    {
        var party = await _db.PlayerParties.SingleAsync(p => p.Id == partyId);
        party.UpdatedAt = DateTime.UtcNow - by;
        await _db.SaveChangesAsync();
    }

    [Fact]
    public void TheFloorIsTheShortestWalkAtThePace_LessTheSlack()
    {
        Assert.Equal(TimeSpan.Zero, WalkRules.Floor(1));
        Assert.Equal(30 / WalkRules.CellsPerSecond - WalkRules.SlackSeconds, WalkRules.Floor(30).TotalSeconds, 3);
    }

    [Fact]
    public async Task AnArrivalSoonerThanTheWalkCouldTake_IsRefused()
    {
        var (instance, company, keep, far, cells) = await SeedAsync();
        Assert.True(WalkRules.Floor(cells) > TimeSpan.FromSeconds(3), $"the farthest site is only {cells:F1} cells away");

        var (begun, _) = await Service.WalkBeginAsync(instance, TestIds.Player, company.Id, At(keep));
        var (arrived, _) = await Service.WalkArriveAsync(instance, TestIds.Player, company.Id, At(far));

        Assert.True(begun.Succeeded, begun.Message);
        Assert.Equal(PartyError.RunTooFast, arrived.Error);
        Assert.Equal(keep, (await _db.PlayerParties.AsNoTracking().SingleAsync(p => p.Id == company.Id)).SiteId);
    }

    [Fact]
    public async Task AWalkedArrival_StandsTheCompanyThere_AsAMarchWould()
    {
        var (instance, company, keep, far, cells) = await SeedAsync();
        await Service.WalkBeginAsync(instance, TestIds.Player, company.Id, At(keep));
        await BackdateAsync(company.Id, WalkRules.Floor(cells) + TimeSpan.FromSeconds(1));

        var (arrived, response) = await Service.WalkArriveAsync(instance, TestIds.Player, company.Id, At(far));

        Assert.True(arrived.Succeeded, arrived.Message);
        var dto = response!.Parties.Single(p => p.Id == company.Id);
        Assert.Equal(CompanyState.Idle, dto.State);
        Assert.Equal(far, dto.SiteId);
        Assert.Null(dto.Journey);
        var row = await _db.PlayerParties.AsNoTracking().SingleAsync(p => p.Id == company.Id);
        Assert.Null(row.AmbushAt);
    }

    [Fact]
    public async Task SettingOutStampsTheClock_SoIdleTimeIsNotBanked()
    {
        var (instance, company, keep, far, _) = await SeedAsync();
        await BackdateAsync(company.Id, TimeSpan.FromHours(1));

        await Service.WalkBeginAsync(instance, TestIds.Player, company.Id, At(keep));
        var (arrived, _) = await Service.WalkArriveAsync(instance, TestIds.Player, company.Id, At(far));

        Assert.Equal(PartyError.RunTooFast, arrived.Error);
    }

    [Fact]
    public async Task ACompanyOnTheRoad_CannotWalk()
    {
        var (instance, company, keep, far, _) = await SeedAsync();
        var (sent, _) = await Service.TravelAsync(instance, TestIds.Player, company.Id, At(far));
        Assert.True(sent.Succeeded, sent.Message);

        var (begun, _) = await Service.WalkBeginAsync(instance, TestIds.Player, company.Id, At(keep));
        var (arrived, _) = await Service.WalkArriveAsync(instance, TestIds.Player, company.Id, At(far));

        Assert.Equal(PartyError.Busy, begun.Error);
        Assert.Equal(PartyError.Busy, arrived.Error);
    }

    [Fact]
    public async Task ACompanyCanOnlySetOutFromWhereItStands()
    {
        var (instance, company, _, far, _) = await SeedAsync();

        var (begun, _) = await Service.WalkBeginAsync(instance, TestIds.Player, company.Id, At(far));

        Assert.Equal(PartyError.NotInThisRegion, begun.Error);
    }

    [Fact]
    public async Task AStaleClientIsRefused()
    {
        var (instance, company, keep, _, _) = await SeedAsync();

        var (begun, _) = await Service.WalkBeginAsync(instance, TestIds.Player, company.Id, new PartyTravelRequest(keep, "0.0.1"));

        Assert.Equal(PartyError.ContractMismatch, begun.Error);
    }
}
