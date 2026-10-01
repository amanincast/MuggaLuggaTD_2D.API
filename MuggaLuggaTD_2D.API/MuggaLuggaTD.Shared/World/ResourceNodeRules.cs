using System.Collections.Generic;

namespace MuggaLuggaTD.Shared.World
{
    /// <summary>The five kinds of work a hired local does (design 12e).</summary>
    public enum ResourceTrade : short
    {
        Miner = 0,
        Forester = 1,
        Farmer = 2,
        Quarrier = 3,
        Trapper = 4
    }

    /// <summary>
    /// What a region's resource sites are (Hiring Hall plan, docs/design/hiring-hall.md): every
    /// <see cref="LocationType.ResourceNode"/> is worked by one trade and yields one good.
    ///
    /// <para><b>The trade is a pure function of the site's id and the region's biome</b>, never
    /// stored, like a site's name: both sides agree by construction, and the generator is untouched
    /// so no world regenerates. The land leans a region toward some trades (mines in the mountains,
    /// timber in the woods) but <b>every biome can turn up every trade</b>, so a player boxed into
    /// one kind of land is short of a good rather than locked out of it.</para>
    /// </summary>
    public static class ResourceNodeRules
    {
        /// <summary>The goods, by trade. These are material names in content (MaterialData).</summary>
        public const string Ore = "Ore";
        public const string Timber = "Timber";
        public const string Grain = "Grain";
        public const string Stone = "Stone";
        public const string Hides = "Hides";

        public static readonly IReadOnlyList<ResourceTrade> Trades = new[]
        {
            ResourceTrade.Miner, ResourceTrade.Forester, ResourceTrade.Farmer, ResourceTrade.Quarrier, ResourceTrade.Trapper
        };

        /// <summary>The good a trade gathers.</summary>
        public static string GoodOf(ResourceTrade trade)
        {
            switch (trade)
            {
                case ResourceTrade.Miner: return Ore;
                case ResourceTrade.Forester: return Timber;
                case ResourceTrade.Farmer: return Grain;
                case ResourceTrade.Quarrier: return Stone;
                default: return Hides;
            }
        }

        /// <summary>What the place is called on the map: MINE, TIMBER STAND…</summary>
        public static string PlaceOf(ResourceTrade trade)
        {
            switch (trade)
            {
                case ResourceTrade.Miner: return "Mine";
                case ResourceTrade.Forester: return "Timber Stand";
                case ResourceTrade.Farmer: return "Farm";
                case ResourceTrade.Quarrier: return "Quarry";
                default: return "Trapping Grounds";
            }
        }

        /// <summary>How many workers a site takes: 2, or 3 in a tier-3+ region (tune).</summary>
        public static int SlotsFor(int regionTier) => regionTier >= 3 ? 3 : 2;

        /// <summary>The trade a resource site is worked by.</summary>
        public static ResourceTrade TradeOf(string siteId, BiomeType biome)
        {
            var weights = WeightsFor(biome);
            int total = 0;
            foreach (int w in weights) total += w;

            var random = DeterministicRandom.ForSubject(TradeSalt, Naming.Hash(siteId ?? ""));
            int roll = random.Next(total);
            for (int i = 0; i < weights.Length; i++)
            {
                if (roll < weights[i]) return Trades[i];
                roll -= weights[i];
            }
            return Trades[0];
        }

        /// <summary>
        /// How strongly each biome leans to each trade, in <see cref="Trades"/> order
        /// (Miner, Forester, Farmer, Quarrier, Trapper). None is zero.
        /// </summary>
        public static int[] WeightsFor(BiomeType biome)
        {
            switch (biome)
            {
                case BiomeType.Forest: return new[] { 1, 5, 1, 1, 3 };
                case BiomeType.Lakeland: return new[] { 1, 2, 4, 1, 2 };
                case BiomeType.Highland: return new[] { 5, 1, 1, 3, 1 };
                case BiomeType.Volcanic: return new[] { 5, 1, 1, 3, 1 };
                case BiomeType.Swamp: return new[] { 1, 2, 2, 1, 5 };
                case BiomeType.Desert: return new[] { 3, 1, 1, 5, 1 };
                default: return new[] { 1, 1, 5, 2, 1 }; // Grassland
            }
        }

        /// <summary>Keeps the trade's roll apart from every other roll keyed by a site id.</summary>
        private const ulong TradeSalt = 0x7472616465UL; // "trade"
    }
}
