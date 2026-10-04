using System;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// Trouble at the diggings (auto-fight.md §7, phase 5; Mike 2026-10-04): now and then raiders harry
    /// the workers of a region nobody patrols, and for that hour they gather at half pace. One of the
    /// holder's companies patrolling the region keeps them off.
    ///
    /// <para>Nothing is stored or rolled at random: an hour is harried or not as a function of the realm,
    /// the region and the hour, so the server's settle and the client's "harried now" always agree.</para>
    /// </summary>
    public static class HarassmentRules
    {
        /// <summary>How many hours in ten thousand an unpatrolled region's diggings are harried (10%, tune).</summary>
        public const int ChanceOutOfTenThousand = 1000;

        /// <summary>What a harried hour's work is worth: half (tune).</summary>
        public const double HarriedFactor = 0.5;

        /// <summary>The hour <paramref name="at"/> falls in, counted from the epoch as the Lucky trait's are.</summary>
        public static long HourOf(DateTime at) => at.Ticks / TimeSpan.TicksPerHour;

        /// <summary>Whether raiders come to the region's diggings in that hour, if nobody patrols it.</summary>
        public static bool IsHarried(string realmId, string regionId, long hourIndex)
        {
            if (string.IsNullOrEmpty(regionId)) return false;
            ulong roll = Naming.Hash($"{(realmId ?? "").ToLowerInvariant()}:{regionId}:{hourIndex}:raiders") % 10000UL;
            return roll < ChanceOutOfTenThousand;
        }

        /// <summary>Whether the region's workers are harried at <paramref name="at"/>: never while it is patrolled.</summary>
        public static bool IsHarriedAt(string realmId, string regionId, DateTime at, bool patrolled)
            => !patrolled && IsHarried(realmId, regionId, HourOf(at));

        /// <summary>When the harried hour holding <paramref name="at"/> ends.</summary>
        public static DateTime HourEnds(DateTime at)
            => new DateTime((HourOf(at) + 1) * TimeSpan.TicksPerHour, DateTimeKind.Utc);
    }
}
