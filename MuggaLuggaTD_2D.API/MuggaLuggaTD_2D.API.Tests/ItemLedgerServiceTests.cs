using System.Text.Json.Nodes;
using Enums;
using Items.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The equipment ledger. What is pinned: a save can neither invent an item nor improve one; who wears
/// what is the save's own business; items held before the ledger are adopted from the <i>stored</i>
/// save, once; and the marketplace moves an item rather than copying it.
/// </summary>
public class ItemLedgerServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private static readonly Guid Realm = Guid.NewGuid();
    private const string Player = "player-1";
    private const string Buyer = "player-2";

    private ItemLedgerService Ledger => new(_db, new FakeSessionLog(), NullLogger<ItemLedgerService>.Instance);

    public void Dispose() => _db.Dispose();

    private static ItemSaveData Sword(string id, float damage = 12f, ItemRarityTypes rarity = ItemRarityTypes.Magic) => new()
    {
        Id = id,
        ItemName = "Iron Sword",
        ItemCount = 1,
        ItemType = ItemTypes.Weapon,
        Rarity = rarity,
        PowerTier = ItemPowerTier.Tier2,
        ImplicitAttribute = new ItemImplicitAttribute { Type = ItemImplicitTypes.MovementSpeed, Value = damage },
    };

    /// <summary>A save as the client writes it: Newtonsoft, with the inventory in InventoryItems.</summary>
    private static JsonNode SaveHolding(params ItemSaveData[] items)
    {
        var save = new UserSaveData { Username = "tester", InventoryItems = items.ToList() };
        return JsonNode.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(save))!;
    }

    private static JsonArray Items(JsonNode save) => ItemLedgerService.SaveItems(save)!;

    private void StoreSave(string userId, JsonNode save)
    {
        _db.PlayerGameData.Add(new PlayerGameData
        {
            GameInstanceId = Realm, UserId = userId, GameData = save.ToJsonString(),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task AGrantedItemIsKept_AsItWasWrittenByTheClient()
    {
        await Ledger.GrantAsync(Realm, Player, new[] { Sword("s1") }, "pve-claim run=1");

        var save = SaveHolding(Sword("s1"));
        var result = await Ledger.ReconcileSaveAsync(Realm, Player, save);

        Assert.Equal(1, result.Kept);
        Assert.False(result.Changed);   // Newtonsoft's 12.0 and the ledger's 12 are the same item
    }

    [Fact]
    public async Task AnItemNobodyGrantedIsDropped()
    {
        await Ledger.GrantAsync(Realm, Player, new[] { Sword("s1") }, "pve-claim run=1");

        var save = SaveHolding(Sword("s1"), Sword("forged", rarity: ItemRarityTypes.Legendary));
        var result = await Ledger.ReconcileSaveAsync(Realm, Player, save);

        Assert.Equal(1, result.Dropped);
        Assert.Equal("s1", (string)Items(save).Single()!["Id"]!);
    }

    [Fact]
    public async Task AnImprovedItemIsWrittenBackToWhatWasGranted_ButKeepsWhoWearsIt()
    {
        await Ledger.GrantAsync(Realm, Player, new[] { Sword("s1", damage: 12f) }, "pve-claim run=1");

        var tampered = Sword("s1", damage: 999f, rarity: ItemRarityTypes.Legendary);
        tampered.EquippedByCharacterId = "hero-7";
        var save = SaveHolding(tampered);
        var result = await Ledger.ReconcileSaveAsync(Realm, Player, save);

        Assert.Equal(1, result.Corrected);
        var item = Items(save).Single()!;
        Assert.Equal((int)ItemRarityTypes.Magic, (int)item["Rarity"]!);
        Assert.Equal(12f, (float)item["ImplicitAttribute"]!["Value"]!);
        Assert.Equal("hero-7", (string)item["EquippedByCharacterId"]!);
    }

    [Fact]
    public async Task OneGrantCannotBeCarriedTwice()
    {
        await Ledger.GrantAsync(Realm, Player, new[] { Sword("s1") }, "pve-claim run=1");

        var save = SaveHolding(Sword("s1"), Sword("s1"));
        await Ledger.ReconcileSaveAsync(Realm, Player, save);

        Assert.Single(Items(save));
    }

    [Fact]
    public async Task MaterialsAreNotTheLedgersBusiness()
    {
        var essence = new ItemSaveData { Id = "m1", ItemName = "Lesser Essence", ItemType = ItemTypes.Material, ItemCount = 9 };
        var save = SaveHolding(essence);

        var result = await Ledger.ReconcileSaveAsync(Realm, Player, save);

        Assert.Equal(0, result.Dropped);
        Assert.Single(Items(save));
    }

    [Fact]
    public async Task ItemsHeldBeforeTheLedger_AreAdoptedFromTheStoredSave_NotTheArrivingOne()
    {
        StoreSave(Player, SaveHolding(Sword("old-1")));

        // The first save after the ledger arrives tries to bring a new sword along with the old one.
        var arriving = SaveHolding(Sword("old-1"), Sword("smuggled"));
        var result = await Ledger.ReconcileSaveAsync(Realm, Player, arriving);

        Assert.Equal(1, result.Kept);
        Assert.Equal(1, result.Dropped);
        Assert.Equal("old-1", (string)Items(arriving).Single()!["Id"]!);
    }

    [Fact]
    public async Task AdoptionHappensOnce()
    {
        StoreSave(Player, SaveHolding(Sword("old-1")));
        await Ledger.EnsureAdoptedAsync(Realm, Player);

        // The stored save changes later (as it will: every save rewrites it); nothing more is adopted.
        var stored = _db.PlayerGameData.Single();
        stored.GameData = SaveHolding(Sword("old-1"), Sword("later")).ToJsonString();
        _db.SaveChanges();
        await Ledger.EnsureAdoptedAsync(Realm, Player);

        Assert.Single(_db.ItemGrants.Where(g => g.UserId == Player));
    }

    [Fact]
    public async Task AFirstClaimAdoptsBeforeItGrants_SoOlderItemsAreNotLost()
    {
        StoreSave(Player, SaveHolding(Sword("old-1")));
        await Ledger.GrantAsync(Realm, Player, new[] { Sword("new-1") }, "pve-claim run=1");

        var save = SaveHolding(Sword("old-1"), Sword("new-1"));
        var result = await Ledger.ReconcileSaveAsync(Realm, Player, save);

        Assert.Equal(2, result.Kept);
    }

    [Fact]
    public async Task ASaleMovesTheItem_FromTheSellersSaveToTheBuyers()
    {
        await Ledger.GrantAsync(Realm, Player, new[] { Sword("s1") }, "pve-claim run=1");
        var listing = Guid.NewGuid();

        var listed = await Ledger.EscrowAsync(Realm, Player, "s1", listing);
        Assert.NotNull(listed);

        // Listed: out of the seller's inventory.
        var sellerSave = SaveHolding(Sword("s1"));
        await Ledger.ReconcileSaveAsync(Realm, Player, sellerSave);
        Assert.Empty(Items(sellerSave));

        Assert.True(await Ledger.TransferAsync(Realm, listing, Buyer));
        var buyerSave = SaveHolding(Sword("s1"));
        var result = await Ledger.ReconcileSaveAsync(Realm, Buyer, buyerSave);
        Assert.Equal(1, result.Kept);
    }

    [Fact]
    public async Task YouCannotListWhatYouDoNotHold_OrListOneThingTwice()
    {
        await Ledger.GrantAsync(Realm, Player, new[] { Sword("s1") }, "pve-claim run=1");

        Assert.Null(await Ledger.EscrowAsync(Realm, Buyer, "s1", Guid.NewGuid()));
        Assert.NotNull(await Ledger.EscrowAsync(Realm, Player, "s1", Guid.NewGuid()));
        Assert.Null(await Ledger.EscrowAsync(Realm, Player, "s1", Guid.NewGuid()));
    }

    [Fact]
    public async Task ACancelledListingGivesTheItemBack()
    {
        await Ledger.GrantAsync(Realm, Player, new[] { Sword("s1") }, "pve-claim run=1");
        var listing = Guid.NewGuid();
        await Ledger.EscrowAsync(Realm, Player, "s1", listing);
        await Ledger.ReleaseAsync(Realm, listing);

        var save = SaveHolding(Sword("s1"));
        var result = await Ledger.ReconcileSaveAsync(Realm, Player, save);
        Assert.Equal(1, result.Kept);
    }
}
