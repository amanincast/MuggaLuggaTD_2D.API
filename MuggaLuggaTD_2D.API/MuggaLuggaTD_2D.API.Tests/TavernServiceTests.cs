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
            new()
            {
                SignatureId = "warrior_cleave", Class = "Warrior", BaseAbilityLinkName = "Cleaving_Blow_1",
                AllowedAffinities = new List<AffinityTypes>
                {
                    AffinityTypes.Physical, AffinityTypes.Fire, AffinityTypes.Water, AffinityTypes.Earth
                }
            }
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
    // -----------------------------------------------------------------
    // Lures and pity
    // -----------------------------------------------------------------

    private async Task GiveCrystalsAsync(int howMany = 9)
    {
        await Wallet.GrantAsync(Realm, Player, new List<MaterialGrant>
        {
            new() { MaterialName = "Perfect Fire Crystal", Quantity = howMany },
            new() { MaterialName = "Minor Fire Crystal", Quantity = howMany }
        }, "test");
    }

    [Fact]
    public async Task PlacingALureSpendsTheCrystalNow()
    {
        // Paid on placement rather than at the restock: the restock happens inside a PvE claim, and
        // a payment that could fail there would be a claim that half-succeeded.
        await GiveCrystalsAsync(2);

        var (outcome, lure) = await Service.PlaceLureAsync(
            Realm, Player, AffinityTypes.Fire, TavernRules.LureStrength.Perfect);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(TavernRules.LureStrength.Perfect, lure!.PendingStrength);

        var held = await Wallet.ReadAsync(Realm, Player);
        Assert.Equal(1, held.Single(m => m.MaterialName == "Perfect Fire Crystal").Quantity);
    }

    [Fact]
    public async Task ALureCannotBePlacedWithoutTheCrystal()
    {
        var (outcome, lure) = await Service.PlaceLureAsync(
            Realm, Player, AffinityTypes.Fire, TavernRules.LureStrength.Perfect);

        Assert.Equal(TavernError.CannotAfford, outcome.Error);
        Assert.Null(lure);
        Assert.Null(await Service.StandingLureAsync(Realm, Player));
    }

    [Fact]
    public async Task OnlyOneOfferMayStand()
    {
        // A second would be silently unspent by the restock that takes the first, and quietly losing
        // a crystal is worse than being told no.
        await GiveCrystalsAsync();

        await Service.PlaceLureAsync(Realm, Player, AffinityTypes.Fire, TavernRules.LureStrength.Perfect);

        var (outcome, _) = await Service.PlaceLureAsync(
            Realm, Player, AffinityTypes.Water, TavernRules.LureStrength.Minor);

        Assert.Equal(TavernError.LureAlreadyStanding, outcome.Error);

        // And the refused offer cost nothing.
        var held = await Wallet.ReadAsync(Realm, Player);
        Assert.Equal(9, held.Single(m => m.MaterialName == "Minor Fire Crystal").Quantity);
    }

    [Fact]
    public async Task ARestockSpendsTheStandingLure()
    {
        await GiveCrystalsAsync();
        await Service.PlaceLureAsync(Realm, Player, AffinityTypes.Fire, TavernRules.LureStrength.Perfect);

        await Service.RestockAsync(Realm, Player, 1, null, "test");

        Assert.Null(await Service.StandingLureAsync(Realm, Player));
    }

    [Fact]
    public async Task ARestockThatMissesLeavesPityBehind()
    {
        // The whole reason a miss is not simply a wasted crystal. Arcane is not an affinity this
        // fixture's signature can roll, so the board is guaranteed to miss it.
        await Wallet.GrantAsync(Realm, Player, new List<MaterialGrant>
        {
            new() { MaterialName = "Perfect Arcane Crystal", Quantity = 3 }
        }, "test");

        await Service.PlaceLureAsync(Realm, Player, AffinityTypes.Arcane, TavernRules.LureStrength.Perfect);
        await Service.RestockAsync(Realm, Player, 1, null, "test");

        var lures = await Service.ReadLuresAsync(Realm, Player);
        var arcane = lures.Single(l => l.Affinity == AffinityTypes.Arcane);

        Assert.Equal(1, arcane.MissedRestocks);
        Assert.Equal(TavernRules.LureStrength.None, arcane.PendingStrength);

        // And it accumulates.
        await Service.PlaceLureAsync(Realm, Player, AffinityTypes.Arcane, TavernRules.LureStrength.Perfect);
        await Service.RestockAsync(Realm, Player, 1, null, "test");

        arcane = (await Service.ReadLuresAsync(Realm, Player)).Single(l => l.Affinity == AffinityTypes.Arcane);
        Assert.Equal(2, arcane.MissedRestocks);
    }

    [Fact]
    public async Task AnUnluredRestockBuildsNoPity()
    {
        // Pity is what a lure buys when it does not pay off, not a reward for playing.
        await Service.RestockAsync(Realm, Player, 1, null, "test");
        await Service.RestockAsync(Realm, Player, 1, null, "test");

        Assert.Empty(await Service.ReadLuresAsync(Realm, Player));
    }

    [Fact]
    public async Task PityResetsOnceTheAffinityAppears()
    {
        await Wallet.GrantAsync(Realm, Player, new List<MaterialGrant>
        {
            new() { MaterialName = "Perfect Arcane Crystal", Quantity = 3 },
            new() { MaterialName = "Perfect Fire Crystal", Quantity = 3 }
        }, "test");

        // Build a drought on Arcane, which this signature cannot roll.
        await Service.PlaceLureAsync(Realm, Player, AffinityTypes.Arcane, TavernRules.LureStrength.Perfect);
        await Service.RestockAsync(Realm, Player, 1, null, "test");

        var arcane = (await Service.ReadLuresAsync(Realm, Player))
            .Single(l => l.Affinity == AffinityTypes.Arcane);
        Assert.Equal(1, arcane.MissedRestocks);

        // Fire, which it can roll, at a ceiling-high target over six slots: it will appear.
        await Service.PlaceLureAsync(Realm, Player, AffinityTypes.Fire, TavernRules.LureStrength.Perfect);
        await Service.RestockAsync(Realm, Player, 1, null, "test");

        var fire = (await Service.ReadLuresAsync(Realm, Player)).Single(l => l.Affinity == AffinityTypes.Fire);
        Assert.Equal(0, fire.MissedRestocks);

        // The Arcane debt is untouched by a Fire board - pity is per affinity.
        arcane = (await Service.ReadLuresAsync(Realm, Player)).Single(l => l.Affinity == AffinityTypes.Arcane);
        Assert.Equal(1, arcane.MissedRestocks);
    }

    [Fact]
    public async Task ALuredBoardLeansTheWayItWasPaidTo()
    {
        await GiveCrystalsAsync();
        await Service.PlaceLureAsync(Realm, Player, AffinityTypes.Fire, TavernRules.LureStrength.Perfect);

        await Service.RestockAsync(Realm, Player, 1, null, "test");

        var board = await Service.ReadBoardAsync(Realm, Player);
        int fire = board.Count(r => r.Affinity == AffinityTypes.Fire);

        // Six slots at 60% each; two or more is a very safe floor and this is not a statistics test.
        Assert.True(fire >= 2, $"expected a Fire-leaning board, got {fire} of {board.Count}");
    }
}
