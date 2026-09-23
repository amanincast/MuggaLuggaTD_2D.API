using Enums;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The Tavern: who is on the board, and what taking one of them costs.
///
/// <para>What is pinned here is the part a client could otherwise decide for itself — that the board
/// is the server's, that a slot is hired at most once, that the wallet is actually charged, and that
/// a hire leaves an entitlement record behind. The roll's shape is <see cref="RecruitRollerTests"/>.</para>
/// </summary>
public class TavernServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private static readonly Guid Realm = Guid.NewGuid();
    private const string Player = "player-1";

    private readonly FakeGameContent _content = new()
    {
        RecruitSheets = new List<RecruitSheet>
        {
            new() { Sheet = "Ally_Warrior_1", Class = "Warrior" }
        },
        Signatures = new List<SignatureDefinition>
        {
            new() { SignatureId = "warrior_cleave", Class = "Warrior", BaseAbilityLinkName = "Cleaving_Blow_1" }
        }
    };

    private MaterialWalletService Wallet =>
        new(_db, new FakeSessionLog(), NullLogger<MaterialWalletService>.Instance);

    private TavernService Service =>
        new(_db, _content, Wallet, new FakeSessionLog(), NullLogger<TavernService>.Instance);

    public void Dispose() => _db.Dispose();

    /// <summary>Enough of everything to afford any rarity.</summary>
    private async Task FillWalletAsync()
    {
        await Wallet.GrantAsync(Realm, Player, new List<MaterialGrant>
        {
            new() { MaterialName = "Lesser Essence", Quantity = 99 },
            new() { MaterialName = "Greater Essence", Quantity = 99 },
            new() { MaterialName = "Supreme Essence", Quantity = 99 },
            new() { MaterialName = "Rare Shard", Quantity = 99 },
            new() { MaterialName = "Legendary Shard", Quantity = 99 }
        }, "test");
    }

    // -----------------------------------------------------------------
    // The board
    // -----------------------------------------------------------------

    [Fact]
    public async Task AFirstVisitRollsABoard_SoTheRoomIsNeverEmpty()
    {
        var board = await Service.ReadBoardAsync(Realm, Player);

        Assert.Equal(TavernRules.BoardSize, board.Count);
        Assert.Equal(Enumerable.Range(0, TavernRules.BoardSize), board.Select(r => r.Slot));
    }

    [Fact]
    public async Task ReadingTheBoardTwice_DoesNotReRollIt()
    {
        var first = await Service.ReadBoardAsync(Realm, Player);
        var second = await Service.ReadBoardAsync(Realm, Player);

        // Otherwise every look at the room would be a free pull.
        Assert.Equal(first.Select(r => r.Id), second.Select(r => r.Id));
    }

    [Fact]
    public async Task ARestockReplacesTheWholeBoard()
    {
        var before = await Service.ReadBoardAsync(Realm, Player);

        await Service.RestockAsync(Realm, Player, locationTier: 3, biome: BiomeType.Volcanic, reason: "test");
        var after = await Service.ReadBoardAsync(Realm, Player);

        Assert.Equal(TavernRules.BoardSize, after.Count);
        Assert.Empty(after.Select(r => r.Id).Intersect(before.Select(r => r.Id)));
    }

    [Fact]
    public async Task OnePlayersBoardIsNotAnothers()
    {
        var mine = await Service.ReadBoardAsync(Realm, Player);
        var theirs = await Service.ReadBoardAsync(Realm, "player-2");

        Assert.Empty(mine.Select(r => r.Id).Intersect(theirs.Select(r => r.Id)));
    }

    [Fact]
    public async Task ABoardIsHeldPerRealm()
    {
        await Service.ReadBoardAsync(Realm, Player);
        var elsewhere = await Service.ReadBoardAsync(Guid.NewGuid(), Player);

        Assert.Equal(TavernRules.BoardSize, elsewhere.Count);
    }

    [Fact]
    public async Task ContentWithNothingRollable_LeavesTheOldBoardAlone()
    {
        var before = await Service.ReadBoardAsync(Realm, Player);

        _content.Signatures = new List<SignatureDefinition>();
        await Service.RestockAsync(Realm, Player, 1, null, "test");

        var after = await Service.ReadBoardAsync(Realm, Player);
        Assert.Equal(before.Select(r => r.Id), after.Select(r => r.Id));
    }

    // -----------------------------------------------------------------
    // Hiring
    // -----------------------------------------------------------------

    [Fact]
    public async Task HiringChargesTheWalletAndLeavesARecord()
    {
        await FillWalletAsync();
        var board = await Service.ReadBoardAsync(Realm, Player);
        var recruit = board[0];
        var cost = TavernRules.HireCost(recruit.Rarity);

        var (outcome, hired) = await Service.HireAsync(Realm, Player, 0);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(hired);
        Assert.Equal(recruit.SignatureId, hired!.SignatureId);
        Assert.Equal(recruit.Affinity, hired.Affinity);
        Assert.Equal(recruit.Rarity, hired.Rarity);
        Assert.False(string.IsNullOrEmpty(hired.CharacterId));

        var wallet = await Wallet.ReadAsync(Realm, Player);
        foreach (var line in cost)
            Assert.Equal(99 - line.Quantity, wallet.Single(m => m.MaterialName == line.MaterialName).Quantity);

        Assert.Single(await Service.ReadHiredAsync(Realm, Player));
    }

    [Fact]
    public async Task ASlotIsHiredAtMostOnce()
    {
        await FillWalletAsync();
        await Service.ReadBoardAsync(Realm, Player);

        Assert.True((await Service.HireAsync(Realm, Player, 0)).Outcome.Succeeded);

        var (outcome, hired) = await Service.HireAsync(Realm, Player, 0);

        Assert.Equal(TavernError.AlreadyHired, outcome.Error);
        Assert.Null(hired);
        Assert.Single(await Service.ReadHiredAsync(Realm, Player));
    }

    [Fact]
    public async Task HiringWithoutTheMaterials_TakesNothingAndLeavesNoRecord()
    {
        await Service.ReadBoardAsync(Realm, Player);

        var (outcome, hired) = await Service.HireAsync(Realm, Player, 0);

        Assert.Equal(TavernError.CannotAfford, outcome.Error);
        Assert.Null(hired);
        Assert.Empty(await Service.ReadHiredAsync(Realm, Player));

        // And the slot is still open, so a player who earns the cost can come back for it.
        var board = await Service.ReadBoardAsync(Realm, Player);
        Assert.Null(board[0].HiredAt);
    }

    [Fact]
    public async Task AnEmptySlot_IsNotAHire()
    {
        await FillWalletAsync();
        await Service.ReadBoardAsync(Realm, Player);

        Assert.Equal(TavernError.NoSuchSlot, (await Service.HireAsync(Realm, Player, 99)).Outcome.Error);
    }

    [Fact]
    public async Task ARecruitContentCanNoLongerBuild_IsRefusedRatherThanHiredEmpty()
    {
        await FillWalletAsync();
        await Service.ReadBoardAsync(Realm, Player);

        // The signature is dropped from content between the roll and the hire.
        _content.Signatures = new List<SignatureDefinition>();

        var (outcome, hired) = await Service.HireAsync(Realm, Player, 0);

        Assert.Equal(TavernError.ContentMismatch, outcome.Error);
        Assert.Null(hired);
        Assert.Empty(await Wallet.ReadAsync(Realm, Player).ContinueWith(t => t.Result.Where(m => m.Quantity < 99)));
    }

    [Fact]
    public async Task TheRosterIsCapped()
    {
        await FillWalletAsync();

        for (int i = 0; i < TavernRules.RosterCap; i++)
        {
            await Service.RestockAsync(Realm, Player, 1, null, "test");
            Assert.True((await Service.HireAsync(Realm, Player, 0)).Outcome.Succeeded, $"hire {i} should succeed");
        }

        await Service.RestockAsync(Realm, Player, 1, null, "test");
        var (outcome, _) = await Service.HireAsync(Realm, Player, 0);

        Assert.Equal(TavernError.RosterFull, outcome.Error);
        Assert.Equal(TavernRules.RosterCap, (await Service.ReadHiredAsync(Realm, Player)).Count);
    }

    [Fact]
    public async Task EveryHireGetsItsOwnCharacterId()
    {
        await FillWalletAsync();

        var ids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            await Service.RestockAsync(Realm, Player, 1, null, "test");
            var (_, hired) = await Service.HireAsync(Realm, Player, 0);
            ids.Add(hired!.CharacterId);
        }

        Assert.Equal(3, ids.Distinct().Count());
    }
}
