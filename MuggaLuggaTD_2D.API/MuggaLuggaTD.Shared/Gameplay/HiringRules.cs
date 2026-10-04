using System;
using System.Collections.Generic;
using System.Linq;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>How good a hired local is at their trade: it sets the price and the base rate.</summary>
    public enum WorkerTier : short
    {
        Local = 0,
        Skilled = 1,
        Master = 2
    }

    /// <summary>
    /// What makes one worker better than another (Mike kept all six, 2026-10-01). Every trait is a
    /// multiplier on a rate the server already works out, never a new mechanic.
    /// </summary>
    public enum WorkerTrait : short
    {
        /// <summary>+15% rate.</summary>
        Steady = 0,
        /// <summary>+40% working in their home biome.</summary>
        Hometown = 1,
        /// <summary>Can also work a second, rolled trade, at half rate.</summary>
        Versatile = 2,
        /// <summary>+25% at a tier-3+ site.</summary>
        Prospector = 3,
        /// <summary>One hour in ten pays double.</summary>
        Lucky = 4,
        /// <summary>+10% to every other worker at the same site.</summary>
        Foreman = 5
    }

    /// <summary>A worker as the board rolled them. The server keeps it; the client only shows it.</summary>
    public sealed class WorkerRoll
    {
        public string Name;
        public ResourceTrade Trade;
        public WorkerTier Tier;
        public List<WorkerTrait> Traits = new List<WorkerTrait>();
        /// <summary>The second trade a <see cref="WorkerTrait.Versatile"/> worker can work; else null.</summary>
        public ResourceTrade? SecondTrade;
        public BiomeType HomeBiome;
        /// <summary>Which look the client draws them in. Cosmetic; any number is valid.</summary>
        public int Look;
    }

    /// <summary>
    /// The Hiring Hall (design 12e, plan docs/design/hiring-hall.md): locals hired with gold to work
    /// resource sites in regions their employer holds. Shared so the client shows the rates, costs
    /// and odds the server will hold it to; the client never rolls or prices a worker.
    ///
    /// <para><b>Output is lazy, like gold.</b> A worker at a site has a rate and a since-when, and
    /// what they have gathered is worked out when someone looks (<see cref="Gathered"/>). Nothing
    /// ticks.</para>
    /// </summary>
    public static class HiringRules
    {
        public const int BoardSize = 6;

        /// <summary>A new local arrives in a free seat this often (the design's timer).</summary>
        public static readonly TimeSpan ArrivalInterval = TimeSpan.FromHours(2);

        /// <summary>The first paid refresh; each one after doubles, until a dungeon is cleared (as the Tavern's).</summary>
        public const long RefreshBaseCost = 100;
        public const int RefreshMaxDoublings = 6;

        /// <summary>Beds: how many workers a player may employ for each region they hold (tune).</summary>
        public const int BedsPerRegion = 2;

        public const double SteadyFactor = 1.15;
        public const double HometownFactor = 1.40;
        public const double ProspectorFactor = 1.25;
        public const int ProspectorMinimumTier = 3;
        public const double ForemanBonus = 0.10;
        public const double SecondTradeFactor = 0.5;
        public const int LuckyOneIn = 10;

        /// <summary>The design's prices: 600 / 1,200 / 2,400 gold, paid once.</summary>
        public static long CostOf(WorkerTier tier) => tier == WorkerTier.Master ? 2400 : tier == WorkerTier.Skilled ? 1200 : 600;

        /// <summary>Goods an hour before traits (tune): 12 / 18 / 26.</summary>
        public static double BaseRate(WorkerTier tier) => tier == WorkerTier.Master ? 26 : tier == WorkerTier.Skilled ? 18 : 12;

        public static int BedsFor(int regionsHeld) => Math.Max(1, regionsHeld) * BedsPerRegion;

        public static long RefreshCostFor(int refreshesSinceClear)
            => RefreshBaseCost << Math.Max(0, Math.Min(RefreshMaxDoublings, refreshesSinceClear));

        /// <summary>Whether a worker can work a site of this trade at all.</summary>
        public static bool CanWork(ResourceTrade trade, ResourceTrade? secondTrade, ResourceTrade siteTrade)
            => trade == siteTrade || (secondTrade.HasValue && secondTrade.Value == siteTrade);

        /// <summary>
        /// A worker's goods an hour at a site, every trait applied. <paramref name="foremenBeside"/>
        /// counts the <i>other</i> workers at that site who are foremen. Zero if they cannot work it.
        /// </summary>
        public static double RateAt(
            WorkerTier tier, ResourceTrade trade, ResourceTrade? secondTrade, IReadOnlyCollection<WorkerTrait> traits,
            BiomeType homeBiome, ResourceTrade siteTrade, BiomeType siteBiome, int siteTier, int foremenBeside)
        {
            if (!CanWork(trade, secondTrade, siteTrade)) return 0;
            traits = traits ?? Array.Empty<WorkerTrait>();

            double rate = BaseRate(tier);
            if (trade != siteTrade) rate *= SecondTradeFactor;
            if (traits.Contains(WorkerTrait.Steady)) rate *= SteadyFactor;
            if (traits.Contains(WorkerTrait.Hometown) && homeBiome == siteBiome) rate *= HometownFactor;
            if (traits.Contains(WorkerTrait.Prospector) && siteTier >= ProspectorMinimumTier) rate *= ProspectorFactor;
            if (foremenBeside > 0) rate *= 1.0 + ForemanBonus * foremenBeside;
            return rate;
        }

        /// <summary>Whether a Lucky worker's hour (counted from the epoch) pays double.</summary>
        public static bool IsLuckyHour(string workerId, long hourIndex)
            => (Naming.Hash($"{workerId}:{hourIndex}") % LuckyOneIn) == 0;

        /// <summary>
        /// Goods gathered at <paramref name="ratePerHour"/> between two instants. A Lucky worker's
        /// lucky hours count double, so the luck comes in lumps rather than as a flat +10%.
        /// </summary>
        public static double Gathered(double ratePerHour, DateTime from, DateTime to, bool lucky, string workerId)
            => Gathered(ratePerHour, from, to, lucky, workerId, null);

        /// <summary>
        /// As above, with the hours raiders harried the diggings (<see cref="HarassmentRules"/>): each
        /// pays <see cref="HarassmentRules.HarriedFactor"/> of its work. Null for none.
        /// </summary>
        public static double Gathered(double ratePerHour, DateTime from, DateTime to, bool lucky, string workerId,
            Func<long, bool> harried)
        {
            if (ratePerHour <= 0 || to <= from) return 0;
            double hours = (to - from).TotalHours;
            if (!lucky && harried == null) return ratePerHour * hours;

            double total = 0;
            long first = from.Ticks / TimeSpan.TicksPerHour, last = (to.Ticks - 1) / TimeSpan.TicksPerHour;
            for (long h = first; h <= last; h++)
            {
                var start = new DateTime(Math.Max(from.Ticks, h * TimeSpan.TicksPerHour), DateTimeKind.Utc);
                var end = new DateTime(Math.Min(to.Ticks, (h + 1) * TimeSpan.TicksPerHour), DateTimeKind.Utc);
                double share = (end - start).TotalHours;
                double factor = lucky && IsLuckyHour(workerId, h) ? 2 : 1;
                if (harried != null && harried(h)) factor *= HarassmentRules.HarriedFactor;
                total += ratePerHour * share * factor;
            }
            return total;
        }

        // -----------------------------------------------------------------
        // The roll
        // -----------------------------------------------------------------

        /// <summary>How often each tier walks in (tune): mostly locals, a master now and then.</summary>
        public static readonly IReadOnlyDictionary<WorkerTier, int> TierWeights = new Dictionary<WorkerTier, int>
        {
            [WorkerTier.Local] = 60,
            [WorkerTier.Skilled] = 30,
            [WorkerTier.Master] = 10
        };

        /// <summary>
        /// Rolls one local looking for work. <paramref name="biome"/> is where they come from, which
        /// leans their trade the way the land leans its sites (<see cref="ResourceNodeRules.WeightsFor"/>).
        /// </summary>
        public static WorkerRoll Roll(Random random, BiomeType biome)
        {
            var weights = ResourceNodeRules.WeightsFor(biome);
            var trade = Pick(ResourceNodeRules.Trades.ToList(), weights, random);
            var tier = Pick(TierWeights.Keys.ToList(), TierWeights.Values.ToArray(), random);

            int traitCount = tier == WorkerTier.Master ? 2 : tier == WorkerTier.Skilled ? 1 : random.Next(2);
            var pool = Enum.GetValues(typeof(WorkerTrait)).Cast<WorkerTrait>().ToList();
            var traits = new List<WorkerTrait>();
            for (int i = 0; i < traitCount; i++)
            {
                var trait = pool[random.Next(pool.Count)];
                pool.Remove(trait);
                traits.Add(trait);
            }

            ResourceTrade? second = null;
            if (traits.Contains(WorkerTrait.Versatile))
            {
                var others = ResourceNodeRules.Trades.Where(t => t != trade).ToList();
                second = others[random.Next(others.Count)];
            }

            int look = random.Next(1 << 20);
            return new WorkerRoll
            {
                Name = Naming.ForHero("Npc_Human_Worker", (ulong)look * 2654435761UL + (ulong)random.Next()),
                Trade = trade,
                Tier = tier,
                Traits = traits,
                SecondTrade = second,
                HomeBiome = biome,
                Look = look
            };
        }

        private static T Pick<T>(IList<T> items, IList<int> weights, Random random)
        {
            int total = weights.Sum();
            int roll = random.Next(total);
            for (int i = 0; i < items.Count; i++)
            {
                if (roll < weights[i]) return items[i];
                roll -= weights[i];
            }
            return items[items.Count - 1];
        }

        /// <summary>A one-line description of a trait, for both sides' screens.</summary>
        public static string Describe(WorkerTrait trait, ResourceTrade? secondTrade = null)
        {
            switch (trait)
            {
                case WorkerTrait.Steady: return "Steady: +15% goods.";
                case WorkerTrait.Hometown: return "Hometown: +40% working in their home land.";
                case WorkerTrait.Versatile:
                    return secondTrade.HasValue
                        ? $"Versatile: can also work a {ResourceNodeRules.PlaceOf(secondTrade.Value).ToLowerInvariant()}, at half rate."
                        : "Versatile: can work a second trade, at half rate.";
                case WorkerTrait.Prospector: return "Prospector: +25% at a tier III or IV site.";
                case WorkerTrait.Lucky: return "Lucky: one hour in ten pays double.";
                default: return "Foreman: +10% to everyone else at the same site.";
            }
        }
    }
}
