using Enums;
using Items.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// First Steps (design 12c). What is pinned: the chest is for all six steps and only once; it holds a
/// Rare piece the ledger knows the player was granted; progress is per world, so a second world (and
/// a reset one) has its own chest; and a client may only report the steps that grant nothing.
/// </summary>
public class FirstStepsTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private static readonly Guid Emberhold = Guid.NewGuid();
    private static readonly Guid Frostmere = Guid.NewGuid();
    private const string Player = "player";

    private readonly FakeGameContent _content = new()
    {
        DroppableItems = new[]
        {
            new ItemTemplate { ItemName = "Cinder Blade", ItemType = ItemTypes.Weapon },
        }
    };

    private readonly FakeSessionLog _log = new();

    public FirstStepsTests()
    {
        _db.GameInstances.Add(new GameInstance { Id = Emberhold, Name = "Emberhold", OwnerId = Player });
        _db.GameInstances.Add(new GameInstance { Id = Frostmere, Name = "Frostmere", OwnerId = Player });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private FirstStepsService Steps =>
        new(_db, _content, new ItemLedgerService(_db, _log, NullLogger<ItemLedgerService>.Instance), _log);

    private async Task DoAll(Guid realm)
    {
        foreach (var step in FirstStepsRules.Steps) await Steps.RecordAsync(realm, Player, step);
    }

    [Fact]
    public async Task TheChestWaitsForAllSix()
    {
        foreach (var step in FirstStepsRules.Steps.Take(5)) await Steps.RecordAsync(Emberhold, Player, step);

        var (error, item) = await Steps.OpenChestAsync(Emberhold, Player);

        Assert.Equal(FirstStepsError.NotFinished, error);
        Assert.Null(item);
    }

    [Fact]
    public async Task AllSixOpenARareChest_ThatTheLedgerGranted()
    {
        await DoAll(Emberhold);

        var (error, item) = await Steps.OpenChestAsync(Emberhold, Player);

        Assert.Equal(FirstStepsError.None, error);
        Assert.Equal(ItemRarityTypes.Rare, item!.Rarity);
        Assert.True(await _db.ItemGrants.AnyAsync(g => g.GameInstanceId == Emberhold && g.UserId == Player && g.ItemId == item.Id),
            "an item the ledger did not record would vanish at the next save");
    }

    [Fact]
    public async Task TheChestOpensOnce()
    {
        await DoAll(Emberhold);
        await Steps.OpenChestAsync(Emberhold, Player);

        var (error, item) = await Steps.OpenChestAsync(Emberhold, Player);

        Assert.Equal(FirstStepsError.AlreadyOpened, error);
        Assert.Null(item);
        Assert.Equal(1, await _db.ItemGrants.CountAsync(g => g.UserId == Player));
    }

    [Fact]
    public async Task EveryWorldHasItsOwnChest()
    {
        await DoAll(Emberhold);
        await Steps.OpenChestAsync(Emberhold, Player);

        var (beforeSteps, _) = await Steps.OpenChestAsync(Frostmere, Player);
        Assert.Equal(FirstStepsError.NotFinished, beforeSteps);

        await DoAll(Frostmere);
        var (error, _) = await Steps.OpenChestAsync(Frostmere, Player);
        Assert.Equal(FirstStepsError.None, error);
    }

    [Fact]
    public async Task ASeasonResetStartsTheStepsOver()
    {
        await DoAll(Emberhold);
        await Steps.OpenChestAsync(Emberhold, Player);

        await FirstStepsService.ResetRealmAsync(_db, Emberhold);
        await _db.SaveChangesAsync();

        Assert.Null(await Steps.ReadAsync(Emberhold, Player));
    }

    [Fact]
    public async Task RecordingTwiceIsOneStep()
    {
        await Steps.RecordAsync(Emberhold, Player, FirstStepsRules.Hire);
        await Steps.RecordAsync(Emberhold, Player, FirstStepsRules.Hire);
        await Steps.RecordAsync(Emberhold, Player, "not-a-step");

        var progress = await Steps.ReadAsync(Emberhold, Player);
        Assert.Equal(new[] { FirstStepsRules.Hire }, progress!.DoneSteps);
    }

    [Fact]
    public void AClientMayOnlyReportTheStepsThatGrantNothing()
    {
        Assert.True(FirstStepsRules.MayClientReport(FirstStepsRules.Company));
        Assert.True(FirstStepsRules.MayClientReport(FirstStepsRules.Equip));
        foreach (var step in new[] { FirstStepsRules.Hire, FirstStepsRules.March, FirstStepsRules.Clear, FirstStepsRules.Garrison })
            Assert.False(FirstStepsRules.MayClientReport(step), step);
    }
}
