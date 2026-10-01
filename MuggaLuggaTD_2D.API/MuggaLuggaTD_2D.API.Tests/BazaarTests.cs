using System.Text.Json.Nodes;
using Enums;
using Items.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The Crossroads Bazaar (design 12d). What is pinned: nobody sets a price; goods leave the seller
/// when listed and come back when pulled; a material is one queue, oldest first, across worlds; the
/// buyer pays from their realm and the seller is paid in theirs, less a tenth; and a world's reset
/// destroys its unsold goods but pays what was already earned.
/// </summary>
public class BazaarTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private static readonly Guid Emberhold = Guid.NewGuid();
    private static readonly Guid Frostmere = Guid.NewGuid();
    private const string Seller = "seller";
    private const string Rival = "rival";
    private const string Buyer = "buyer";

    private readonly FakeGameContent _content = new()
    {
        Materials = new[]
        {
            new MaterialTemplate { MaterialName = "Lesser Essence", Category = MaterialCategory.Essence, Tier = MaterialTier.Tier1 },
            new MaterialTemplate { MaterialName = "Legendary Shard", Category = MaterialCategory.RarityShard, Tier = MaterialTier.Tier3 },
        }
    };

    private readonly FakeSessionLog _log = new();

    public BazaarTests()
    {
        _db.GameInstances.Add(new GameInstance { Id = Emberhold, Name = "Emberhold", OwnerId = Seller });
        _db.GameInstances.Add(new GameInstance { Id = Frostmere, Name = "Frostmere", OwnerId = Buyer });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private GoldService Gold => new(_db, _log, NullLogger<GoldService>.Instance);
    private MaterialWalletService Wallet => new(_db, _log, NullLogger<MaterialWalletService>.Instance);
    private ItemLedgerService Ledger => new(_db, _log, NullLogger<ItemLedgerService>.Instance);
    private BazaarService Bazaar => new(_db, _content, Ledger, Wallet, Gold, _log);

    private static ItemSaveData Sword(string id, ItemRarityTypes rarity = ItemRarityTypes.Rare) => new()
    {
        Id = id,
        ItemName = "Cinder Blade",
        ItemCount = 1,
        ItemType = ItemTypes.Weapon,
        Rarity = rarity,
        PowerTier = ItemPowerTier.Tier4,
        ImplicitAttribute = new ItemImplicitAttribute { Type = ItemImplicitTypes.MovementSpeed, Value = 3 },
    };

    private async Task<int> Held(Guid realm, string user, string material) =>
        (await Wallet.ReadAsync(realm, user)).FirstOrDefault(m => m.MaterialName == material)?.Quantity ?? 0;

    private async Task Give(Guid realm, string user, string material, int quantity) =>
        await Wallet.GrantAsync(realm, user, new[] { new MaterialGrant { MaterialName = material, Quantity = quantity } }, "test");

    // -----------------------------------------------------------------
    // The Assay
    // -----------------------------------------------------------------

    [Fact]
    public void TheAssayPricesByWhatAThingIs_NotByWhoSellsIt()
    {
        Assert.Equal(BazaarAssay.PriceOf(Sword("a")), BazaarAssay.PriceOf(Sword("b")));
        Assert.True(BazaarAssay.PriceOf(Sword("a", ItemRarityTypes.Legendary)) > BazaarAssay.PriceOf(Sword("a", ItemRarityTypes.Common)));
        Assert.True(BazaarAssay.PriceOf(MaterialCategory.Essence, MaterialTier.Tier3) > BazaarAssay.PriceOf(MaterialCategory.Essence, MaterialTier.Tier1));
        Assert.True(BazaarAssay.PriceOf(MaterialCategory.RarityShard, MaterialTier.Tier1) > BazaarAssay.PriceOf(MaterialCategory.Essence, MaterialTier.Tier1));
    }

    [Fact]
    public void PricesAreTidy_AndNeverBelowTheFloor()
    {
        Assert.Equal(BazaarAssay.MinimumPrice, BazaarAssay.Tidy(0.3));
        Assert.Equal(0, BazaarAssay.Tidy(47.2) % 5);
        Assert.Equal(0, BazaarAssay.Tidy(4_321) % 50);
        Assert.Equal(0, BazaarAssay.Tidy(54_321) % 100);
    }

    [Fact]
    public void TheHouseKeepsATenth()
    {
        Assert.Equal(100, BazaarAssay.FeeOn(1_000));
        Assert.Equal(900, BazaarAssay.NetOf(1_000));
        Assert.Equal(0, BazaarAssay.NetOf(0));
    }

    // -----------------------------------------------------------------
    // Materials
    // -----------------------------------------------------------------

    [Fact]
    public async Task ListingTakesTheMaterialsOutOfTheWallet_AndPullingBackReturnsWhatIsUnsold()
    {
        await Give(Emberhold, Seller, "Lesser Essence", 10);
        var (outcome, listing) = await Bazaar.ListMaterialAsync(Emberhold, Seller, "Lesser Essence", 8);
        Assert.True(outcome.Succeeded);
        Assert.Equal(2, await Held(Emberhold, Seller, "Lesser Essence"));

        await Gold.GrantAsync(Frostmere, Buyer, 10_000, "test");
        Assert.True((await Bazaar.BuyMaterialAsync(Frostmere, Buyer, "Lesser Essence", 3, null)).Outcome.Succeeded);

        var (pulled, _, returned) = await Bazaar.PullBackAsync(Emberhold, Seller, listing!.Id);
        Assert.True(pulled.Succeeded);
        Assert.Equal(5, returned);
        Assert.Equal(7, await Held(Emberhold, Seller, "Lesser Essence"));
    }

    [Fact]
    public async Task YouCannotListWhatYouDoNotHave()
    {
        await Give(Emberhold, Seller, "Lesser Essence", 2);
        var (outcome, _) = await Bazaar.ListMaterialAsync(Emberhold, Seller, "Lesser Essence", 3);
        Assert.Equal(BazaarError.NotHeld, outcome.Error);
        Assert.Equal(2, await Held(Emberhold, Seller, "Lesser Essence"));
    }

    [Fact]
    public async Task TheOldestListingSellsFirst_AcrossSellersAndWorlds()
    {
        await Give(Emberhold, Seller, "Lesser Essence", 5);
        await Give(Frostmere, Rival, "Lesser Essence", 5);
        var (_, first) = await Bazaar.ListMaterialAsync(Emberhold, Seller, "Lesser Essence", 5);
        first!.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
        _db.SaveChanges();
        var (_, second) = await Bazaar.ListMaterialAsync(Frostmere, Rival, "Lesser Essence", 5);

        await Gold.GrantAsync(Frostmere, Buyer, 10_000, "test");
        await Bazaar.BuyMaterialAsync(Frostmere, Buyer, "Lesser Essence", 7, null);

        Assert.Equal(5, _db.MarketplaceListings.Find(first.Id)!.QuantitySold);
        Assert.Equal(ListingStatus.Sold, _db.MarketplaceListings.Find(first.Id)!.Status);
        Assert.Equal(2, _db.MarketplaceListings.Find(second!.Id)!.QuantitySold);
        Assert.Equal(7, await Held(Frostmere, Buyer, "Lesser Essence"));
    }

    [Fact]
    public async Task TheBuyerPaysInTheirWorld_AndTheSellerIsPaidInTheirs_LessATenth()
    {
        long unit = BazaarAssay.PriceOf(MaterialCategory.Essence, MaterialTier.Tier1);
        await Give(Emberhold, Seller, "Lesser Essence", 10);
        await Bazaar.ListMaterialAsync(Emberhold, Seller, "Lesser Essence", 10);
        await Gold.GrantAsync(Frostmere, Buyer, 10_000, "test");

        var bought = await Bazaar.BuyMaterialAsync(Frostmere, Buyer, "Lesser Essence", 10, unit);

        Assert.Equal(unit * 10, bought.Spent);
        Assert.Equal(10_000 - unit * 10, await Gold.BalanceAsync(Frostmere, Buyer));
        Assert.Equal(BazaarAssay.NetOf(unit * 10), await Bazaar.OwedAsync(Emberhold, Seller));
        Assert.Equal(0, await Gold.BalanceAsync(Emberhold, Seller));   // not until collected

        var (collected, balance) = await Bazaar.CollectAsync(Emberhold, Seller);
        Assert.Equal(BazaarAssay.NetOf(unit * 10), collected);
        Assert.Equal(collected, balance);
        Assert.Equal(0, await Bazaar.OwedAsync(Emberhold, Seller));
        Assert.Equal(0, (await Bazaar.CollectAsync(Emberhold, Seller)).Collected);   // nothing twice
    }

    [Fact]
    public async Task APurchaseIsAllOrNothing()
    {
        await Give(Emberhold, Seller, "Lesser Essence", 3);
        await Bazaar.ListMaterialAsync(Emberhold, Seller, "Lesser Essence", 3);
        await Gold.GrantAsync(Frostmere, Buyer, 10_000, "test");

        var tooMany = await Bazaar.BuyMaterialAsync(Frostmere, Buyer, "Lesser Essence", 4, null);
        Assert.Equal(BazaarError.NotEnoughListed, tooMany.Outcome.Error);
        Assert.Equal(10_000, await Gold.BalanceAsync(Frostmere, Buyer));

        var broke = await Bazaar.BuyMaterialAsync(Emberhold, "pauper", "Lesser Essence", 1, null);
        Assert.Equal(BazaarError.CannotAfford, broke.Outcome.Error);
        Assert.Equal(0, _db.MarketplaceListings.Single().QuantitySold);
    }

    [Fact]
    public async Task ABuyerIsNeverSoldTheirOwnGoods()
    {
        await Give(Emberhold, Seller, "Lesser Essence", 5);
        await Bazaar.ListMaterialAsync(Emberhold, Seller, "Lesser Essence", 5);
        await Gold.GrantAsync(Emberhold, Seller, 10_000, "test");

        var own = await Bazaar.BuyMaterialAsync(Emberhold, Seller, "Lesser Essence", 1, null);
        Assert.Equal(BazaarError.NotEnoughListed, own.Outcome.Error);
        Assert.Empty(await Bazaar.MaterialsAsync(Seller));
        Assert.Single(await Bazaar.MaterialsAsync(Buyer));
    }

    [Fact]
    public async Task ARepricedItemIsRefused_NotCharged()
    {
        await Give(Emberhold, Seller, "Lesser Essence", 5);
        await Bazaar.ListMaterialAsync(Emberhold, Seller, "Lesser Essence", 5);
        await Gold.GrantAsync(Frostmere, Buyer, 10_000, "test");

        var stale = await Bazaar.BuyMaterialAsync(Frostmere, Buyer, "Lesser Essence", 1, quotedUnitPrice: 1);
        Assert.Equal(BazaarError.PriceChanged, stale.Outcome.Error);
        Assert.Equal(10_000, await Gold.BalanceAsync(Frostmere, Buyer));
    }

    [Fact]
    public async Task MyListingsSayHowManyStandAhead()
    {
        await Give(Frostmere, Rival, "Lesser Essence", 22);
        var (_, theirs) = await Bazaar.ListMaterialAsync(Frostmere, Rival, "Lesser Essence", 22);
        theirs!.CreatedAt = DateTime.UtcNow.AddMinutes(-5);
        _db.SaveChanges();
        await Give(Emberhold, Seller, "Lesser Essence", 6);
        await Bazaar.ListMaterialAsync(Emberhold, Seller, "Lesser Essence", 6);

        var mine = Assert.Single(await Bazaar.MineAsync(Emberhold, Seller));
        Assert.Equal(22, mine.UnitsAhead);
        Assert.Equal(BazaarAssay.PriceOf(MaterialCategory.Essence, MaterialTier.Tier1), mine.UnitPrice);
    }

    // -----------------------------------------------------------------
    // Equipment
    // -----------------------------------------------------------------

    [Fact]
    public async Task EquipmentCrossesWorlds_AndArrivesInTheBuyersLedger()
    {
        await Ledger.GrantAsync(Emberhold, Seller, new[] { Sword("blade-1") }, "test");
        var (listed, listing) = await Bazaar.ListEquipmentAsync(Emberhold, Seller, "blade-1");
        Assert.True(listed.Succeeded);

        var board = Assert.Single(await Bazaar.EquipmentAsync(Buyer));
        Assert.Equal("Emberhold", board.FromWorld);
        Assert.Equal(BazaarAssay.PriceOf(Sword("blade-1")), board.Price);

        await Gold.GrantAsync(Frostmere, Buyer, 100_000, "test");
        var bought = await Bazaar.BuyEquipmentAsync(Frostmere, Buyer, listing!.Id, board.Price);
        Assert.True(bought.Outcome.Succeeded);
        Assert.Equal("blade-1", bought.Item!.Id);

        Assert.Single(await Ledger.HeldAsync(Frostmere, Buyer));
        Assert.Empty(await Ledger.HeldAsync(Emberhold, Seller));
        Assert.Equal(BazaarAssay.NetOf(board.Price), await Bazaar.OwedAsync(Emberhold, Seller));

        var sold = Assert.Single(await Bazaar.MineAsync(Emberhold, Seller));
        Assert.Equal("Frostmere", sold.SoldIntoWorld);
    }

    [Fact]
    public async Task AnItemIdTakenInTheBuyersWorld_IsGivenAFreshOne()
    {
        await Ledger.GrantAsync(Emberhold, Seller, new[] { Sword("same-id") }, "test");
        await Ledger.GrantAsync(Frostmere, "someone", new[] { Sword("same-id") }, "test");
        var (_, listing) = await Bazaar.ListEquipmentAsync(Emberhold, Seller, "same-id");
        await Gold.GrantAsync(Frostmere, Buyer, 100_000, "test");

        var bought = await Bazaar.BuyEquipmentAsync(Frostmere, Buyer, listing!.Id, null);

        Assert.NotEqual("same-id", bought.Item!.Id);
        Assert.Equal(bought.Item.Id, Assert.Single(await Ledger.HeldAsync(Frostmere, Buyer)).ItemId);
    }

    [Fact]
    public async Task EquipmentIsSoldOnce()
    {
        await Ledger.GrantAsync(Emberhold, Seller, new[] { Sword("blade-1") }, "test");
        var (_, listing) = await Bazaar.ListEquipmentAsync(Emberhold, Seller, "blade-1");
        await Gold.GrantAsync(Frostmere, Buyer, 100_000, "test");
        await Gold.GrantAsync(Frostmere, Rival, 100_000, "test");

        Assert.True((await Bazaar.BuyEquipmentAsync(Frostmere, Buyer, listing!.Id, null)).Outcome.Succeeded);
        var late = await Bazaar.BuyEquipmentAsync(Frostmere, Rival, listing.Id, null);
        Assert.Equal(BazaarError.NotActive, late.Outcome.Error);
        Assert.Equal(100_000, await Gold.BalanceAsync(Frostmere, Rival));
    }

    [Fact]
    public async Task PullingBackEquipmentReturnsIt()
    {
        await Ledger.GrantAsync(Emberhold, Seller, new[] { Sword("blade-1") }, "test");
        var (_, listing) = await Bazaar.ListEquipmentAsync(Emberhold, Seller, "blade-1");
        Assert.Empty(await Ledger.HeldAsync(Emberhold, Seller));

        var (outcome, item, _) = await Bazaar.PullBackAsync(Emberhold, Seller, listing!.Id);
        Assert.True(outcome.Succeeded);
        Assert.Equal("blade-1", item!.Id);
        Assert.Single(await Ledger.HeldAsync(Emberhold, Seller));
    }

    [Fact]
    public async Task OnlyTheSellerMayPullBack_FromTheWorldItCameFrom()
    {
        await Ledger.GrantAsync(Emberhold, Seller, new[] { Sword("blade-1") }, "test");
        var (_, listing) = await Bazaar.ListEquipmentAsync(Emberhold, Seller, "blade-1");

        Assert.Equal(BazaarError.NotYours, (await Bazaar.PullBackAsync(Emberhold, Rival, listing!.Id)).Outcome.Error);
        Assert.Equal(BazaarError.NotYours, (await Bazaar.PullBackAsync(Frostmere, Seller, listing.Id)).Outcome.Error);
    }

    // -----------------------------------------------------------------
    // A world resets
    // -----------------------------------------------------------------

    [Fact]
    public async Task AResetDestroysThatWorldsUnsoldGoods_ButPaysWhatWasEarned()
    {
        await Give(Emberhold, Seller, "Lesser Essence", 10);
        await Bazaar.ListMaterialAsync(Emberhold, Seller, "Lesser Essence", 10);
        await Ledger.GrantAsync(Emberhold, Seller, new[] { Sword("blade-1") }, "test");
        await Bazaar.ListEquipmentAsync(Emberhold, Seller, "blade-1");
        await Give(Frostmere, Rival, "Legendary Shard", 2);
        await Bazaar.ListMaterialAsync(Frostmere, Rival, "Legendary Shard", 2);

        await Gold.GrantAsync(Frostmere, Buyer, 100_000, "test");
        await Bazaar.BuyMaterialAsync(Frostmere, Buyer, "Lesser Essence", 4, null);
        long earned = await Bazaar.OwedAsync(Emberhold, Seller);

        await BazaarService.ExpireRealmAsync(_db, Gold, _log, Emberhold);

        Assert.Equal(earned, await Gold.BalanceAsync(Emberhold, Seller));
        Assert.Equal(0, await Bazaar.OwedAsync(Emberhold, Seller));
        Assert.Empty(await Bazaar.EquipmentAsync(Buyer));
        Assert.Empty(_db.ItemGrants.Where(g => g.ItemId == "blade-1"));
        Assert.Equal(0, await Held(Emberhold, Seller, "Lesser Essence"));   // destroyed, not returned

        // Another world's goods are untouched.
        var left = Assert.Single(await Bazaar.MaterialsAsync(Buyer));
        Assert.Equal("Legendary Shard", left.Name);
    }
}
