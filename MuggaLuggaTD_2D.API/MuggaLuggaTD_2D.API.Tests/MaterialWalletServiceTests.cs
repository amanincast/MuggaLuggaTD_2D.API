using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The material wallet. Materials buy characters at the Tavern, so the balance has to live where the
/// client cannot write it — granted by claimed runs, spent through endpoints, never trusted from a save.
///
/// <para>Spending is the forgiving half (it only ever destroys the caller's own materials) and
/// granting is the guarded half, so what is pinned here is that a spend can never go negative, never
/// half-applies, and that a grant accumulates rather than replaces.</para>
/// </summary>
public class MaterialWalletServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private static readonly Guid Realm = Guid.NewGuid();
    private const string Player = "player-1";

    private MaterialWalletService Service =>
        new(_db, new FakeSessionLog(), NullLogger<MaterialWalletService>.Instance);

    public void Dispose() => _db.Dispose();

    // -----------------------------------------------------------------
    // Granting
    // -----------------------------------------------------------------

    [Fact]
    public async Task GrantingAddsToWhatIsAlreadyHeld()
    {
        await Service.GrantAsync(Realm, Player, Grants(("Lesser Essence", 3)), "run-1");
        await Service.GrantAsync(Realm, Player, Grants(("Lesser Essence", 2)), "run-2");

        var held = await Service.ReadAsync(Realm, Player);

        Assert.Equal(5, Assert.Single(held).Quantity);
    }

    [Fact]
    public async Task OnePlayersMaterialsAreNotAnothers()
    {
        await Service.GrantAsync(Realm, Player, Grants(("Rare Shard", 4)), "run-1");
        await Service.GrantAsync(Realm, "player-2", Grants(("Rare Shard", 1)), "run-2");

        Assert.Equal(4, (await Service.ReadAsync(Realm, Player)).Single().Quantity);
        Assert.Equal(1, (await Service.ReadAsync(Realm, "player-2")).Single().Quantity);
    }

    [Fact]
    public async Task MaterialsAreHeldPerRealm_SoASecondWorldStartsEmpty()
    {
        await Service.GrantAsync(Realm, Player, Grants(("Lesser Essence", 9)), "run-1");

        Assert.Empty(await Service.ReadAsync(Guid.NewGuid(), Player));
    }

    [Fact]
    public async Task GrantingNothingIsHarmless()
    {
        await Service.GrantAsync(Realm, Player, new List<MaterialGrant>(), "run-1");
        await Service.GrantAsync(Realm, Player, Grants(("Lesser Essence", 0)), "run-2");

        Assert.Empty(await Service.ReadAsync(Realm, Player));
    }

    // -----------------------------------------------------------------
    // Spending
    // -----------------------------------------------------------------

    [Fact]
    public async Task SpendingDeductsWhatWasNamed()
    {
        await Service.GrantAsync(Realm, Player, Grants(("Lesser Essence", 5), ("Rare Shard", 2)), "run-1");

        var outcome = await Service.SpendAsync(Realm, Player, Grants(("Lesser Essence", 3)), "merge");

        Assert.True(outcome.Succeeded);
        var held = await Service.ReadAsync(Realm, Player);
        Assert.Equal(2, held.Single(m => m.MaterialName == "Lesser Essence").Quantity);
        Assert.Equal(2, held.Single(m => m.MaterialName == "Rare Shard").Quantity);
    }

    [Fact]
    public async Task SpendingMoreThanIsHeldIsRefused_AndTakesNothing()
    {
        await Service.GrantAsync(Realm, Player, Grants(("Lesser Essence", 2)), "run-1");

        var outcome = await Service.SpendAsync(Realm, Player, Grants(("Lesser Essence", 3)), "merge");

        Assert.False(outcome.Succeeded);
        Assert.Equal(WalletError.InsufficientMaterials, outcome.Error);
        Assert.Equal(2, (await Service.ReadAsync(Realm, Player)).Single().Quantity);
    }

    [Fact]
    public async Task ASpendIsAllOrNothing_SoAnUnaffordableLineTakesNothingAtAll()
    {
        await Service.GrantAsync(Realm, Player, Grants(("Lesser Essence", 5), ("Rare Shard", 1)), "run-1");

        var outcome = await Service.SpendAsync(
            Realm, Player, Grants(("Lesser Essence", 2), ("Rare Shard", 9)), "hire");

        Assert.False(outcome.Succeeded);
        var held = await Service.ReadAsync(Realm, Player);
        Assert.Equal(5, held.Single(m => m.MaterialName == "Lesser Essence").Quantity);
        Assert.Equal(1, held.Single(m => m.MaterialName == "Rare Shard").Quantity);
    }

    [Fact]
    public async Task TheSameMaterialTwiceInOneSpendIsCountedOnce_NotCheckedTwiceAgainstTheFullBalance()
    {
        await Service.GrantAsync(Realm, Player, Grants(("Lesser Essence", 5)), "run-1");

        // 3 + 3 is 6, which is more than 5: split across two lines it must still be refused.
        var outcome = await Service.SpendAsync(
            Realm, Player, Grants(("Lesser Essence", 3), ("Lesser Essence", 3)), "hire");

        Assert.False(outcome.Succeeded);
        Assert.Equal(5, (await Service.ReadAsync(Realm, Player)).Single().Quantity);
    }

    [Fact]
    public async Task SpendingWhatIsNotHeldAtAllIsRefused()
    {
        var outcome = await Service.SpendAsync(Realm, Player, Grants(("Perfect Fire Crystal", 1)), "hire");

        Assert.Equal(WalletError.InsufficientMaterials, outcome.Error);
    }

    [Fact]
    public async Task SpendingNothingIsRefusedRatherThanSilentlyDoingNothing()
    {
        var outcome = await Service.SpendAsync(Realm, Player, new List<MaterialGrant>(), "hire");

        Assert.Equal(WalletError.NothingToSpend, outcome.Error);
    }

    [Fact]
    public async Task ABalanceNeverGoesNegative()
    {
        await Service.GrantAsync(Realm, Player, Grants(("Lesser Essence", 1)), "run-1");
        await Service.SpendAsync(Realm, Player, Grants(("Lesser Essence", 1)), "merge");
        await Service.SpendAsync(Realm, Player, Grants(("Lesser Essence", 1)), "merge-again");

        Assert.Equal(0, (await Service.ReadAsync(Realm, Player)).Single().Quantity);
    }

    private static List<MaterialGrant> Grants(params (string Name, int Quantity)[] entries)
        => entries.Select(e => new MaterialGrant { MaterialName = e.Name, Quantity = e.Quantity }).ToList();
}
