using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>What a player did to earn a lump of points.</summary>
    public enum SeasonDeed
    {
        /// <summary>Cleared a dungeon or portal, anywhere.</summary>
        SiteCleared = 0,

        /// <summary>Landed a raid on a rival region.</summary>
        RaidLanded = 1,

        /// <summary>Repelled a raid on your own region.</summary>
        RaidRepelled = 2,

        /// <summary>Took a rival region by siege.</summary>
        SiegeWon = 3,

        /// <summary>Held a region against a siege.</summary>
        SiegeRepelled = 4
    }

    /// <summary>
    /// How a season is scored (see <c>docs/design/seasons-and-scoring.md</c>).
    ///
    /// <para>A realm runs for a season of a length its creator chooses, and the player with the most
    /// points at the end wins. Points accrue continuously for ground you hold and in lumps for deeds
    /// you do — so the whole season matters rather than only its final day, and a player pushed back
    /// to their capital is behind rather than finished.</para>
    ///
    /// <para>Shared because the client has to be able to tell a player what a region is earning them
    /// and where they stand; the server alone decides what is actually banked.</para>
    ///
    /// <para><b>Every number here is a first pass.</b> No two people have played this game against
    /// each other, so the weights between holding, clearing and raiding are reasoned guesses rather
    /// than measurements.</para>
    /// </summary>
    public static class SeasonScoreRules
    {
        // -----------------------------------------------------------------
        // Held ground
        // -----------------------------------------------------------------

        /// <summary>Points an ordinary, unfortified tier-1 region earns its holder per hour.</summary>
        public const double BaseRatePerHour = 10.0;

        /// <summary>
        /// Added to a region's multiplier per point of entrenchment.
        ///
        /// <para>Development is in the rate on purpose: it makes tending a region a way to score
        /// rather than only a way to keep it, which gives repairing ruins a second reason to exist.
        /// At the cap of 5 a fully entrenched region earns half again what a bare one does.</para>
        /// </summary>
        public const double EntrenchmentStep = 0.10;

        /// <summary>
        /// What the seven central regions are worth, against ordinary ground.
        ///
        /// <para>The map already reserves them: players are seated in a band at distance 2-4, so the
        /// centre is neutral land everyone borders and nobody starts in. Paying a premium for it
        /// turns an unused feature of the map into the thing worth fighting over — without making
        /// holding it an instant win, which would hand the realm to whoever has one good day.</para>
        /// </summary>
        public const double HeartlandMultiplier = 4.0;

        /// <summary>How far from the centre the heartland reaches. 0-1 is the centre and its six neighbours.</summary>
        public const int HeartlandRadius = 1;

        /// <summary>True for the regions at the heart of the map, which score at a premium.</summary>
        public static bool IsHeartland(WorldRegionData region)
        {
            if (region == null) return false;
            return HexCoord.Distance(region.Hex, new HexCoord(0, 0)) <= HeartlandRadius;
        }

        /// <summary>Deeper land is worth more, on the same 1-4 scale the map is generated against.</summary>
        public static double TierMultiplier(int tier)
        {
            if (tier < 1) tier = 1;
            if (tier > 4) tier = 4;
            return 1.0 + ((tier - 1) * 0.5); // 1.0, 1.5, 2.0, 2.5
        }

        /// <summary>What one region earns its holder per hour.</summary>
        public static double RateFor(WorldRegionData region)
        {
            if (region == null) return 0;

            int entrenchment = region.Entrenchment < 0 ? 0 : (region.Entrenchment > 5 ? 5 : region.Entrenchment);

            return BaseRatePerHour
                   * TierMultiplier(region.Tier)
                   * (1.0 + (entrenchment * EntrenchmentStep))
                   * (IsHeartland(region) ? HeartlandMultiplier : 1.0);
        }

        /// <summary>What everything a player holds earns them per hour, together.</summary>
        public static double RateForHoldings(string userId, IEnumerable<WorldRegionData> allRegions)
        {
            if (string.IsNullOrEmpty(userId) || allRegions == null) return 0;

            double total = 0;
            foreach (var region in allRegions)
            {
                if (region != null && region.IsOwnedByPlayer(userId))
                    total += RateFor(region);
            }

            return total;
        }

        // -----------------------------------------------------------------
        // Deeds
        // -----------------------------------------------------------------

        /// <summary>
        /// What a deed pays.
        ///
        /// <para>Clearing a site is the bounce-back lever: it is the game's actual combat, it does
        /// not require beating another player, and someone with almost no territory can still earn
        /// it. Raiding pays because contesting should beat sitting still, and repelling pays because
        /// <c>siege.md</c> §6 asks that successful defence be worth something.</para>
        /// </summary>
        public static double PointsFor(SeasonDeed deed)
        {
            switch (deed)
            {
                case SeasonDeed.SiteCleared: return 40.0;
                case SeasonDeed.RaidLanded: return 60.0;
                case SeasonDeed.RaidRepelled: return 60.0;

                // A siege is days of raiding and a locked army. Paying for it on top of the region's
                // income is what makes contesting worth more than sitting still (siege.md §7a).
                // Roughly thirty hours of an ordinary region, and a repel is worth half.
                case SeasonDeed.SiegeWon: return 300.0;
                case SeasonDeed.SiegeRepelled: return 150.0;
                default: return 0;
            }
        }

        // -----------------------------------------------------------------
        // Accrual
        // -----------------------------------------------------------------

        /// <summary>
        /// Points earned by holding ground between two moments at a fixed rate.
        ///
        /// <para>This is the whole of the accrual: nothing ticks, and a score is settled up only when
        /// something changes what a player holds. The arithmetic is identical whether the server was
        /// busy or idle, and whether anyone was online.</para>
        /// </summary>
        public static double Accrued(double pointsPerHour, DateTime from, DateTime to)
        {
            if (pointsPerHour <= 0 || to <= from) return 0;

            return pointsPerHour * (to - from).TotalHours;
        }

        /// <summary>
        /// The moment accrual should be measured to: now, or the end of the season if it has already
        /// passed. Clamping here is what lets the final standings be computed lazily — the first
        /// person to look after the closing time sees what anybody else would have.
        /// </summary>
        public static DateTime AccrueUntil(DateTime utcNow, DateTime seasonEndsAt)
        {
            return utcNow < seasonEndsAt ? utcNow : seasonEndsAt;
        }
    }
}
