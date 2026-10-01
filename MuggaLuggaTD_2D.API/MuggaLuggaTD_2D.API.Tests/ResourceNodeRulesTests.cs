using Enums;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using Newtonsoft.Json.Linq;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Resource sites and their goods (Hiring Hall plan). What is pinned: a site's trade is a pure
/// function of its id and biome; the land leans a biome toward its trades but never rules one out;
/// a site is named for its trade; goods are priced in bulk, under the Assay's floor; and a fight
/// never pays goods.
/// </summary>
public class ResourceNodeRulesTests
{
    private static readonly string[] Ids = Enumerable.Range(0, 4000).Select(i => $"r{i / 5}:{i % 5}").ToArray();

    [Fact]
    public void ASitesTradeIsTheSameEveryTime()
    {
        foreach (var biome in Enum.GetValues<BiomeType>())
            foreach (var id in Ids.Take(200))
                Assert.Equal(ResourceNodeRules.TradeOf(id, biome), ResourceNodeRules.TradeOf(id, biome));
    }

    [Fact]
    public void EveryBiomeCanTurnUpEveryTrade_AndLeansToItsOwn()
    {
        foreach (var biome in Enum.GetValues<BiomeType>())
        {
            var counts = ResourceNodeRules.Trades.ToDictionary(t => t, _ => 0);
            foreach (var id in Ids) counts[ResourceNodeRules.TradeOf(id, biome)]++;

            Assert.All(counts, c => Assert.True(c.Value > 0, $"{biome} never turns up {c.Key}"));

            var weights = ResourceNodeRules.WeightsFor(biome);
            var favourite = ResourceNodeRules.Trades[Array.IndexOf(weights, weights.Max())];
            Assert.Equal(favourite, counts.OrderByDescending(c => c.Value).First().Key);
        }
    }

    [Fact]
    public void ASiteIsNamedForWhatIsWorkedThere()
    {
        var words = new Dictionary<ResourceTrade, string[]>
        {
            [ResourceTrade.Miner] = new[] { "Mine", "Diggings", "Seams" },
            [ResourceTrade.Forester] = new[] { "Stand", "Loggings", "Sawpits" },
            [ResourceTrade.Farmer] = new[] { "Fields", "Farm", "Acres" },
            [ResourceTrade.Quarrier] = new[] { "Quarry", "Delves", "Cuttings" },
            [ResourceTrade.Trapper] = new[] { "Snares", "Traplines", "Runs" },
        };

        int checkedSites = 0;
        foreach (var biome in Enum.GetValues<BiomeType>())
        {
            var region = new WorldRegionData { RegionId = $"r-{biome}", Seed = 1000 + (int)biome, Biome = biome, Tier = 2 };
            var nodes = RegionGenerator.Generate(region).Sites.Where(s => s.Type == LocationType.ResourceNode);
            foreach (var site in nodes)
            {
                string name = Naming.SiteName(region, "Testmoor", site);
                var trade = ResourceNodeRules.TradeOf(site.SiteId, biome);
                Assert.Contains(words[trade], w => name.EndsWith(w));
                checkedSites++;
            }
        }
        Assert.True(checkedSites > 0);
    }

    [Fact]
    public void GoodsArePricedInBulk_UnderTheFloor()
    {
        Assert.Equal(BazaarAssay.GoodsUnitPrice, BazaarAssay.PriceOf(MaterialCategory.Goods, MaterialTier.Tier1));
        Assert.True(BazaarAssay.GoodsUnitPrice < BazaarAssay.MinimumPrice);
    }

    [Fact]
    public void AFightNeverPaysGoods()
    {
        var random = new Random(7);
        for (int i = 0; i < 2000; i++)
            Assert.NotEqual(MaterialCategory.Goods, MaterialRewardCalculator.SelectCategory(1 + i % 30, random));
    }

    [Fact]
    public void TheShippedContentCarriesEveryGood_AsUndroppableGoods()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MuggaLuggaTD_2D.API", "GameContent", "MaterialData.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var materials = (JArray)JObject.Parse(File.ReadAllText(
            Path.Combine(dir!.FullName, "MuggaLuggaTD_2D.API", "GameContent", "MaterialData.json")))["Materials"]!;

        foreach (var trade in ResourceNodeRules.Trades)
        {
            string good = ResourceNodeRules.GoodOf(trade);
            var entry = materials.FirstOrDefault(m => (string?)m["ItemName"] == good);
            Assert.True(entry != null, $"content has no {good}");
            Assert.Equal((int)MaterialCategory.Goods, (int)entry!["Category"]!);
            Assert.False((bool)entry["IsDroppable"]!, $"{good} must not drop");
        }
    }
}
