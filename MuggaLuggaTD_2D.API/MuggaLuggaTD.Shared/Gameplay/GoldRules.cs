using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// What gold is worth and where it comes from.
    ///
    /// <para><b>Gold is never a drop.</b> The player does not pick coins up off the floor; a cleared
    /// run pays a single figure at the end and land pays by the hour. That is a deliberate choice: a
    /// coin on the floor is a coin the client spawns, counts and reports, and every currency this
    /// game has learned to trust is one the client never touches. There is nothing to mint here
    /// because there is nothing in the scene to mint.</para>
    ///
    /// <para><b>Two sources, both derived from something the server already knows:</b> a clear pays a
    /// fraction of the experience that clear is worth, and a holding pays on the same weighting the
    /// season prices ground at. Neither needs the client to report anything.</para>
    /// </summary>
    public static class GoldRules
    {
        // -----------------------------------------------------------------
        // A cleared run
        // -----------------------------------------------------------------

        /// <summary>
        /// Gold per point of experience a clear is worth.
        ///
        /// <para>Pricing a run's gold off its experience rather than counting enemies again is what
        /// stops the two drifting. Experience is already walked wave by wave, enemy by enemy, with
        /// elites and the boss priced in (<see cref="RunRewardCalculator"/>), so it <i>is</i> the
        /// measure of how long and how hard the run was. A second walk over the same waves would be
        /// a second thing to keep in step for no extra expressiveness.</para>
        ///
        /// <para>This is therefore the one dial for run income. If deep sites should pay
        /// disproportionately, that belongs in the wave and tier tuning both rewards read — not in a
        /// gold-only exception that would make the two disagree about what a run was.</para>
        /// </summary>
        public const double GoldPerExperience = 0.05;

        /// <summary>What clearing a location pays, from the experience that clear is worth.</summary>
        public static long GoldForClear(long experience)
            => experience <= 0 ? 0 : (long)Math.Round(experience * GoldPerExperience);

        // -----------------------------------------------------------------
        // Land held
        // -----------------------------------------------------------------

        /// <summary>
        /// Gold an ordinary, unfortified tier-1 region pays its holder per hour.
        ///
        /// <para>Deliberately its own number rather than a multiple of
        /// <see cref="SeasonScoreRules.BaseRatePerHour"/>. Points are the win condition and gold is
        /// the economy; tying them together would mean a scoring rebalance silently repricing the
        /// Tavern, and an economy rebalance silently deciding who wins the season.</para>
        /// </summary>
        public const double BaseGoldPerHour = 6.0;

        /// <summary>
        /// What one region pays its holder per hour.
        ///
        /// <para>The <i>weighting</i> is shared with season scoring on purpose — how much better a
        /// deep, fortified, heartland region is than a shallow border one is a fact about the map,
        /// not about what is being paid out. Only the base rate differs.</para>
        /// </summary>
        public static double RateFor(WorldRegionData region)
        {
            if (region == null) return 0;

            int entrenchment = region.Entrenchment < 0 ? 0 : (region.Entrenchment > 5 ? 5 : region.Entrenchment);

            return BaseGoldPerHour
                   * SeasonScoreRules.TierMultiplier(region.Tier)
                   * (1.0 + (entrenchment * SeasonScoreRules.EntrenchmentStep))
                   * (SeasonScoreRules.IsHeartland(region) ? SeasonScoreRules.HeartlandMultiplier : 1.0);
        }

        /// <summary>What everything a player holds pays them per hour, together.</summary>
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

        /// <summary>
        /// Gold accrued by holding land at <paramref name="goldPerHour"/> between two instants.
        ///
        /// <para>Same shape as <see cref="SeasonScoreRules.Accrued"/>, and for the same reason:
        /// storing "settled + rate + when" means nothing has to tick, the arithmetic is identical
        /// whether the server was busy or idle, and a player offline for a week is paid exactly what
        /// they are owed.</para>
        /// </summary>
        public static double Accrued(double goldPerHour, DateTime from, DateTime to)
        {
            if (goldPerHour <= 0 || to <= from) return 0;
            return goldPerHour * (to - from).TotalHours;
        }
    }
}
