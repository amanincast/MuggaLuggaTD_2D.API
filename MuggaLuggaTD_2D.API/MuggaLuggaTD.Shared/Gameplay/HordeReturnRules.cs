using System;
using System.Collections.Generic;
using System.Linq;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// The horde returns (Reclaiming phase 3, spec §6, Mike 2026-11-16). Land you can't tend, you lose:
    /// <code>
    /// CAUSE stands 12h ──▶ GATHERS (8h) ──▶ the garrison's roll ──▶ holds: scattered, safe 24h
    ///   unruly / invaded    fix the cause: scattered                fails: FALLS, overrun but still yours
    ///                                                                        not reclaimed in 3 days: back to the wild
    /// </code>
    /// <list type="bullet">
    /// <item><b>Causes</b> (row P3-1), nothing random: land claimed from the wild with no steward for 12 hours, or land a
    /// Lesser rift invaded and nobody sealed for 12 hours. Never the seat (P3-2).</item>
    /// <item><b>A gathering</b> (P3-3): eight hours' warning, a faction's muster. Appointing a steward or sealing the rift
    /// scatters it.</item>
    /// <item><b>The roll</b> (P3-5, P3-10): the region's hold, a patrol's power added, against the horde's strength by
    /// tier, with the dice a faction's siege uses.</item>
    /// <item><b>The fall</b> (P3-6): the land stays its holder's but overrun by a horde of its tier, which only they take
    /// back; after three days it lapses to the wild.</item>
    /// <item><b>Limits</b> (P3-9): one gathering per player, a region that held is safe a day, none in the season's last day.</item>
    /// </list>
    /// Nothing ticks: the server's sweep reads these over the world and acts.
    /// </summary>
    public static class HordeReturnRules
    {
        /// <summary>How long a cause must stand before a horde gathers (row P3-1).</summary>
        public static readonly TimeSpan CauseLasts = TimeSpan.FromHours(12);

        /// <summary>How long a horde gathers before it strikes (row P3-3): a faction's muster.</summary>
        public static TimeSpan Gathers => FactionSiegeRules.Muster;

        /// <summary>A region that turned a horde away is safe this long (row P3-9).</summary>
        public static readonly TimeSpan SafeFor = TimeSpan.FromHours(24);

        /// <summary>Fallen land not reclaimed in this long goes back to the wild (row P3-6).</summary>
        public static readonly TimeSpan LapsesAfter = TimeSpan.FromDays(3);

        /// <summary>
        /// The horde's strength per tier at the roll (row P3-5). Against <see cref="RegionHoldCalculator.HoldFloorPerTier"/>
        /// (500): land with nobody standing in it falls about 19 times in 20, and a garrison worth about twice the horde
        /// turns it away about as often. A first pass (tune).
        /// </summary>
        public const double StrengthPerTier = 1200;

        public static bool IsGathering(WorldRegionData region) => region?.Gathering != null;

        public static DateTime StrikesAt(WorldRegionData region) =>
            new DateTime(region?.Gathering?.StrikesAtUtcTicks ?? 0, DateTimeKind.Utc);

        /// <summary>
        /// Land a horde may come back to: a player's, not their seat (row P3-2), and not overrun already. The seat's ring
        /// needs no steward, so only an invasion brings a horde there.
        /// </summary>
        public static bool CanBeStruck(WorldRegionData region) =>
            region != null && region.Ownership == LocationOwnership.Player && !string.IsNullOrEmpty(region.OwnerUserId)
            && !region.IsCapital && !HordeRules.IsOverrun(region);

        /// <summary>
        /// The unruly stamp a region should carry now: when it became unruly, kept while it stays so, and 0 once it is
        /// not. The sweep writes it when it differs.
        /// </summary>
        public static long UnrulyStamp(WorldRegionData region, DateTime now)
        {
            if (!StewardRules.IsUnruly(region)) return 0;
            return region.UnrulySinceUtcTicks > 0 ? region.UnrulySinceUtcTicks : now.Ticks;
        }

        /// <summary>
        /// The cause that has stood long enough to bring a horde, and since when: the older of the two when both do.
        /// <paramref name="invadedAt"/> is when a Lesser rift still open in the region invaded it, or null.
        /// </summary>
        public static (HordeCause Cause, DateTime Since) CauseOf(WorldRegionData region, DateTime now, DateTime? invadedAt)
        {
            var none = (HordeCause.None, DateTime.MaxValue);
            if (!CanBeStruck(region)) return none;

            var found = none;
            if (StewardRules.IsUnruly(region) && region.UnrulySinceUtcTicks > 0)
            {
                var since = new DateTime(region.UnrulySinceUtcTicks, DateTimeKind.Utc);
                if (now - since >= CauseLasts) found = (HordeCause.Unruly, since);
            }
            if (invadedAt.HasValue && now - invadedAt.Value >= CauseLasts && invadedAt.Value < found.Item2)
                found = (HordeCause.Invaded, invadedAt.Value);
            return found;
        }

        /// <summary>Whether a gathering's cause still stands; fixed, it scatters (row P3-3). <paramref name="invaded"/>: a rift still invades.</summary>
        public static bool CauseStands(WorldRegionData region, bool invaded)
        {
            if (region?.Gathering == null) return false;
            return region.Gathering.Cause == HordeCause.Unruly ? StewardRules.IsUnruly(region) : invaded;
        }

        /// <summary>Whether a gathering should be called off without a fight: the land changed hands, or something else overran it.</summary>
        public static bool CalledOff(WorldRegionData region) =>
            region?.Gathering == null || !CanBeStruck(region) || !region.IsOwnedByPlayer(region.Gathering.UserId);

        /// <summary>Whether a region turned a horde away lately and is still safe (row P3-9).</summary>
        public static bool IsSafe(WorldRegionData region, DateTime now) => region != null && region.HordeSafeUntilUtcTicks > now.Ticks;

        /// <summary>
        /// Where a horde starts to gather now (rows P3-1, P3-9): for each player with no gathering already, the region of
        /// theirs whose cause has stood longest, if any. None in the season's last day. <paramref name="invadedAt"/> maps a
        /// region to when the Lesser rift still open in it invaded.
        /// </summary>
        public static List<(WorldRegionData Region, HordeCause Cause)> ToGather(IReadOnlyCollection<WorldRegionData> regions,
            DateTime now, IReadOnlyDictionary<string, DateTime> invadedAt, bool seasonClosing)
        {
            var result = new List<(WorldRegionData, HordeCause)>();
            if (regions == null || seasonClosing) return result;

            var busy = new HashSet<string>(regions.Where(IsGathering).Select(r => r.Gathering.UserId ?? r.OwnerUserId), StringComparer.Ordinal);
            var picks = new Dictionary<string, (WorldRegionData Region, HordeCause Cause, DateTime Since)>(StringComparer.Ordinal);
            foreach (var region in regions.OrderBy(r => r.RegionId, StringComparer.Ordinal))
            {
                if (region == null || IsGathering(region) || IsSafe(region, now)) continue;
                if (busy.Contains(region.OwnerUserId ?? string.Empty)) continue;
                DateTime? invaded = invadedAt != null && invadedAt.TryGetValue(region.RegionId, out var at) ? at : (DateTime?)null;
                var (cause, since) = CauseOf(region, now, invaded);
                if (cause == HordeCause.None) continue;
                if (picks.TryGetValue(region.OwnerUserId, out var held) && held.Since <= since) continue;
                picks[region.OwnerUserId] = (region, cause, since);
            }
            foreach (var pick in picks.Values.OrderBy(p => p.Region.RegionId, StringComparer.Ordinal))
                result.Add((pick.Region, pick.Cause));
            return result;
        }

        /// <summary>A new gathering, from now.</summary>
        public static HordeGathering NewGathering(string userId, HordeCause cause, DateTime now) => new HordeGathering
        {
            UserId = userId,
            Cause = cause,
            StartedAtUtcTicks = now.Ticks,
            StrikesAtUtcTicks = (now + Gathers).Ticks,
        };

        /// <summary>The horde's strength at the roll, by the region's tier (row P3-5).</summary>
        public static double Strength(int tier) => StrengthPerTier * Math.Max(1, Math.Min(4, tier));

        /// <summary>
        /// The region's hold against the horde (rows P3-5, P3-10): its garrison and steward as a siege reads them, with
        /// the power of any company patrolling there added to the garrison.
        /// </summary>
        public static long Hold(WorldRegionData region, IReadOnlyCollection<WorldRegionData> regions, double patrolPower)
        {
            if (region == null) return 0;
            double garrison = RegionHoldCalculator.GarrisonPowerOf(region) + Math.Max(0, patrolPower);
            return RegionHoldCalculator.Hold(garrison, region.Tier, region.Entrenchment,
                SupplyCalculator.SupplyFor(region, regions), region.Resolve);
        }

        /// <summary>The roll when the gathering's time is up, with the server's d20: the dice a faction's siege uses.</summary>
        public static FactionSiegeResult Settle(double strength, long hold, int d20Roll) =>
            FactionSiegeRules.Settle(strength, hold, d20Roll);

        /// <summary>When fallen land goes back to the wild.</summary>
        public static DateTime LapsesAt(WorldRegionData region) =>
            new DateTime(region?.Horde?.FellAtUtcTicks ?? 0, DateTimeKind.Utc) + LapsesAfter;

        /// <summary>Fallen land still overrun three days on goes back to the wild (row P3-6).</summary>
        public static bool ShouldLapse(WorldRegionData region, DateTime now) =>
            HordeRules.IsReturned(region) && HordeRules.IsOverrun(region) && region.Horde.FellAtUtcTicks > 0 && LapsesAt(region) <= now;

        /// <summary>Why it came, for the dossier and the letters: "for want of a steward".</summary>
        public static string WhyItCame(HordeCause cause) =>
            cause == HordeCause.Invaded ? "out of an open rift" : "for want of a steward";

        /// <summary>How to scatter it: what the player can do from the map.</summary>
        public static string HowToScatter(HordeCause cause) =>
            cause == HordeCause.Invaded ? "Seal the rift and it scatters." : "Station a steward at the keep and it scatters.";
    }
}
