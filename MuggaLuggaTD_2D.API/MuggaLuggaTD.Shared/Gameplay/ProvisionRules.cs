using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// What auto mode eats (<c>docs/design/auto-fight.md</c> §5; Mike, 2026-10-04: "that gives our
    /// farming resources a true reason to exist").
    ///
    /// <para>Provisions are goods from the player's wallet, gathered by Hiring Hall workers. Each fight
    /// takes Grain, and a dungeon takes Hides as well for its kit; a patrol eats Grain by the hour.
    /// Ore, Timber and Stone stay for Fortify and siege supplies. <b>Provisions are the only limit on
    /// auto mode</b>: there is no cap on how many companies may use it. When the Grain runs out the
    /// order stops, rather than eating goods saved for a siege.</para>
    /// </summary>
    public static class ProvisionRules
    {
        public const int GrainPerFight = 2;
        public const int HidesPerDungeon = 1;
        public const int PatrolGrainPerHour = 3;

        /// <summary>The goods one auto-fight at a site of this kind takes.</summary>
        public static IReadOnlyList<(string Good, int Quantity)> CostOfFight(LocationType type)
        {
            var cost = new List<(string, int)> { (ResourceNodeRules.Grain, GrainPerFight) };
            if (type == LocationType.Dungeon) cost.Add((ResourceNodeRules.Hides, HidesPerDungeon));
            return cost;
        }

        /// <summary>
        /// The Grain a patrol has eaten after <paramref name="patrolled"/>, in whole units. The server
        /// charges the difference since it last settled, so the fraction carries over.
        /// </summary>
        public static int PatrolGrainEaten(TimeSpan patrolled) =>
            patrolled <= TimeSpan.Zero ? 0 : (int)Math.Floor(patrolled.TotalHours * PatrolGrainPerHour);

        /// <summary>Whether a wallet holding <paramref name="held"/> can pay <paramref name="cost"/>.</summary>
        public static bool CanAfford(IReadOnlyDictionary<string, long> held, IEnumerable<(string Good, int Quantity)> cost)
        {
            foreach (var (good, quantity) in cost)
                if (held == null || !held.TryGetValue(good, out long have) || have < quantity) return false;
            return true;
        }
    }
}
