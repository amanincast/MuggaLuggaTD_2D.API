using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>How a faction stands, in one word for the Powers panel.</summary>
    public enum FactionReadiness
    {
        /// <summary>Below <see cref="FactionStrengthRules.ActThreshold"/>: it will not march yet.</summary>
        Rebuilding = 0,
        Ready = 1,

        /// <summary>Lost an attack lately: no new attacks, half regeneration, a quarter weaker in defence.</summary>
        Bloodied = 2,

        /// <summary>A siege of its own is mustering (phase 3 of <c>docs/design/npc-factions.md</c>).</summary>
        Mustering = 3
    }

    /// <summary>
    /// A faction's manpower (<c>docs/design/npc-factions.md</c> §3; Mike, 2026-10-06).
    ///
    /// <para>Mike's protection for players, in place of a daily cap: a faction can only do what its
    /// strength pays for. Its <b>cap</b> is what its land supports, the same yardstick as a player's
    /// hold (<see cref="RegionHoldCalculator.HoldFloor"/> × entrenchment for each region it holds), so a
    /// faction that gains land grows and one that loses land shrinks. It refills to the cap over
    /// <see cref="RefillTime"/>, at half the rate while <see cref="BloodiedFor"/> after a lost attack.</para>
    ///
    /// <para>Nothing ticks: the server stores the strength and when it was settled, and asks this rule
    /// what it is now.</para>
    /// </summary>
    public static class FactionStrengthRules
    {
        /// <summary>From empty to full, when not Bloodied.</summary>
        public static readonly TimeSpan RefillTime = TimeSpan.FromHours(24);

        /// <summary>How long a lost attack leaves a faction Bloodied: the same window as captivity and muster.</summary>
        public static readonly TimeSpan BloodiedFor = TimeSpan.FromHours(8);

        /// <summary>A Bloodied faction refills at this share of its usual rate.</summary>
        public const double BloodiedRefill = 0.5;

        /// <summary>A faction marches nothing below this share of its cap.</summary>
        public const double ActThreshold = 0.6;

        /// <summary>A share of strength a raid marches with (phase 2).</summary>
        public const double RaidShare = 0.3;

        /// <summary>A share of strength a siege marches with (phase 3).</summary>
        public const double SiegeShare = 0.6;

        /// <summary>
        /// Strength a faction banks for each gold of ransom paid to it (Mike, 2026-10-06: "a test of
        /// scaling"). Never above its cap.
        /// </summary>
        public const double StrengthPerRansomGold = 1.0;

        /// <summary>Whether a region is held by a faction rather than by a player or nobody.</summary>
        public static bool IsFaction(FactionId faction) => faction == FactionId.Ashkin || faction == FactionId.Grimjaw;

        /// <summary>The factions there are, in the order the Powers panel lists them.</summary>
        public static readonly IReadOnlyList<FactionId> All = new[] { FactionId.Grimjaw, FactionId.Ashkin };

        /// <summary>Whether <paramref name="faction"/> holds <paramref name="region"/>.</summary>
        public static bool Holds(FactionId faction, WorldRegionData region) =>
            region != null && string.IsNullOrEmpty(region.OwnerUserId) && region.Faction == faction;

        /// <summary>What one region adds to its holder's cap: the walls and locals, entrenched.</summary>
        public static double CapOf(WorldRegionData region) =>
            RegionHoldCalculator.HoldFloor(region.Tier) * RegionHoldCalculator.EntrenchmentMultiplier(region.Entrenchment);

        /// <summary>The strength <paramref name="faction"/>'s land supports.</summary>
        public static double Cap(FactionId faction, IEnumerable<WorldRegionData> regions)
        {
            double cap = 0;
            if (regions == null) return cap;
            foreach (var region in regions)
            {
                if (Holds(faction, region)) cap += CapOf(region);
            }
            return Math.Round(cap);
        }

        /// <summary>How many regions <paramref name="faction"/> holds.</summary>
        public static int RegionsHeld(FactionId faction, IEnumerable<WorldRegionData> regions)
        {
            int held = 0;
            if (regions == null) return held;
            foreach (var region in regions)
            {
                if (Holds(faction, region)) held++;
            }
            return held;
        }

        /// <summary>Whether a faction is still Bloodied at <paramref name="utcNow"/>.</summary>
        public static bool IsBloodied(DateTime? bloodiedUntilUtc, DateTime utcNow) =>
            bloodiedUntilUtc.HasValue && utcNow < bloodiedUntilUtc.Value;

        /// <summary>
        /// Strength at <paramref name="utcNow"/>, given what it was at <paramref name="settledAtUtc"/>.
        /// It refills at cap ÷ <see cref="RefillTime"/> an hour, at <see cref="BloodiedRefill"/> of
        /// that for any part of the time it was Bloodied, and never stands above the cap: a faction
        /// that lost land is at once no stronger than what is left supports.
        /// </summary>
        public static double Settle(double strength, DateTime settledAtUtc, DateTime utcNow, double cap, DateTime? bloodiedUntilUtc)
        {
            if (cap <= 0) return 0;
            strength = Math.Max(0, Math.Min(strength, cap));
            if (utcNow <= settledAtUtc) return strength;

            double elapsed = (utcNow - settledAtUtc).TotalHours;
            double bloodied = 0;
            if (bloodiedUntilUtc.HasValue && bloodiedUntilUtc.Value > settledAtUtc)
                bloodied = Math.Min(elapsed, (bloodiedUntilUtc.Value - settledAtUtc).TotalHours);

            double perHour = cap / RefillTime.TotalHours;
            double gained = perHour * ((elapsed - bloodied) + BloodiedRefill * bloodied);
            return Math.Min(cap, strength + gained);
        }

        /// <summary>Strength as a share of the cap, 0..1.</summary>
        public static double Readiness(double strength, double cap) =>
            cap <= 0 ? 0 : Math.Max(0, Math.Min(1, strength / cap));

        /// <summary>The word for how a faction stands.</summary>
        public static FactionReadiness Word(double strength, double cap, bool bloodied, bool mustering = false)
        {
            if (mustering) return FactionReadiness.Mustering;
            if (bloodied) return FactionReadiness.Bloodied;
            return Readiness(strength, cap) >= ActThreshold ? FactionReadiness.Ready : FactionReadiness.Rebuilding;
        }

        /// <summary>Strength after a ransom of <paramref name="gold"/> is paid to the faction.</summary>
        public static double BankRansom(double strength, long gold, double cap) =>
            Math.Min(cap, Math.Max(0, strength) + Math.Max(0, gold) * StrengthPerRansomGold);
    }
}
