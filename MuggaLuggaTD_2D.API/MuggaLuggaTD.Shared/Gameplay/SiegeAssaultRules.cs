using System;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>The fight an assault is, as the server hands it to the attacker's client.</summary>
    public struct SiegeEncounter
    {
        /// <summary>Level the defenders spawn at.</summary>
        public int EnemyLevel;

        /// <summary>Waves the attacker must survive to break the defence.</summary>
        public int Waves;

        /// <summary>Champions stationed in the region, who stand with the defence as elites.</summary>
        public int EliteCount;
    }

    /// <summary>
    /// How hard an assault is, and what winning or losing it does (<c>docs/design/siege.md</c> §5-6).
    ///
    /// <para><b>The server hands the client the encounter.</b> The client may still cheat inside the
    /// fight, as it can in any PvE run, but it cannot ask for an easier one: the difficulty is set
    /// from the region's hold as frozen when muster closed, against the army that was locked when the
    /// siege was declared. Barely clearing the gate is brutal; massively outclassing the defence is a
    /// walkover. Power sets the envelope, and the player's skill decides the outcome inside it.</para>
    ///
    /// <para>What stops a forged win from being worth a region is everything before it: days of
    /// visible raiding, a declared siege, a muster the defender could use, and a single assault per
    /// siege.</para>
    /// </summary>
    public static class SiegeAssaultRules
    {
        /// <summary>Waves when the army only just clears the gate.</summary>
        public const int MaximumWaves = 8;

        /// <summary>Waves when the army brings twice the region's hold or more.</summary>
        public const int MinimumWaves = 3;

        /// <summary>
        /// The march-to-hold ratio at which the assault is as easy as it gets. The gate is 0.6, so
        /// the difficulty is spread across everything from "just enough" to "more than twice".
        /// </summary>
        public const double OverwhelmingRatio = 2.0;

        /// <summary>
        /// Enemy level, as a share of the attacking champions' own average level, at the gate and at
        /// the overwhelming end. An evenly matched assault is fought against equals.
        /// </summary>
        public const double EnemyLevelShareAtGate = 1.0;
        public const double EnemyLevelShareOverwhelming = 0.4;

        /// <summary>
        /// Resolve a region is left with when it falls. It changes hands wrecked, so the new owner
        /// inherits something that needs tending - and, once the truce lifts, something the old owner
        /// can take back without a week of raiding.
        /// </summary>
        public const int WreckedResolve = 30;

        /// <summary>Resolve a region gains for holding against a siege. Successful defence should pay.</summary>
        public const int RepelResolveBonus = 15;

        /// <summary>
        /// How long after the assault window closes a started assault may still be claimed. A fight
        /// begun a minute before the window shut should not be forfeited by the clock.
        /// </summary>
        public const int ClaimGraceHours = 2;

        public static TimeSpan ClaimGrace => TimeSpan.FromHours(ClaimGraceHours);

        /// <summary>
        /// How far the army clears the gate, from 0 (exactly at it, or below) to 1 (overwhelming).
        /// </summary>
        public static double Advantage(double marchingPower, long frozenHold)
        {
            if (frozenHold <= 0) return 1.0;

            double ratio = marchingPower / frozenHold;
            double span = OverwhelmingRatio - RegionHoldCalculator.SiegeGateFraction;
            double advantage = (ratio - RegionHoldCalculator.SiegeGateFraction) / span;

            return advantage < 0 ? 0 : (advantage > 1 ? 1 : advantage);
        }

        /// <summary>
        /// The encounter for an assault. <paramref name="armySize"/> is how many champions march, so
        /// the enemies can be pitched against their average level rather than their sum.
        /// </summary>
        public static SiegeEncounter EncounterFor(double marchingPower, long frozenHold, int armySize, int garrisonCount)
        {
            double advantage = Advantage(marchingPower, frozenHold);

            int waves = (int)Math.Round(
                MaximumWaves - ((MaximumWaves - MinimumWaves) * advantage), MidpointRounding.AwayFromZero);

            double averageLevel = marchingPower / (PartyPowerCalculator.POWER_PER_LEVEL * Math.Max(1, armySize));
            double share = EnemyLevelShareAtGate - ((EnemyLevelShareAtGate - EnemyLevelShareOverwhelming) * advantage);
            int enemyLevel = Math.Max(1, (int)Math.Round(averageLevel * share, MidpointRounding.AwayFromZero));

            return new SiegeEncounter
            {
                EnemyLevel = enemyLevel,
                Waves = waves,
                EliteCount = Math.Max(0, garrisonCount)
            };
        }
    }
}
