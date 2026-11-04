using Enums;
using Items.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The Marketplace spec on top of the Bazaar (Mike 2026-10-10, 2026-11-04): prices stay at the assay; a
/// listing pays a 2% deposit, back as it sells and kept if pulled back or run out; listings and orders
/// last 72 hours; 100 listings and 20 orders a realm; buy orders hold their gold and fill oldest first
/// in both directions; and the board says what sold this week.
/// </summary>
public class MarketplaceTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private static readonly Guid Emberhold = Guid.NewGuid();
    private static readonly Guid Frostmere = Guid.NewGuid();
    private const string Seller = "seller";
    private const string Rival = "rival";
    private const string Buyer = "buyer";
    private const string Essence = "Lesser Essence";
    private const long Purse = 10_000;

    private readonly FakeGameContent _content = new()
    {
        Materials = new[]
        {
            new MaterialTemplate { MaterialName = Essence, Category = MaterialCategory.Essence, Tier = MaterialTier.Tier1 },
            new MaterialTemplate { MaterialName = "Timber", Category = MaterialCategory.Goods, Tier = MaterialTier.Tier1 },
        }
    };

    private readonly FakeSessionLog _log = new();

    public MarketplaceTests()
    {
        _db.GameInstances.Add(new GameInstance { Id = Emberhold, Name = "Emberhold", OwnerId = Seller });
        _db.GameInstances.Add(new GameInstance { Id = Frostmere, Name = "Frostmere", OwnerId = Buyer });
        _db.SaveChanges();
        foreach (var realm in new[] { Emberhold, Frostmere })
            foreach (var player in new[] { Seller, Rival, Buyer })
                Gold.GrantAsync(realm, player, Purse, "test").GetAwaiter().GetResult();
    }

    public void Dispose() => _db.Dispose();

    private GoldService Gold => new(_db, _log, NullLogger<GoldService>.Instance);
    private MaterialWalletService Wallet => new(_db, _log, NullLogger<MaterialWalletService>.Instance);
    private ItemLedgerService Ledger => new(_db, _log, NullLogger<ItemLedgerService>.Instance);
    private BazaarService Bazaar => new(_db, _content, Ledger, Wallet, Gold, _log);

    private static long Unit => BazaarAssay.PriceOf(MaterialCategory.Essence, MaterialTier.Tier1);

    private static ItemSaveData Sword(string id) => new()
    {
        Id = id, ItemName = "Cinder Blade", ItemCount = 1, ItemType = ItemTypes.Weapon, Rarity = ItemRarityTypes.Rare,
        PowerTier = ItemPowerTier.Tier4,
        ImplicitAttribute = new ItemImplicitAttribute { Type = ItemImplicitTypes.MovementSpeed, Value = 3 },
    };

    private async Task<int> Held(Guid realm, string user, string material = Essence) =>
        (await Wallet.ReadAsync(realm, user)).FirstOrDefault(m => m.MaterialName == material)?.Quantity ?? 0;

    private async Task Give(Guid realm, string user, int quantity, string material = Essence) =>
        await Wallet.GrantAsync(realm, user, new[] { new MaterialGrant { MaterialName = material, Quantity = quantity } }, "test");

    private async Task RunOutTheClockAsync()
    {
        foreach (var l in _db.MarketplaceListings) l.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        foreach (var o in _db.MarketOrders) o.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();
    }

    // -----------------------------------------------------------------
    // The deposit (row 8)
    // -----------------------------------------------------------------

    [Fact]
    public void TheDepositIsAFiftieth_RoundedUp_AndComesBackWholeAcrossPartSales()
    {
        Assert.Equal(20, BazaarAssay.DepositOn(1_000));
        Assert.Equal(1, BazaarAssay.DepositOn(10));
        Assert.Equal(3, BazaarAssay.DepositOn(101));
        Assert.Equal(0, BazaarAssay.DepositOn(0));

        long back = BazaarAssay.DepositBack(7, 10, 0, 3) + BazaarAssay.DepositBack(7, 10, 3, 7) + BazaarAssay.DepositBack(7, 10, 7, 10);
        Assert.Equal(7, back);
        Assert.Equal(0, BazaarAssay.DepositBack(7, 10, 4, 4));
    }

    [Fact]
    public async Task ListingTakesADeposit_KeptOnAPullBack()
    {
        await Give(Emberhold, Seller, 50);
        var (outcome, listing) = await Bazaar.ListMaterialAsync(Emberhold, Seller, Essence, 50);
        Assert.True(outcome.Succeeded);
        long deposit = BazaarAssay.DepositOn(Unit * 50);
        Assert.Equal(deposit, listing!.Deposit);
        Assert.Equal(Purse - deposit, await Gold.BalanceAsync(Emberhold, Seller));

        await Bazaar.PullBackAsync(Emberhold, Seller, listing.Id);
        Assert.Equal(50, await Held(Emberhold, Seller));
        Assert.Equal(0, await Bazaar.OwedAsync(Emberhold, Seller));
        Assert.Equal(Purse - deposit, await Gold.BalanceAsync(Emberhold, Seller));
    }

    [Fact]
    public async Task ASellerWhoCannotPayTheDeposit_KeepsTheirGoods()
    {
        await Give(Emberhold, "pauper", 10);
        var materials = await Bazaar.ListMaterialAsync(Emberhold, "pauper", Essence, 10);
        Assert.Equal(BazaarError.CannotAfford, materials.Outcome.Error);
        Assert.Equal(10, await Held(Emberhold, "pauper"));

        await Ledger.GrantAsync(Emberhold, "pauper", new[] { Sword("blade-1") }, "test");
        var (gear, _) = await Bazaar.ListEquipmentAsync(Emberhold, "pauper", "blade-1");
        Assert.Equal(BazaarError.CannotAfford, gear.Error);
        Assert.Null(Assert.Single(await Ledger.HeldAsync(Emberhold, "pauper")).ListingId);
        Assert.Empty(_db.MarketplaceListings);
    }

    [Fact]
    public async Task APartSale_PaysBackThatShareOfTheDeposit()
    {
        await Give(Emberhold, Seller, 10);
        var (_, listing) = await Bazaar.ListMaterialAsync(Emberhold, Seller, Essence, 10);
        await Bazaar.BuyMaterialAsync(Frostmere, Buyer, Essence, 4, Unit);

        long expected = BazaarAssay.NetOf(Unit * 4) + BazaarAssay.DepositBack(listing!.Deposit, 10, 0, 4);
        Assert.Equal(expected, await Bazaar.OwedAsync(Emberhold, Seller));
    }

    // -----------------------------------------------------------------
    // 72 hours (row 5)
    // -----------------------------------------------------------------

    [Fact]
    public async Task AfterItsHours_AnUnsoldListingGoesBack_AndTheDepositIsKept()
    {
        await Give(Emberhold, Seller, 10);
        await Bazaar.ListMaterialAsync(Emberhold, Seller, Essence, 10);
        await Ledger.GrantAsync(Emberhold, Seller, new[] { Sword("blade-1") }, "test");
        await Bazaar.ListEquipmentAsync(Emberhold, Seller, "blade-1");
        Assert.Equal(0, await Bazaar.SweepAsync());   // not yet

        await RunOutTheClockAsync();
        Assert.Equal(2, await Bazaar.SweepAsync());

        Assert.Equal(10, await Held(Emberhold, Seller));
        Assert.Null(Assert.Single(await Ledger.HeldAsync(Emberhold, Seller)).ListingId);
        Assert.All(_db.MarketplaceListings, l => Assert.Equal(ListingStatus.Returned, l.Status));
        Assert.Equal(0, await Bazaar.OwedAsync(Emberhold, Seller));
        Assert.Empty(await Bazaar.MaterialsAsync(Buyer));

        var mine = await Bazaar.MineAsync(Emberhold, Seller);
        Assert.Equal(2, mine.Count);   // shown for a day, so the seller sees why
        Assert.All(mine, l => Assert.Equal(ListingStatus.Returned, l.Status));
        Assert.Equal(0, await Bazaar.SweepAsync());   // once
    }

    [Fact]
    public async Task AfterItsHours_AnOpenOrderGivesBackItsGold()
    {
        await Bazaar.PostOrderAsync(Frostmere, Buyer, Essence, 20);
        Assert.Equal(Purse - Unit * 20, await Gold.BalanceAsync(Frostmere, Buyer));

        await RunOutTheClockAsync();
        Assert.Equal(1, await Bazaar.SweepAsync());
        Assert.Equal(Purse, await Gold.BalanceAsync(Frostmere, Buyer));
        Assert.Equal(MarketOrderStatus.Expired, _db.MarketOrders.Single().Status);
    }

    // -----------------------------------------------------------------
    // The caps (M2)
    // -----------------------------------------------------------------

    [Fact]
    public async Task AHundredListingsAndTwentyOrders_AreTheCap()
    {
        for (int i = 0; i < BazaarAssay.MaxListings; i++)
            _db.MarketplaceListings.Add(new MarketplaceListing
            {
                GameInstanceId = Emberhold, SellerId = Seller, Kind = ListingKind.Material, GoodsKey = Essence, GoodsName = Essence, Quantity = 1,
            });
        for (int i = 0; i < BazaarAssay.MaxOrders; i++)
            _db.MarketOrders.Add(new MarketOrder { GameInstanceId = Frostmere, BuyerId = Buyer, MaterialName = "Timber", Quantity = 1, UnitPrice = 2 });
        await _db.SaveChangesAsync();

        await Give(Emberhold, Seller, 1);
        Assert.Equal(BazaarError.TooMany, (await Bazaar.ListMaterialAsync(Emberhold, Seller, Essence, 1)).Outcome.Error);
        Assert.Equal(1, await Held(Emberhold, Seller));   // nothing taken
        Assert.Equal(BazaarError.TooMany, (await Bazaar.PostOrderAsync(Frostmere, Buyer, Essence, 1)).Outcome.Error);

        // The caps are per realm: the same player lists freely from another world.
        await Give(Frostmere, Seller, 1);
        Assert.True((await Bazaar.ListMaterialAsync(Frostmere, Seller, Essence, 1)).Outcome.Succeeded);
    }

    [Fact]
    public async Task AnOrderAsksFor1To999()
    {
        Assert.Equal(BazaarError.NothingNamed, (await Bazaar.PostOrderAsync(Frostmere, Buyer, Essence, 0)).Outcome.Error);
        Assert.Equal(BazaarError.NothingNamed,
            (await Bazaar.PostOrderAsync(Frostmere, Buyer, Essence, BazaarAssay.MaxOrderQuantity + 1)).Outcome.Error);
        Assert.Equal(BazaarError.UnknownGoods, (await Bazaar.PostOrderAsync(Frostmere, Buyer, "Moonbeams", 5)).Outcome.Error);
        Assert.Equal(Purse, await Gold.BalanceAsync(Frostmere, Buyer));
    }

    // -----------------------------------------------------------------
    // Buy orders (row 2)
    // -----------------------------------------------------------------

    [Fact]
    public async Task AnOrderBuysWhatIsListed_ThenTheNextListingSellsIntoIt()
    {
        await Give(Frostmere, Rival, 3);
        await Bazaar.ListMaterialAsync(Frostmere, Rival, Essence, 3);

        var ordered = await Bazaar.PostOrderAsync(Frostmere, Buyer, Essence, 10);
        Assert.True(ordered.Outcome.Succeeded);
        Assert.Equal(3, ordered.Bought);
        Assert.Equal(3, await Held(Frostmere, Buyer));
        Assert.Equal(Purse - Unit * 10, await Gold.BalanceAsync(Frostmere, Buyer));   // all ten held or paid
        Assert.Equal(Unit * 7, ordered.Order!.HeldGold);

        // The seller in another world lists ten: seven fill the order at once, three wait in the queue.
        await Give(Emberhold, Seller, 10);
        var listed = await Bazaar.ListMaterialAsync(Emberhold, Seller, Essence, 10);
        Assert.Equal(7, listed.SoldToOrders);
        Assert.Equal(10, await Held(Frostmere, Buyer));
        var order = _db.MarketOrders.AsNoTracking().Single();
        Assert.Equal(MarketOrderStatus.Filled, order.Status);
        Assert.Equal(0, order.HeldGold);

        Assert.Equal(BazaarAssay.NetOf(Unit * 7) + BazaarAssay.DepositBack(listed.Listing!.Deposit, 10, 0, 7),
            await Bazaar.OwedAsync(Emberhold, Seller));
        Assert.Equal(3, Assert.Single(await Bazaar.MaterialsAsync(Buyer)).Available);

        // The buyer paid exactly what they took.
        Assert.Equal(Purse - Unit * 10, await Gold.BalanceAsync(Frostmere, Buyer));
    }

    [Fact]
    public async Task OrdersFill_OldestFirst()
    {
        await Bazaar.PostOrderAsync(Frostmere, Buyer, Essence, 5);
        await Bazaar.PostOrderAsync(Emberhold, Rival, Essence, 5);

        await Give(Emberhold, Seller, 6);
        await Bazaar.ListMaterialAsync(Emberhold, Seller, Essence, 6);

        Assert.Equal(5, await Held(Frostmere, Buyer));
        Assert.Equal(1, await Held(Emberhold, Rival));
    }

    [Fact]
    public async Task APlayersOwnListingNeverFillsTheirOwnOrder_InAnyWorld()
    {
        await Bazaar.PostOrderAsync(Frostmere, Seller, Essence, 5);
        await Give(Emberhold, Seller, 5);
        var listed = await Bazaar.ListMaterialAsync(Emberhold, Seller, Essence, 5);
        Assert.Equal(0, listed.SoldToOrders);
        Assert.Equal(0, await Held(Frostmere, Seller));

        // And a new order does not buy their own listing either.
        var again = await Bazaar.PostOrderAsync(Frostmere, Seller, Essence, 5);
        Assert.Equal(0, again.Bought);
    }

    [Fact]
    public async Task CancellingAnOrder_GivesBackTheGoldStillHeld_AndOnlyTheBuyerMay()
    {
        var ordered = await Bazaar.PostOrderAsync(Frostmere, Buyer, Essence, 10);
        await Give(Emberhold, Seller, 4);
        await Bazaar.ListMaterialAsync(Emberhold, Seller, Essence, 4);

        var (theirs, _) = await Bazaar.CancelOrderAsync(Frostmere, Rival, ordered.Order!.Id);
        Assert.Equal(BazaarError.NotYours, theirs.Error);

        var (outcome, refunded) = await Bazaar.CancelOrderAsync(Frostmere, Buyer, ordered.Order.Id);
        Assert.True(outcome.Succeeded);
        Assert.Equal(Unit * 6, refunded);
        Assert.Equal(Purse - Unit * 4, await Gold.BalanceAsync(Frostmere, Buyer));
        Assert.Equal(4, await Held(Frostmere, Buyer));

        var (twice, _) = await Bazaar.CancelOrderAsync(Frostmere, Buyer, ordered.Order.Id);
        Assert.Equal(BazaarError.NotActive, twice.Error);
    }

    [Fact]
    public async Task AWorldResetGivesBackItsOrdersGold()
    {
        await Bazaar.PostOrderAsync(Emberhold, Rival, Essence, 10);
        await BazaarService.ExpireRealmAsync(_db, Gold, _log, Emberhold);

        Assert.Equal(Purse, await Gold.BalanceAsync(Emberhold, Rival));
        Assert.Equal(MarketOrderStatus.Expired, _db.MarketOrders.Single().Status);
    }

    // -----------------------------------------------------------------
    // What the board shows (M1)
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheBoardSaysWhatIsWanted_AndWhatSoldThisWeek()
    {
        await Bazaar.PostOrderAsync(Frostmere, Buyer, Essence, 10);
        await Bazaar.PostOrderAsync(Emberhold, Rival, Essence, 5);
        await Bazaar.PostOrderAsync(Emberhold, Rival, "Timber", 40);

        var wanted = await Bazaar.WantedAsync(Seller);
        Assert.Equal(15, wanted.Single(w => w.Name == Essence).Units);
        Assert.Equal(2, wanted.Single(w => w.Name == Essence).Orders);
        Assert.Equal(40, wanted.Single(w => w.Name == "Timber").Units);
        Assert.Equal(5, (await Bazaar.WantedAsync(Buyer)).Single(w => w.Name == Essence).Units);   // not your own

        await Give(Emberhold, Seller, 12);
        await Bazaar.ListMaterialAsync(Emberhold, Seller, Essence, 12);
        await Ledger.GrantAsync(Emberhold, Seller, new[] { Sword("blade-1") }, "test");
        var (_, blade) = await Bazaar.ListEquipmentAsync(Emberhold, Seller, "blade-1");
        await Bazaar.BuyEquipmentAsync(Frostmere, Buyer, blade!.Id, null);

        var (materials, gear) = await Bazaar.DemandAsync();
        Assert.Equal(12, materials.Single(d => d.Name == Essence).Sold);
        var rare = Assert.Single(gear);
        Assert.Equal((ItemTypes.Weapon, ItemRarityTypes.Rare, 1), (rare.Type, rare.Rarity, rare.Sold));

        // Older than a week does not count.
        var (later, _) = await Bazaar.DemandAsync(DateTime.UtcNow.AddDays(BazaarAssay.DemandDays + 1));
        Assert.Empty(later);
    }

    [Fact]
    public async Task MyOrdersShowWhatFilled()
    {
        await Bazaar.PostOrderAsync(Frostmere, Buyer, Essence, 10);
        await Give(Emberhold, Seller, 4);
        await Bazaar.ListMaterialAsync(Emberhold, Seller, Essence, 4);

        var mine = Assert.Single(await Bazaar.MyOrdersAsync(Frostmere, Buyer));
        Assert.Equal((10, 4, MarketOrderStatus.Open), (mine.Quantity, mine.Filled, mine.Status));
        Assert.Equal(Unit * 6, mine.HeldGold);
        Assert.Empty(await Bazaar.MyOrdersAsync(Emberhold, Buyer));   // per realm
    }
}
