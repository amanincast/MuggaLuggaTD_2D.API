using System;
using System.Collections.Generic;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>How great the danger of a road is, as the player is shown it.</summary>
    public enum AmbushRisk
    {
        Low = 0,
        Moderate = 1,
        High = 2,
    }

    /// <summary>
    /// Ambushes on the road (<c>docs/design/parties-and-travel.md</c> §4, phase 3).
    ///
    /// <para><b>Rolled by the server when a travel order is accepted</b>, never by the client: an ambush
    /// is a fight that pays, so a client roll could be farmed or dodged. The roll is stored on the
    /// journey as <b>when</b> it strikes (a share of the journey's time), so re-reading the journey cannot
    /// re-roll it, and the client is not told until it has happened.</para>
    ///
    /// <para><b>A road skirmish is not a dungeon.</b> It is fought as the smallest run there is - a
    /// tier-1 clearing's waves, at the level of the land it happens in - and pays a share of that. It
    /// takes no site, brings no recruit and restores no resolve. It is shared so the client can show
    /// the risk before sending a company, and the size of the warband when one strikes.</para>
    /// </summary>
    public static class AmbushRules
    {
        /// <summary>The chance on a short road in tier-1 land the player holds.</summary>
        public const double BaseChance = 0.08;

        /// <summary>Added per tier above the first: deeper land is wilder.</summary>
        public const double ChancePerTier = 0.04;

        /// <summary>Land the player does not hold multiplies the chance: nobody keeps its roads.</summary>
        public const double UnheldLandFactor = 1.75;

        /// <summary>Added per minute the journey runs beyond the first: more road, more chances.</summary>
        public const double ChancePerMinute = 0.02;

        /// <summary>No journey is more likely than this to be ambushed.</summary>
        public const double MaximumChance = 0.35;

        /// <summary>The ambush strikes somewhere in this share of the journey's time - never at a doorstep.</summary>
        public const double EarliestStrike = 0.25;
        public const double LatestStrike = 0.75;

        /// <summary>An ambush is fought as a site of this tier: the fewest waves and no boss.</summary>
        public const int SkirmishTier = 1;

        /// <summary>The share of that run's experience, gold, items and materials a won ambush pays.</summary>
        public const double RewardShare = 0.5;

        /// <summary>Below these chances a road reads as low, then moderate, then high.</summary>
        public const double ModerateFrom = 0.12;
        public const double HighFrom = 0.22;

        /// <summary>
        /// The chance a journey is ambushed: by the tier of the land it ends in, whether the player
        /// holds that land, and how long it is.
        /// </summary>
        public static double ChanceFor(int regionTier, bool heldByPlayer, TimeSpan duration)
        {
            double chance = BaseChance + ChancePerTier * Math.Max(0, regionTier - 1);
            if (!heldByPlayer) chance *= UnheldLandFactor;
            chance += ChancePerMinute * Math.Max(0, duration.TotalMinutes - 1);
            return Math.Max(0, Math.Min(MaximumChance, chance));
        }

        public static AmbushRisk RiskOf(double chance) =>
            chance >= HighFrom ? AmbushRisk.High
            : chance >= ModerateFrom ? AmbushRisk.Moderate
            : AmbushRisk.Low;

        /// <summary>
        /// Rolls a journey: the share of its time at which it is ambushed, or null for a quiet road.
        /// <paramref name="random"/> is the caller's, so the server owns the entropy and tests can fix it.
        /// </summary>
        public static double? Roll(double chance, Random random)
        {
            if (random == null || random.NextDouble() >= chance) return null;
            return EarliestStrike + random.NextDouble() * (LatestStrike - EarliestStrike);
        }

        /// <summary>
        /// Where a journey stands at a share of its time, as a fractional index into its cells - the
        /// point at which an ambush halts it. The same walk <see cref="TravelRules.Progress"/> does.
        /// </summary>
        public static double IndexAt(IReadOnlyList<double> cumulativeSeconds, double timeShare)
        {
            if (cumulativeSeconds == null || cumulativeSeconds.Count == 0) return 0;
            double total = cumulativeSeconds[cumulativeSeconds.Count - 1];
            var start = DateTime.MinValue;
            return TravelRules.Progress(cumulativeSeconds, start, start.AddSeconds(total * timeShare));
        }

        /// <summary>
        /// The road back from where an ambush halted a company to where it set out: the cells walked so
        /// far, reversed, timed by the same seconds they took on the way out.
        /// </summary>
        public static (List<int[]> Cells, List<double> Seconds) RouteBack(
            IReadOnlyList<int[]> cells, IReadOnlyList<double> cumulativeSeconds, double haltedIndex)
        {
            var backCells = new List<int[]>();
            var backSeconds = new List<double>();
            if (cells == null || cumulativeSeconds == null || cells.Count == 0) return (backCells, backSeconds);

            int last = Math.Max(0, Math.Min(cells.Count - 1, (int)Math.Floor(haltedIndex)));
            double origin = cumulativeSeconds[last];
            for (int i = last; i >= 0; i--)
            {
                backCells.Add(cells[i]);
                backSeconds.Add(Math.Round(origin - cumulativeSeconds[i], 2));
            }

            // The first cell of an outbound route may itself sit a crossing's time after departure; the
            // walk back begins where the company stands, at zero.
            double shift = backSeconds[0];
            for (int i = 0; i < backSeconds.Count; i++) backSeconds[i] -= shift;
            return (backCells, backSeconds);
        }

        /// <summary>
        /// A share of a reward: rounded by chance rather than down, so half of one is sometimes one and
        /// sometimes none, and never always nothing.
        /// </summary>
        public static int Share(int amount, Random random)
        {
            double exact = amount * RewardShare;
            int whole = (int)Math.Floor(exact);
            if (random != null && random.NextDouble() < exact - whole) whole++;
            return whole;
        }
    }
}
