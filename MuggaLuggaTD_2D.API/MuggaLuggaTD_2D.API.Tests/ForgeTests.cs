using System.Text.Json;
using System.Text.Json.Nodes;
using Enums;
using Items.Models;
using Items.Utilities;
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
/// The Forge, phase 1 (Forge spec; Mike 2026-10-10). What a player should be able to rely on:
/// - Temper and Ascend raise a piece and keep its rolls, near enough (±5%: no perfect craft).
/// - They cost what the table says, or nothing at all.
/// - A worn piece stays worn, and its new power counts at once.
/// - Ascend eats its second piece; Salvage gives materials.
/// - A listed or unheld piece can't be touched.
/// </summary>
public class ForgeTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private Guid _realm;
    private const string Me = TestIds.Player;

    private readonly FakeGameContent _content = new()
    {
        DroppableItems = new[] { new ItemTemplate { ItemName = "Cinder Blade", ItemType = ItemTypes.Weapon } },
        Materials = new[]
        {
            new MaterialTemplate { MaterialName = "Lesser Essence", Category = MaterialCategory.Essence, Tier = MaterialTier.Tier1 },
            new MaterialTemplate { MaterialName = "Greater Essence", Category = MaterialCategory.Essence, Tier = MaterialTier.Tier2 },
            new MaterialTemplate { MaterialName = "Common Shard", Category = MaterialCategory.RarityShard, Tier = MaterialTier.Tier1 },
            new MaterialTemplate { MaterialName = "Minor Fire Crystal", Category = MaterialCategory.AffinityCrystal, Tier = MaterialTier.Tier1, AffinityType = AffinityTypes.Fire },
        }
    };

    public void Dispose() => _db.Dispose();

    private GoldService Gold => new(_db, _log, NullLogger<GoldService>.Instance);
    private MaterialWalletService Wallet => new(_db, _log, NullLogger<MaterialWalletService>.Instance);
    private ItemLedgerService Ledger => new(_db, _log, NullLogger<ItemLedgerService>.Instance);
    private ForgeService Forge => new(_db, _content, Ledger, Wallet, Gold, _log, NullLogger<ForgeService>.Instance);

    /// <summary>A blade with two explicit rolls at known qualities: Health at 80%, Evasion at 100%.</summary>
    private static ItemSaveData Blade(string id, ItemRarityTypes rarity = ItemRarityTypes.Common, int tier = 2,
        ItemTypes type = ItemTypes.Weapon, string? wornBy = null)
    {
        var t = (ItemPowerTier)tier;
        return new ItemSaveData
        {
            Id = id, ItemName = "Cinder Blade", ItemType = type, ItemCount = 1, Rarity = rarity, PowerTier = t,
            EquippedByCharacterId = wornBy,
            ImplicitAttribute = new ItemImplicitAttribute { Type = ItemImplicitTypes.MovementSpeed, Value = ItemAttributeRoller.ImplicitValue(ItemImplicitTypes.MovementSpeed, t) },
            ExplicitAttributes = new List<ItemExplicitAttribute>
            {
                new() { Type = ItemExplicitTypes.Health, Value = 0.8f * ItemAttributeRoller.MaxExplicitValue(ItemExplicitTypes.Health, t, rarity, false) },
                new() { Type = ItemExplicitTypes.EvasionFlat, Value = ItemAttributeRoller.MaxExplicitValue(ItemExplicitTypes.EvasionFlat, t, rarity, false) },
            },
        };
    }

    /// <summary>A realm where I hold <paramref name="items"/> (granted and saved), my best hero at <paramref name="level"/>.</summary>
    private async Task RealmAsync(int level, params ItemSaveData[] items)
    {
        var realm = await _db.AddInstanceAsync();
        _realm = realm.Id;
        var save = new JsonObject
        {
            ["Characters"] = new JsonArray(new JsonObject { ["Id"] = "hero-1", ["Level"] = level }),
            ["InventoryItems"] = new JsonArray(items.Select(i => JsonSerializer.SerializeToNode(i)).ToArray()),
        };
        // Granted before the save exists, as a run's claim would be, so the ledger adopts nothing twice.
        await Ledger.GrantAsync(_realm, Me, items.Select(i => { var c = Copy(i); c.EquippedByCharacterId = null; return c; }), "test");
        await _db.AddPlayerSaveAsync(_realm, Me, save.ToJsonString());
    }

    private static ItemSaveData Copy(ItemSaveData item) => JsonSerializer.Deserialize<ItemSaveData>(JsonSerializer.Serialize(item))!;

    private async Task Give(string material, int quantity) =>
        await Wallet.GrantAsync(_realm, Me, new[] { new MaterialGrant { MaterialName = material, Quantity = quantity } }, "test");

    private async Task<int> Held(string material) =>
        (await Wallet.ReadAsync(_realm, Me)).FirstOrDefault(m => m.MaterialName == material)?.Quantity ?? 0;

    private async Task<JsonObject?> StoredItemAsync(string id)
    {
        var json = await _db.PlayerGameData.AsNoTracking().Where(p => p.GameInstanceId == _realm && p.UserId == Me).Select(p => p.GameData).FirstAsync();
        return ItemLedgerService.SaveItems(JsonNode.Parse(json))!.OfType<JsonObject>().FirstOrDefault(i => (string?)i["Id"] == id);
    }

    private static double Quality(ItemExplicitAttribute a, ItemPowerTier tier, ItemRarityTypes rarity) =>
        a.Value / ItemAttributeRoller.MaxExplicitValue(a.Type, tier, rarity, a.IsInfluenceBonus);

    // -----------------------------------------------------------------
    // The rules
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(2, "Lesser Essence", 2, 100)]
    [InlineData(4, "Lesser Essence", 4, 200)]
    [InlineData(5, "Greater Essence", 2, 300)]
    [InlineData(7, "Greater Essence", 4, 500)]
    [InlineData(8, "Supreme Essence", 2, 750)]
    [InlineData(10, "Supreme Essence", 4, 1500)]
    public void TemperCosts_FollowTheTable(int toTier, string essence, int count, long gold)
    {
        var cost = ForgeRules.TemperTable(toTier);
        Assert.Equal(essence, cost.Materials.Single().MaterialName);
        Assert.Equal(count, cost.Materials.Single().Quantity);
        Assert.Equal(gold, cost.Gold);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(12, 5)]
    [InlineData(30, 10)]
    public void TheTemperCap_IsWhatADropCouldReachAtTheBestHerosLevel(int level, int cap) =>
        Assert.Equal(cap, ForgeRules.TemperCap(level));

    [Fact]
    public void AnUpgrade_KeepsEachRollsQuality_WithoutDriftInAPreview()
    {
        var item = Blade("a");
        ForgeRules.Raise(item, ItemPowerTier.Tier3, ItemRarityTypes.Uncommon, random: null);
        Assert.Equal(0.8, Quality(item.ExplicitAttributes[0], ItemPowerTier.Tier3, ItemRarityTypes.Uncommon), 3);
        Assert.Equal(1.0, Quality(item.ExplicitAttributes[1], ItemPowerTier.Tier3, ItemRarityTypes.Uncommon), 3);
        Assert.Equal(ItemAttributeRoller.ImplicitValue(ItemImplicitTypes.MovementSpeed, ItemPowerTier.Tier3), item.ImplicitAttribute.Value);
    }

    [Fact]
    public void TheDrift_MovesARollAtMostFivePercent_AndNeverOutOfItsRange()
    {
        var random = new Random(7);
        for (int i = 0; i < 500; i++)
        {
            var item = Blade("a");
            ForgeRules.Temper(item, random);
            double health = Quality(item.ExplicitAttributes[0], item.PowerTier, item.Rarity);
            double evasion = Quality(item.ExplicitAttributes[1], item.PowerTier, item.Rarity);
            Assert.InRange(health, 0.75 - 1e-6, 0.85 + 1e-6);
            Assert.InRange(evasion, 0.95 - 1e-6, 1.0 + 1e-6);
        }
    }

    [Fact]
    public void APerfectRoll_CannotBeLockedIn()
    {
        var random = new Random(11);
        var qualities = Enumerable.Range(0, 200).Select(_ =>
        {
            var item = Blade("a");
            ForgeRules.Temper(item, random);
            return Quality(item.ExplicitAttributes[1], item.PowerTier, item.Rarity);
        }).ToList();
        Assert.Contains(qualities, q => q < 0.99);
    }

    [Fact]
    public void AnUpgrade_OpensTheSlotsItsTierAndRarityGrant()
    {
        var temper = Blade("a", tier: 2);
        ForgeRules.Temper(temper, new Random(1));
        Assert.Equal(ItemAttributeRoller.GetTotalExplicitCount(ItemPowerTier.Tier3, ItemRarityTypes.Common), temper.ExplicitAttributes.Count);

        var ascend = Blade("b", ItemRarityTypes.Uncommon);
        ForgeRules.Ascend(ascend, new Random(1));
        Assert.Equal(ItemRarityTypes.Magic, ascend.Rarity);
        Assert.Equal(ItemAttributeRoller.GetTotalExplicitCount(ItemPowerTier.Tier2, ItemRarityTypes.Magic), ascend.ExplicitAttributes.Count);
    }

    [Fact]
    public void AscendStops_AtLegendary_AndWantsAFitSecondPiece()
    {
        Assert.NotNull(ForgeRules.CantAscend(Blade("a", ItemRarityTypes.Legendary), null));
        var rare = Blade("a", ItemRarityTypes.Rare);
        Assert.Null(ForgeRules.CantAscend(rare, Blade("b", ItemRarityTypes.Magic)));
        Assert.NotNull(ForgeRules.CantAscend(rare, Blade("b", ItemRarityTypes.Uncommon)));
        Assert.NotNull(ForgeRules.CantAscend(rare, Blade("b", ItemRarityTypes.Rare, type: ItemTypes.Gloves)));
        Assert.NotNull(ForgeRules.CantAscend(rare, rare));
    }

    [Fact]
    public void Salvage_GivesEssenceByTier_AShardByRarity_AndACrystalPerInfluence()
    {
        var item = Blade("a", ItemRarityTypes.Rare, tier: 7);
        item.Influences = new List<ItemInfluence> { new() { AffinityType = AffinityTypes.Fire, Stars = 2 } };
        var table = ForgeRules.SalvageTable(item, a => a == AffinityTypes.Fire ? "Minor Fire Crystal" : null);

        Assert.Contains(table, l => l.MaterialName == "Greater Essence" && l.Quantity == 3 && l.Chance == 1);
        Assert.Contains(table, l => l.MaterialName == "Common Shard" && l.Chance == 1);
        Assert.Contains(table, l => l.MaterialName == "Rare Shard" && l.Chance == 0.25);
        Assert.Contains(table, l => l.MaterialName == "Minor Fire Crystal");
    }

    [Fact]
    public void ATemper_CostsMoreThanThePriceItAdds_SoTemperingToSellDoesNotPay()
    {
        // The Bazaar prices gear by its power; if a step cost less than the price it adds, the Forge
        // would print gold (spec §10). Checked for every step, for a Common and a Rare piece.
        foreach (var rarity in new[] { ItemRarityTypes.Common, ItemRarityTypes.Rare })
            for (int tier = 1; tier < 10; tier++)
            {
                var before = Blade("a", rarity, tier);
                var after = ForgeRules.Preview(before, (ItemPowerTier)(tier + 1), rarity);
                long gain = BazaarAssay.NetOf(BazaarAssay.PriceOf(after)) - BazaarAssay.NetOf(BazaarAssay.PriceOf(before));
                var cost = ForgeRules.TemperCost(before);
                long materials = cost.Materials.Sum(m => m.Quantity * BazaarAssay.PriceOf(MaterialCategory.Essence,
                    m.MaterialName == ForgeRules.LesserEssence ? MaterialTier.Tier1 : m.MaterialName == ForgeRules.GreaterEssence ? MaterialTier.Tier2 : MaterialTier.Tier3));
                Assert.True(cost.Gold + materials >= gain, $"{rarity} tier {tier}->{tier + 1}: costs {cost.Gold}+{materials}, adds {gain}");
            }
    }

    // -----------------------------------------------------------------
    // The service
    // -----------------------------------------------------------------

    [Fact]
    public async Task Tempering_PaysTheTable_RaisesThePiece_AndAWornPieceStaysWornAndCountsAtOnce()
    {
        await RealmAsync(12, Blade("blade", wornBy: "hero-1"));
        await Give("Lesser Essence", 5);
        await Gold.GrantAsync(_realm, Me, 1000, "test");

        var outcome = await Forge.TemperAsync(_realm, Me, "blade");
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(ItemPowerTier.Tier3, outcome.Response!.Item!.PowerTier);
        Assert.Equal(2, await Held("Lesser Essence"));
        Assert.Equal(1000 - ForgeRules.TemperCost(Blade("blade")).Gold, await Gold.BalanceAsync(_realm, Me));

        var stored = await StoredItemAsync("blade");
        Assert.Equal((int)ItemPowerTier.Tier3, (int)stored!["PowerTier"]!);
        Assert.Equal("hero-1", (string?)stored["EquippedByCharacterId"]);
        Assert.Equal(ItemPowerTier.Tier3, (await Ledger.FindAsync(_realm, Me, "blade"))!.Value.Item.PowerTier);
    }

    [Fact]
    public async Task WithoutTheMaterialsOrGold_NothingIsSpent()
    {
        await RealmAsync(12, Blade("blade"));
        await Give("Lesser Essence", 5);

        var outcome = await Forge.TemperAsync(_realm, Me, "blade");
        Assert.Equal(ForgeError.NotEnough, outcome.Error);
        Assert.Equal(5, await Held("Lesser Essence"));

        await Gold.GrantAsync(_realm, Me, 1000, "test");
        _db.PlayerMaterials.RemoveRange(_db.PlayerMaterials);
        await _db.SaveChangesAsync();
        Assert.Equal(ForgeError.NotEnough, (await Forge.TemperAsync(_realm, Me, "blade")).Error);
        Assert.Equal(1000, await Gold.BalanceAsync(_realm, Me));
    }

    [Fact]
    public async Task APieceCannotBeTemperedPastWhatTheHeroesCouldFind()
    {
        await RealmAsync(3, Blade("blade", tier: 2));
        await Give("Lesser Essence", 5);
        await Gold.GrantAsync(_realm, Me, 1000, "test");

        var outcome = await Forge.TemperAsync(_realm, Me, "blade");
        Assert.Equal(ForgeError.Refused, outcome.Error);
        Assert.Contains("level 6", outcome.Message);
    }

    [Fact]
    public async Task Ascending_EatsTheSecondPiece()
    {
        await RealmAsync(12, Blade("blade"), Blade("fodder"));
        await Give("Common Shard", 1);
        await Gold.GrantAsync(_realm, Me, 1000, "test");

        var outcome = await Forge.AscendAsync(_realm, Me, "blade", "fodder");
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(ItemRarityTypes.Uncommon, outcome.Response!.Item!.Rarity);
        Assert.Equal("fodder", outcome.Response.RemovedItemId);
        Assert.Null(await Ledger.FindAsync(_realm, Me, "fodder"));
        Assert.Null(await StoredItemAsync("fodder"));
    }

    [Fact]
    public async Task AWornSecondPiece_IsNotEaten()
    {
        await RealmAsync(12, Blade("blade"), Blade("fodder", wornBy: "hero-1"));
        await Give("Common Shard", 1);
        await Gold.GrantAsync(_realm, Me, 1000, "test");

        Assert.Equal(ForgeError.Worn, (await Forge.AscendAsync(_realm, Me, "blade", "fodder")).Error);
        Assert.Equal(1, await Held("Common Shard"));
    }

    [Fact]
    public async Task Salvaging_GivesMaterials_AndThePieceIsGone()
    {
        var item = Blade("old", ItemRarityTypes.Magic, tier: 4);
        item.Influences = new List<ItemInfluence> { new() { AffinityType = AffinityTypes.Fire, Stars = 1 } };
        await RealmAsync(12, item);

        var outcome = await Forge.SalvageAsync(_realm, Me, "old");
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(2, await Held("Lesser Essence"));
        Assert.Equal(1, await Held("Common Shard"));
        Assert.Equal(1, await Held("Minor Fire Crystal"));
        Assert.Null(await Ledger.FindAsync(_realm, Me, "old"));
    }

    [Fact]
    public async Task AWornPiece_IsNotBrokenDown()
    {
        await RealmAsync(12, Blade("blade", wornBy: "hero-1"));
        Assert.Equal(ForgeError.Worn, (await Forge.SalvageAsync(_realm, Me, "blade")).Error);
        Assert.NotNull(await Ledger.FindAsync(_realm, Me, "blade"));
    }

    [Fact]
    public async Task APieceOnTheBazaar_OrNotHeld_CannotBeTouched()
    {
        await RealmAsync(12, Blade("blade"));
        await Ledger.EscrowAsync(_realm, Me, "blade", Guid.NewGuid());
        Assert.Equal(ForgeError.Listed, (await Forge.SalvageAsync(_realm, Me, "blade")).Error);
        Assert.Equal(ForgeError.NoSuchItem, (await Forge.TemperAsync(_realm, Me, "someone-elses")).Error);
    }

    [Fact]
    public async Task AnOldSave_IsWrittenBackToTheForgedPiece()
    {
        var blade = Blade("blade");
        await RealmAsync(12, blade);
        await Give("Lesser Essence", 5);
        await Gold.GrantAsync(_realm, Me, 1000, "test");
        await Forge.TemperAsync(_realm, Me, "blade");

        // A save sent before the forge still carries tier 2; the ledger corrects it.
        var old = new JsonObject { ["InventoryItems"] = new JsonArray(JsonSerializer.SerializeToNode(blade)) };
        var result = await Ledger.ReconcileSaveAsync(_realm, Me, old);
        Assert.Equal(1, result.Corrected);
        Assert.Equal((int)ItemPowerTier.Tier3, (int)ItemLedgerService.SaveItems(old)![0]!["PowerTier"]!);
    }
}
