using System;
using System.Collections.Generic;
using System.Linq;
using Enums;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// Treasure maps (Active Content D; Mike 2026-10-08: "open to this, but get feedback on rarity and how much it's
    /// enjoyed"). A rare find from a dungeon or a portal points to a hidden cache somewhere in the realm, seen only by
    /// its holder. A company goes there and fights the cache's guardians, in a ruin's rooms; the win opens it: a Rare
    /// (sometimes Legendary) chest, twice a clear's gold, and now and then another map.
    ///
    /// <para><b>Everything a map says comes from its id</b>: which region (among those its holder could reach), which
    /// site it waits by, and so the riddle. The server stores the region it chose, so a map never moves.</para>
    /// </summary>
    public static class TreasureMapRules
    {
        /// <summary>A cache's run is a pseudo-site, <c>cache:{map id}</c>, as a champion's is <c>bounty:{slot}</c>.</summary>
        public const string SitePrefix = "cache:";

        /// <summary>The guardians are fought in a ruin's rooms (Rooms arena).</summary>
        public const LocationType SiteType = LocationType.Ruin;

        /// <summary>The chance a clear finds a map: a dungeon's boss 2%, a portal 0.5% (tune).</summary>
        public const double DungeonChance = 0.02;
        public const double PortalChance = 0.005;

        /// <summary>The chance an opened cache holds another map (tune).</summary>
        public const double ChainChance = 0.10;

        /// <summary>A player holds at most this many maps; a find beyond it is not made.</summary>
        public const int MostHeld = 3;

        /// <summary>The guardians stand this many levels above the site the cache waits by.</summary>
        public const int LevelsAbove = 1;

        /// <summary>The cache's gold, in clears' worth at its level and tier.</summary>
        public const double GoldInClears = 2.0;

        /// <summary>The chest is Rare, Legendary one time in five (tune).</summary>
        public const double LegendaryChance = 0.20;

        /// <summary>Caches lie this far, at most, from the holder's own land, so a company can reach them.</summary>
        public const int MostHexesFromHome = 3;

        public static string SiteId(Guid mapId) => SitePrefix + mapId.ToString("N");

        public static bool IsSiteId(string siteId) =>
            !string.IsNullOrEmpty(siteId) && siteId.StartsWith(SitePrefix, StringComparison.Ordinal);

        public static bool TryParseSiteId(string siteId, out Guid mapId)
        {
            mapId = Guid.Empty;
            return IsSiteId(siteId) && Guid.TryParseExact(siteId.Substring(SitePrefix.Length), "N", out mapId);
        }

        /// <summary>The chance a clear of this kind of site finds a map; none for the rest.</summary>
        public static double ChanceFrom(LocationType site) =>
            site == LocationType.Dungeon ? DungeonChance : site == LocationType.Portal ? PortalChance : 0;

        /// <summary>
        /// The regions a map may point into: not a capital, and, when the holder has land, within
        /// <see cref="MostHexesFromHome"/> of it. A player with no land may be sent anywhere that is not a capital.
        /// </summary>
        public static List<WorldRegionData> Eligible(IEnumerable<WorldRegionData> regions, string holderUserId)
        {
            var all = (regions ?? Enumerable.Empty<WorldRegionData>()).Where(r => r != null).ToList();
            var home = all.Where(r => !string.IsNullOrEmpty(holderUserId) && r.OwnerUserId == holderUserId).ToList();
            return all
                .Where(r => !r.IsCapital)
                .Where(r => home.Count == 0 || home.Any(h => HexCoord.Distance(h.Hex, r.Hex) <= MostHexesFromHome))
                .OrderBy(r => r.RegionId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>The region a map points into, seeded by its id; null when no region will do.</summary>
        public static WorldRegionData PickRegion(Guid mapId, IReadOnlyList<WorldRegionData> eligible)
        {
            if (eligible == null || eligible.Count == 0) return null;
            var random = DeterministicRandom.ForSubject(Naming.Hash($"map:{mapId:N}"), 0x6D6170);
            return eligible[random.Next(eligible.Count)];
        }

        /// <summary>
        /// The site the cache waits by, the landmark the riddle names: one of the region's fightable sites, seeded by the
        /// map. Null for a region with none.
        /// </summary>
        public static SiteSpec Landmark(Guid mapId, RegionLayout layout)
        {
            var sites = layout?.Sites?.Where(s => s.IsFightable).OrderBy(s => s.SiteId, StringComparer.Ordinal).ToList();
            if (sites == null || sites.Count == 0) return null;
            var random = DeterministicRandom.ForSubject(Naming.Hash($"map:{mapId:N}:mark"), 0x6D61726B);
            return sites[random.Next(sites.Count)];
        }

        /// <summary>The guardians' tier and level: the landmark's tier, a level above it.</summary>
        public static (int Tier, int Level) Terms(SiteSpec landmark) =>
            landmark == null ? (1, 1 + LevelsAbove) : (Math.Max(1, landmark.Tier), Math.Max(1, landmark.Level) + LevelsAbove);

        /// <summary>The cache's gold: <see cref="GoldInClears"/> times what the fight's clear would pay.</summary>
        public static long CacheGold(long clearGold) => (long)Math.Round(Math.Max(0, clearGold) * GoldInClears);

        public static ItemRarityTypes ChestRarity(Random random) =>
            random != null && random.NextDouble() < LegendaryChance ? ItemRarityTypes.Legendary : ItemRarityTypes.Rare;
    }
}
