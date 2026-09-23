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

    private GoldService Gold =>
        new(_db, new FakeSessionLog(), NullLogger<GoldService>.Instance);

    private TavernService Service =>
        new(_db, _content, Wallet, Gold, new FakeSessionLog(), NullLogger<TavernService>.Instance);

    /// <summary>Enough gold for several paid refreshes.</summary>
    private async Task FillPurseAsync(long gold = 10_000)
        => await Gold.GrantAsync(Realm, Player, gold, "test");

    /// <summary>Empties seats by hiring, so an arrival has somewhere to sit.</summary>
    private async Task FreeSeatsAsync(int howMany)
    {
        await FillWalletAsync();

        var board = await Service.ReadBoardAsync(Realm, Player);
        foreach (var recruit in board.Take(howMany))
            await Service.HireAsync(Realm, Player, recruit.Slot);
    }

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
    public async Task AClearTakesNobodyAway()
    {
        // The whole reason the board became seats. Farming the materials to afford a recruit used to
        // be the very thing that took that recruit away, so saving up was self-defeating.
        await FreeSeatsAsync(1);
        var before = await Service.ReadBoardAsync(Realm, Player);

        await Service.BringARecruitAsync(Realm, Player, 3, BiomeType.Volcanic, "test");
        var after = await Service.ReadBoardAsync(Realm, Player);

        foreach (var recruit in before)
            Assert.Contains(recruit.Id, after.Select(r => r.Id));
    }

    [Fact]
    public async Task AClearSeatsExactlyOneNewRecruit()
    {
        await FreeSeatsAsync(2);
        var before = await Service.ReadBoardAsync(Realm, Player);

        await Service.BringARecruitAsync(Realm, Player, 3, BiomeType.Volcanic, "test");
        var after = await Service.ReadBoardAsync(Realm, Player);

        Assert.Equal(before.Count + 1, after.Count);
    }

    [Fact]
    public async Task AFullBoardTakesNobody()
    {
        // Not a limitation - it is the situation the paid refresh exists for.
        var before = await Service.ReadBoardAsync(Realm, Player);
        Assert.Equal(TavernRules.BoardSize, before.Count);

        var outcome = await Service.BringARecruitAsync(Realm, Player, 3, BiomeType.Volcanic, "test");

        Assert.Equal(TavernError.BoardIsFull, outcome.Error);
        Assert.Equal(before.Select(r => r.Id), (await Service.ReadBoardAsync(Realm, Player)).Select(r => r.Id));
    }

    [Fact]
    public async Task AFullBoardDoesNotEatTheStandingLure()
    {
        // A crystal that bought nothing would be a crystal quietly lost.
        await Wallet.GrantAsync(Realm, Player, new List<MaterialGrant>
        {
            new() { MaterialName = "Perfect Fire Crystal", Quantity = 1 }
        }, "test");

        await Service.ReadBoardAsync(Realm, Player);   // fills all six seats
        await Service.PlaceLureAsync(Realm, Player, AffinityTypes.Fire, TavernRules.LureStrength.Perfect);

        await Service.BringARecruitAsync(Realm, Player, 1, null, "test");

        Assert.NotNull(await Service.StandingLureAsync(Realm, Player));
    }

    [Fact]
    public async Task HiringFreesTheSeat()
    {
        // Hiring is the main way a seat comes free, so a hired card must leave the board rather than
        // sitting there marked hired and blocking its seat for good.
        await FillWalletAsync();
        var board = await Service.ReadBoardAsync(Realm, Player);

        await Service.HireAsync(Realm, Player, board[0].Slot);

        var after = await Service.ReadBoardAsync(Realm, Player);
        Assert.Equal(TavernRules.BoardSize - 1, after.Count);
        Assert.DoesNotContain(board[0].Id, after.Select(r => r.Id));
    }

    // -----------------------------------------------------------------
    // The paid refresh
    // -----------------------------------------------------------------

    [Fact]
    public async Task APaidRefreshReplacesTheWholeBoard()
    {
        await FillPurseAsync();
        var before = await Service.ReadBoardAsync(Realm, Player);

        var outcome = await Service.RefreshAsync(Realm, Player);

        Assert.True(outcome.Succeeded, outcome.Message);

        var after = await Service.ReadBoardAsync(Realm, Player);
        Assert.Equal(TavernRules.BoardSize, after.Count);
        Assert.Empty(after.Select(r => r.Id).Intersect(before.Select(r => r.Id)));
    }

    [Fact]
    public async Task APaidRefreshCostsGold()
    {
        await FillPurseAsync(5_000);
        await Service.ReadBoardAsync(Realm, Player);

        await Service.RefreshAsync(Realm, Player);

        Assert.Equal(5_000 - TavernRules.RefreshCostGold, await Gold.BalanceAsync(Realm, Player));
    }

    [Fact]
    public async Task ARefreshNobodyCanAffordChangesNothing()
    {
        var before = await Service.ReadBoardAsync(Realm, Player);

        var outcome = await Service.RefreshAsync(Realm, Player);

        Assert.Equal(TavernError.CannotAfford, outcome.Error);
        Assert.Equal(before.Select(r => r.Id), (await Service.ReadBoardAsync(Realm, Player)).Select(r => r.Id));
    }

    [Fact]
    public async Task ARefreshThatCanFindNobodyGivesTheGoldBack()
    {
        // A refresh that charged for an empty room would be a bug the player pays for.
        await FillPurseAsync(5_000);
        await Service.ReadBoardAsync(Realm, Player);

        _content.Signatures = new List<SignatureDefinition>();
        var outcome = await Service.RefreshAsync(Realm, Player);

        Assert.False(outcome.Succeeded);
        Assert.Equal(5_000, await Gold.BalanceAsync(Realm, Player));
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
        await FreeSeatsAsync(1);
        var before = await Service.ReadBoardAsync(Realm, Player);

        _content.Signatures = new List<SignatureDefinition>();
        await Service.BringARecruitAsync(Realm, Player, 1, null, "test");

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

        // The recruit left the board when they were hired, so the seat is empty rather than "already
        // hired". Either way the second attempt takes nothing and charges nothing.
        Assert.Equal(TavernError.NoSuchSlot, outcome.Error);
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
            var board = await Service.ReadBoardAsync(Realm, Player);
            Assert.True((await Service.HireAsync(Realm, Player, board[0].Slot)).Outcome.Succeeded,
                $"hire {i} should succeed");
        }

        var last = await Service.ReadBoardAsync(Realm, Player);
        var (outcome, _) = await Service.HireAsync(Realm, Player, last[0].Slot);

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
            await FreeSeatsAsync(1);
        await Service.BringARecruitAsync(Realm, Player, 1, null, "test");
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

        await FreeSeatsAsync(1);
        await Service.BringARecruitAsync(Realm, Player, 1, null, "test");

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
        await FreeSeatsAsync(1);
        await Service.BringARecruitAsync(Realm, Player, 1, null, "test");

        var lures = await Service.ReadLuresAsync(Realm, Player);
        var arcane = lures.Single(l => l.Affinity == AffinityTypes.Arcane);

        Assert.Equal(1, arcane.MissedRestocks);
        Assert.Equal(TavernRules.LureStrength.None, arcane.PendingStrength);

        // And it accumulates.
        await Service.PlaceLureAsync(Realm, Player, AffinityTypes.Arcane, TavernRules.LureStrength.Perfect);
        await FreeSeatsAsync(1);
        await Service.BringARecruitAsync(Realm, Player, 1, null, "test");

        arcane = (await Service.ReadLuresAsync(Realm, Player)).Single(l => l.Affinity == AffinityTypes.Arcane);
        Assert.Equal(2, arcane.MissedRestocks);
    }

    [Fact]
    public async Task AnUnluredRestockBuildsNoPity()
    {
        // Pity is what a lure buys when it does not pay off, not a reward for playing.
        await FreeSeatsAsync(1);
        await Service.BringARecruitAsync(Realm, Player, 1, null, "test");
        await FreeSeatsAsync(1);
        await Service.BringARecruitAsync(Realm, Player, 1, null, "test");

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
        await FreeSeatsAsync(1);
        await Service.BringARecruitAsync(Realm, Player, 1, null, "test");

        var arcane = (await Service.ReadLuresAsync(Realm, Player))
            .Single(l => l.Affinity == AffinityTypes.Arcane);
        Assert.Equal(1, arcane.MissedRestocks);

        // Now a hit, forced rather than hoped for: one arrival against a 60% target is a coin flip,
        // so content is narrowed to a signature that can only roll Fire.
        _content.Signatures = new List<SignatureDefinition>
        {
            new()
            {
                SignatureId = "warrior_cleave", Class = "Warrior", BaseAbilityLinkName = "Cleaving_Blow_1",
                AllowedAffinities = new List<AffinityTypes> { AffinityTypes.Fire }
            }
        };

        await Service.PlaceLureAsync(Realm, Player, AffinityTypes.Fire, TavernRules.LureStrength.Perfect);
        await FreeSeatsAsync(1);
        await Service.BringARecruitAsync(Realm, Player, 1, null, "test");

        var fire = (await Service.ReadLuresAsync(Realm, Player)).Single(l => l.Affinity == AffinityTypes.Fire);
        Assert.Equal(0, fire.MissedRestocks);

        // The Arcane debt is untouched by a Fire board - pity is per affinity.
        arcane = (await Service.ReadLuresAsync(Realm, Player)).Single(l => l.Affinity == AffinityTypes.Arcane);
        Assert.Equal(1, arcane.MissedRestocks);
    }

    [Fact]
    public async Task APaidRefreshSpendsTheStandingLureOnTheBoardItRolls()
    {
        // A refresh still rolls a whole board, so this is where a lure's share is visible. An
        // arrival is a single roll and would be a coin flip to assert on.
        await GiveCrystalsAsync();
        await FillPurseAsync();
        await Service.ReadBoardAsync(Realm, Player);

        // Narrowed to a signature that can only roll Fire, so this asserts the lure is carried into
        // the roll rather than gambling on a share. The share itself is pinned deterministically over
        // thousands of seeded rolls in TavernLureTests - an unseeded six-slot sample is not evidence.
        _content.Signatures = new List<SignatureDefinition>
        {
            new()
            {
                SignatureId = "warrior_cleave", Class = "Warrior", BaseAbilityLinkName = "Cleaving_Blow_1",
                AllowedAffinities = new List<AffinityTypes> { AffinityTypes.Fire }
            }
        };

        await Service.PlaceLureAsync(Realm, Player, AffinityTypes.Fire, TavernRules.LureStrength.Perfect);
        await Service.RefreshAsync(Realm, Player);

        // The lure must have been spent by the board it paid for.
        Assert.Null(await Service.StandingLureAsync(Realm, Player));

        var board = await Service.ReadBoardAsync(Realm, Player);
        Assert.All(board, r => Assert.Equal(AffinityTypes.Fire, r.Affinity));
    }
}
