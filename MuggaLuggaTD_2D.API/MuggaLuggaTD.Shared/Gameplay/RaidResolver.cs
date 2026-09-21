using System;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>What a resolved raid did.</summary>
    public struct RaidResult
    {
        /// <summary>True when the raid landed. A repelled raid changes nothing but the cooldown.</summary>
        public bool AttackerWins;

        public int D20Roll;
        public int Modifier;
        public int Total;

        /// <summary>Resolve the region lost. Zero when the raid was repelled.</summary>
        public int ResolveDamage;

        /// <summary>The region's resolve after the raid.</summary>
        public int ResolveAfter;
    }

    /// <summary>
    /// Raiding a rival region (see <c>docs/design/siege.md</c> §3).
    ///
    /// <para>A raid is attrition, not conquest. Winning one <b>takes nothing</b> — it wears the
    /// region's resolve down, and resolve multiplies hold, so enough of them make the region cheap
    /// enough to besiege later. That is the whole point of the shape: the server cannot verify a
    /// real-time fight, so the design's answer is that <b>no single fight may be worth a region</b>.
    /// A forged raid win buys one cooldown's worth of progress and nothing else.</para>
    ///
    /// <para><b>The cooldown is the anti-cheat, not the fight.</b> Rate-limiting an attacker against
    /// a given region is what bounds what cheating can buy, which is why the cooldown applies whether
    /// the raid lands or not.</para>
    ///
    /// <para>Shared because the dossier has to show the player the bar they are being measured
    /// against, and the damage they can expect to do, under exactly the rules the server will apply.</para>
    /// </summary>
    public static class RaidResolver
    {
        /// <summary>
        /// The share of a region's hold an attacker must field to raid it — half the siege gate, as
        /// the design's "roughly half the gate, a lower bar than a siege".
        /// </summary>
        public const double RaidBarFraction = RegionHoldCalculator.SiegeGateFraction / 2.0;

        /// <summary>Resolve lost by a raid that barely clears the bar.</summary>
        public const int MinimumResolveDamage = 5;

        /// <summary>Resolve lost by a raid that arrives with the region's whole hold in hand.</summary>
        public const int MaximumResolveDamage = 15;

        /// <summary>Lowest resolve a raid can grind a region down to. Below this, raiding has done its work.</summary>
        public const int MinimumResolve = 0;

        /// <summary>Hours an attacker must wait before raiding the same region again.</summary>
        public const int CooldownHours = 4;

        public static TimeSpan Cooldown => TimeSpan.FromHours(CooldownHours);

        /// <summary>The power an attacker must bring before they may raid at all.</summary>
        public static long RaidBar(long hold)
        {
            return (long)Math.Round(hold * RaidBarFraction, MidpointRounding.AwayFromZero);
        }

        /// <summary>True when the marching party is strong enough to raid.</summary>
        public static bool ClearsBar(double marchingPower, long hold)
        {
            return marchingPower >= RaidBar(hold);
        }

        /// <summary>
        /// How much resolve a successful raid costs the region, scaled by how far past the bar the
        /// attacker arrived.
        ///
        /// <para>Linear from <see cref="MinimumResolveDamage"/> at the bar to
        /// <see cref="MaximumResolveDamage"/> for an attacker bringing the region's entire hold, and
        /// clamped at both ends. Bounded on purpose: an overwhelming attacker raids faster, but never
        /// so fast that one raid is worth a region.</para>
        /// </summary>
        public static int ResolveDamage(double marchingPower, long hold)
        {
            if (hold <= 0) return MaximumResolveDamage;

            double bar = RaidBarFraction;
            double ratio = marchingPower / hold;

            // Where the attacker sits between "just cleared the bar" and "brought the whole hold".
            double span = 1.0 - bar;
            double progress = span <= 0 ? 1.0 : (ratio - bar) / span;
            if (progress < 0) progress = 0;
            if (progress > 1) progress = 1;

            double damage = MinimumResolveDamage + ((MaximumResolveDamage - MinimumResolveDamage) * progress);
            return (int)Math.Round(damage, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Resolves a raid. <paramref name="d20Roll"/> is injected so the server owns the dice, as it
        /// does for every other contested outcome; the client renders what comes back.
        ///
        /// <para>The fight itself is settled by <see cref="PassivePvPResolver"/> — the same dice and
        /// the same power modifier a siege assault will use — so a raid is a real contest that can be
        /// repelled, rather than a guaranteed tick of damage for anyone who clears the bar.</para>
        /// </summary>
        public static RaidResult Resolve(double marchingPower, long hold, int resolveBefore, int d20Roll)
        {
            var fight = PassivePvPResolver.Resolve((float)marchingPower, hold, d20Roll);

            int damage = fight.AttackerWins ? ResolveDamage(marchingPower, hold) : 0;
            int after = resolveBefore - damage;
            if (after < MinimumResolve) after = MinimumResolve;

            return new RaidResult
            {
                AttackerWins = fight.AttackerWins,
                D20Roll = fight.D20Roll,
                Modifier = fight.Modifier,
                Total = fight.Total,
                // Report what was actually taken, which is less than the roll when resolve was
                // already near the floor.
                ResolveDamage = resolveBefore - after,
                ResolveAfter = after
            };
        }
    }
}
