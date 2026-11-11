using System;
using System.Collections.Generic;
using System.Linq;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// Reclaiming your home region (spec "Reclaiming - Take back your home region", Mike 2026-11-10). A new seat
    /// starts <b>overrun</b>: the Hall stands, but a horde holds every other site in the region. Each is freed with a
    /// fight; when all are free, the <b>lair</b> opens, and killing its warlord reclaims the region.
    ///
    /// <para>While overrun (row 1): no company marches out, the region earns nothing and its sites can't be worked
    /// until freed, and nobody may raid, besiege or target it. Losing a horde fight costs nothing (row 6), and a freed
    /// site stays freed (row 7).</para>
    ///
    /// <para>Everything here is a pure function of the region: which sites are held, which is the lair and the levels
    /// come from its seed and its keep. Only <see cref="HordeState.Freed"/> and the rest are stored.</para>
    /// </summary>
    public static class HordeRules
    {
        /// <summary>Freeing a site is a pseudo-site run, <c>horde:{siteId}</c>, as a champion's is <c>bounty:{slot}</c>.</summary>
        public const string SitePrefix = "horde:";

        /// <summary>A held site's fight is the open-field waves of a camp; the lair's ends with its warlord.</summary>
        public const LocationType SiteType = LocationType.Camp;

        /// <summary>The ramp: the nearest held site is this many levels over the base, the furthest <see cref="FarLevelsAbove"/>.</summary>
        public const int NearLevelsAbove = 1;
        public const int FarLevelsAbove = 4;

        /// <summary>The lair's warlord stands this many levels over the base (row 4: about LV 6 for a new player).</summary>
        public const int LairLevelsAbove = 5;

        /// <summary>The reclaim reward (row 5): a Rare chest at the lair's level, and gold.</summary>
        public const long ReclaimGold = 500;

        public static string SiteId(string siteId) => SitePrefix + siteId;

        public static bool IsSiteId(string siteId) =>
            !string.IsNullOrEmpty(siteId) && siteId.StartsWith(SitePrefix, StringComparison.Ordinal);

        /// <summary>The real site a horde fight frees, or null when it is not a horde id.</summary>
        public static string TargetOf(string hordeSiteId) =>
            IsSiteId(hordeSiteId) ? hordeSiteId.Substring(SitePrefix.Length) : null;

        /// <summary>A fresh horde for a new seat. <paramref name="rosterLevel"/> is the player's level, 1 for a newcomer.</summary>
        public static HordeState NewHorde(int rosterLevel) =>
            new HordeState { BaseLevel = Math.Max(1, rosterLevel) };

        /// <summary>True while a horde holds the region: it has one, and its warlord still stands.</summary>
        public static bool IsOverrun(WorldRegionData region) =>
            region?.Horde != null && region.Horde.ReclaimedAtUtcTicks == 0;

        /// <summary>True once a horde was driven out of the region.</summary>
        public static bool IsReclaimed(WorldRegionData region) =>
            region?.Horde != null && region.Horde.ReclaimedAtUtcTicks != 0;

        /// <summary>Every site but the keep is the horde's to begin with.</summary>
        public static bool CanBeHeld(SiteSpec site) => site != null && site.Type != LocationType.Castle;

        /// <summary>True while the horde holds this site: the region is overrun and the site not yet freed.</summary>
        public static bool IsHeld(WorldRegionData region, SiteSpec site) =>
            IsOverrun(region) && CanBeHeld(site) && !region.Horde.Freed.Contains(site.SiteId);

        /// <summary>The sites a horde holds or held, nearest the keep first.</summary>
        public static List<SiteSpec> HeldSites(RegionLayout layout)
        {
            if (layout?.Sites == null) return new List<SiteSpec>();
            var keep = layout.Sites.FirstOrDefault(s => s.Type == LocationType.Castle);
            return layout.Sites.Where(CanBeHeld)
                .OrderBy(s => keep == null ? 0 : Distance(s.Cell, keep.Cell))
                .ThenBy(s => s.SiteId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// The lair: the camp furthest from the keep, else the furthest ruin, cave or other held site. Null when the
        /// region has no site to hold one.
        /// </summary>
        public static SiteSpec LairOf(RegionLayout layout)
        {
            var held = HeldSites(layout);
            if (held.Count == 0) return null;
            foreach (var kind in new[] { LocationType.Camp, LocationType.Ruin, LocationType.Dungeon })
            {
                var furthest = held.LastOrDefault(s => s.Type == kind);
                if (furthest != null) return furthest;
            }
            return held[held.Count - 1];
        }

        public static bool IsLair(RegionLayout layout, string siteId) =>
            !string.IsNullOrEmpty(siteId) && LairOf(layout)?.SiteId == siteId;

        /// <summary>The level of the horde at a held site: a ramp outward from the keep (row 4), the lair at its top.</summary>
        public static int LevelAt(WorldRegionData region, RegionLayout layout, string siteId)
        {
            int baseLevel = Math.Max(1, region?.Horde?.BaseLevel ?? 1);
            if (IsLair(layout, siteId)) return baseLevel + LairLevelsAbove;
            var ramp = HeldSites(layout).Where(s => !IsLair(layout, s.SiteId)).ToList();
            int index = ramp.FindIndex(s => s.SiteId == siteId);
            if (index < 0) return baseLevel;
            double t = ramp.Count <= 1 ? 0 : index / (double)(ramp.Count - 1);
            return baseLevel + (int)Math.Round(NearLevelsAbove + t * (FarLevelsAbove - NearLevelsAbove));
        }

        /// <summary>How many sites there are to free, the lair included.</summary>
        public static int Total(RegionLayout layout) => HeldSites(layout).Count;

        /// <summary>How many of them are free.</summary>
        public static int FreedCount(WorldRegionData region, RegionLayout layout)
        {
            if (region?.Horde == null) return 0;
            var ids = new HashSet<string>(HeldSites(layout).Select(s => s.SiteId), StringComparer.Ordinal);
            return region.Horde.Freed.Count(ids.Contains);
        }

        /// <summary>The held sites still standing between the player and the lair: every one but the lair, not yet freed.</summary>
        public static int LeftBeforeLair(WorldRegionData region, RegionLayout layout)
        {
            if (!IsOverrun(region)) return 0;
            return HeldSites(layout).Count(s => !IsLair(layout, s.SiteId) && !region.Horde.Freed.Contains(s.SiteId));
        }

        /// <summary>The lair opens once every other held site is free.</summary>
        public static bool LairOpen(WorldRegionData region, RegionLayout layout) =>
            IsOverrun(region) && LeftBeforeLair(region, layout) == 0;

        /// <summary>
        /// Why this site can't be freed now, or null when it can: the region must be overrun and yours, the site held,
        /// and the lair waits for the rest.
        /// </summary>
        public static string WhyNotFree(WorldRegionData region, RegionLayout layout, string siteId, string userId)
        {
            if (!IsOverrun(region)) return "No horde holds this land.";
            if (!region.IsOwnedByPlayer(userId)) return "Only its holder can take this land back.";
            var site = layout?.Sites?.FirstOrDefault(s => s.SiteId == siteId);
            if (site == null || !CanBeHeld(site)) return "The horde does not hold that.";
            if (region.Horde.Freed.Contains(siteId)) return "That is already free.";
            if (IsLair(layout, siteId))
            {
                int left = LeftBeforeLair(region, layout);
                if (left > 0) return $"The warlord waits until the rest of the land is free: {left} left.";
            }
            return null;
        }

        /// <summary>The words for a refusal to march out of overrun land, to attack it, or to work a held site.</summary>
        public const string RoadsHeld = "The roads out are held by the horde. Take back your land first.";
        public const string Protected = "That land is overrun, and its holder is taking it back. It can't be attacked yet.";
        public const string SiteHeld = "The horde holds it. Free it first.";

        private static int Distance(GridCell a, GridCell b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    }
}
