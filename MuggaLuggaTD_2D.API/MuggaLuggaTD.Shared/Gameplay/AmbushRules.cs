using System;
using System.Collections.Generic;
using System.Linq;

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
            => ChanceFor(regionTier, heldByPlayer, duration, patrolled: false);

        /// <summary>
        /// As above, and a region one of the player's companies patrols is safer still
        /// (<see cref="PatrolFactor"/>; <c>docs/design/auto-fight.md</c> §6). A second patrol adds nothing.
        /// </summary>
        public static double ChanceFor(int regionTier, bool heldByPlayer, TimeSpan duration, bool patrolled)
            => ChanceFor(regionTier, heldByPlayer, duration, patrolled, 1.0);

        /// <summary>
        /// As above, times <paramref name="factor"/>: what the hour does to the region's roads (Hunting
        /// Season, <see cref="RegionConditionRules.AmbushChanceFactor"/>). Still capped.
        /// </summary>
        public static double ChanceFor(int regionTier, bool heldByPlayer, TimeSpan duration, bool patrolled, double factor)
        {
            double chance = BaseChance + ChancePerTier * Math.Max(0, regionTier - 1);
            if (!heldByPlayer) chance *= UnheldLandFactor;
            chance += ChancePerMinute * Math.Max(0, duration.TotalMinutes - 1);
            if (patrolled) chance *= PatrolFactor;
            chance *= factor;
            return Math.Max(0, Math.Min(MaximumChance, chance));
        }

        /// <summary>What a patrolling company does to the chance of an ambush in its region.</summary>
        public const double PatrolFactor = 0.5;

        /// <summary>How often a patrol meets the mobs it keeps down: one skirmish in this much walking.</summary>
        public static readonly TimeSpan PatrolSkirmishEvery = TimeSpan.FromMinutes(20);

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
        /// The chance a journey across several regions is ambushed: each region's walk is a chance of
        /// its own (by its tier, whether it is held, and how long it takes there), and the road is
        /// quiet only if every one of them is. Still capped at <see cref="MaximumChance"/>.
        /// </summary>
        public static double ChanceForRoute(IEnumerable<(int Tier, bool Held, TimeSpan Walk)> legs)
            => ChanceForRoute(legs?.Select(l => (l.Tier, l.Held, l.Walk, false)));

        /// <summary>As above, with whether each region walked is patrolled by one of the player's companies.</summary>
        public static double ChanceForRoute(IEnumerable<(int Tier, bool Held, TimeSpan Walk, bool Patrolled)> legs)
            => ChanceForRoute(legs?.Select(l => (l.Tier, l.Held, l.Walk, l.Patrolled, 1.0)));

        /// <summary>As above, with each region's factor for the hour the road is rolled (Hunting Season).</summary>
        public static double ChanceForRoute(IEnumerable<(int Tier, bool Held, TimeSpan Walk, bool Patrolled, double Factor)> legs)
        {
            double quiet = 1;
            if (legs != null)
                foreach (var leg in legs) quiet *= 1 - ChanceFor(leg.Tier, leg.Held, leg.Walk, leg.Patrolled, leg.Factor);
            return Math.Min(MaximumChance, 1 - quiet);
        }

        /// <summary>
        /// The road back from where an ambush halted a company (<paramref name="haltedSeconds"/> after
        /// it set out) to where it set out: every cell walked so far, reversed, timed by the seconds
        /// they took on the way out - border crossings included - and starting at zero.
        /// </summary>
        public static List<RouteLeg> RouteBack(IReadOnlyList<RouteLeg> legs, double haltedSeconds)
        {
            var walked = new List<(string Region, int[] Cell, double At)>();
            if (legs != null)
                foreach (var leg in legs)
                    for (int i = 0; i < leg.Cells.Count && i < leg.Seconds.Count; i++)
                        if (leg.Seconds[i] <= haltedSeconds || walked.Count == 0)
                            walked.Add((leg.RegionId, leg.Cells[i], leg.Seconds[i]));

            var back = new List<RouteLeg>();
            if (walked.Count == 0) return back;

            double from = walked[walked.Count - 1].At;
            for (int i = walked.Count - 1; i >= 0; i--)
            {
                var point = walked[i];
                if (back.Count == 0 || back[back.Count - 1].RegionId != point.Region)
                    back.Add(new RouteLeg { RegionId = point.Region });
                var current = back[back.Count - 1];
                current.Cells.Add(point.Cell);
                current.Seconds.Add(Math.Round(from - point.At, 2));
            }
            return back;
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
