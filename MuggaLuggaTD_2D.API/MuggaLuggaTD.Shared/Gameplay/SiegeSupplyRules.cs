using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// What an army eats (Hiring Hall phase 4; plan <c>docs/design/hiring-hall.md</c> in the Unity
    /// repo): declaring a siege costs goods, scaled by the target's hold (Mike, 2026-10-01: "scaled by
    /// the target's strength"). Shared so the siege codex quotes the bill the server will charge.
    ///
    /// <para>Scaled by <b>hold</b>, not tier, so both sides' play moves it: fortifying raises the
    /// attacker's bill as well as the walls, and raiding the resolve down lowers it. It is paid at
    /// declare, after every other check has passed (a refused declare costs nothing), and is spent
    /// whatever the siege comes to - the army has eaten the grain.</para>
    /// </summary>
    public static class SiegeSupplyRules
    {
        /// <summary>Goods per 1,000 hold: grain to eat, timber for engines, hides for tents, ore for arms (tune).</summary>
        public static readonly IReadOnlyList<(string Good, double PerThousandHold)> Rates = new[]
        {
            (ResourceNodeRules.Grain, 300.0),
            (ResourceNodeRules.Timber, 200.0),
            (ResourceNodeRules.Hides, 150.0),
            (ResourceNodeRules.Ore, 100.0)
        };

        /// <summary>No siege is cheaper than one against this much hold: a tier-1 region's floor.</summary>
        public const long MinimumHold = (long)RegionHoldCalculator.HoldFloorPerTier;

        /// <summary>The goods a siege against <paramref name="hold"/> costs, each rounded up to a ten.</summary>
        public static IReadOnlyList<(string Good, int Quantity)> CostFor(long hold)
        {
            double thousands = Math.Max(hold, MinimumHold) / 1000.0;
            var bill = new List<(string, int)>(Rates.Count);
            foreach (var (good, rate) in Rates)
                bill.Add((good, (int)(Math.Ceiling(rate * thousands / 10.0) * 10)));
            return bill;
        }
    }
}
