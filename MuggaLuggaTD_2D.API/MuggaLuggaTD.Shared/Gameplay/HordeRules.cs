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

        /// <summary>
        /// A player who can't win (spec §4, Mike 2026-11-12: "a good idea if the player can't beat it"): from the
        /// <see cref="LairEasesAfter"/>th loss at the lair, the warlord stands a level lower for each further loss, at
        /// most <see cref="LairEaseMost"/> lower.
        /// </summary>
        public const int LairEasesAfter = 3, LairEaseMost = 3;

        /// <summary>
        /// How many levels the warlord has eased. <paramref name="nextFight"/>: for the fight not yet begun (the card),
        /// whose losses are every try so far; otherwise for the fight under way, whose own try is not a loss.
        /// </summary>
        public static int LairEase(WorldRegionData region, bool nextFight = false, string userId = null)
        {
            // A wild warlord eases for each player separately (row P2-5): it is shared land.
            int tries = Math.Max(0, IsWild(region) ? StakeOf(region, userId)?.LairTries ?? 0 : region?.Horde?.LairTries ?? 0);
            int losses = nextFight ? tries : Math.Max(0, tries - 1);
            return Math.Min(LairEaseMost, Math.Max(0, losses - (LairEasesAfter - 1)));
        }

        /// <summary>The reclaim reward (row 5): a Rare chest at the lair's level, and gold.</summary>
        public const long ReclaimGold = 500;

        /// <summary>
        /// The reclaim reward's free worker (row 5): a Skilled local, at work at the first resource site freed (phase 1c).
        /// </summary>
        public const WorkerTier ReclaimWorkerTier = WorkerTier.Skilled;

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
        public static SiteSpec LairOf(RegionLayout layout, bool wild = false)
        {
            var held = HeldSites(layout);
            if (held.Count == 0) return null;
            // A wild warlord sits in the region's strongest site (row P2-2): its dungeon, else a ruin, else a camp.
            var kinds = wild
                ? new[] { LocationType.Dungeon, LocationType.Ruin, LocationType.Camp }
                : new[] { LocationType.Camp, LocationType.Ruin, LocationType.Dungeon };
            foreach (var kind in kinds)
            {
                var furthest = held.LastOrDefault(s => s.Type == kind);
                if (furthest != null) return furthest;
            }
            return held[held.Count - 1];
        }

        public static bool IsLair(RegionLayout layout, string siteId, bool wild = false) =>
            !string.IsNullOrEmpty(siteId) && LairOf(layout, wild)?.SiteId == siteId;

        /// <summary>The region's lair: a seat's or a wild horde's.</summary>
        public static SiteSpec LairOf(WorldRegionData region, RegionLayout layout) => LairOf(layout, IsWild(region));

        public static bool IsLair(WorldRegionData region, RegionLayout layout, string siteId) => IsLair(layout, siteId, IsWild(region));

        /// <summary>
        /// The level of the horde at a held site: a ramp outward from the keep (row 4), the lair at its top. A wild
        /// warlord eases for each player (<paramref name="userId"/>) separately.
        /// </summary>
        public static int LevelAt(WorldRegionData region, RegionLayout layout, string siteId, bool nextFight = false, string userId = null)
        {
            int baseLevel = Math.Max(1, region?.Horde?.BaseLevel ?? 1);
            bool wild = IsWild(region);
            if (IsLair(layout, siteId, wild)) return baseLevel + LairLevelsAbove - LairEase(region, nextFight, userId);
            var ramp = HeldSites(layout).Where(s => !IsLair(layout, s.SiteId, wild)).ToList();
            int index = ramp.FindIndex(s => s.SiteId == siteId);
            if (index < 0) return baseLevel;
            double t = ramp.Count <= 1 ? 0 : index / (double)(ramp.Count - 1);
            return baseLevel + (int)Math.Round(NearLevelsAbove + t * (FarLevelsAbove - NearLevelsAbove));
        }

        /// <summary>How much there is to take back: every held site (the lair included) and every pack.</summary>
        public static int Total(RegionLayout layout, int seed) => HeldSites(layout).Count + Packs(layout, seed, 1).Count;

        /// <summary>
        /// How much there is to take back in this region. A wild horde's sites open together when its warlord falls
        /// (row P2-2), so its count is its warbands and the lair.
        /// </summary>
        public static int Total(WorldRegionData region, RegionLayout layout) =>
            IsWild(region) ? PacksOf(region, layout).Count + (LairOf(region, layout) != null ? 1 : 0) : Total(layout, region?.Seed ?? 0);

        /// <summary>How much of it is taken back: sites freed and packs broken.</summary>
        public static int FreedCount(WorldRegionData region, RegionLayout layout)
        {
            if (region?.Horde == null) return 0;
            var sites = new HashSet<string>(HeldSites(layout).Select(s => s.SiteId), StringComparer.Ordinal);
            var packs = new HashSet<string>(PacksOf(region, layout).Select(p => p.Id), StringComparer.Ordinal);
            return region.Horde.Freed.Count(sites.Contains) + region.Horde.Broken.Count(packs.Contains);
        }

        /// <summary>What still stands between the player and the lair: every held site but the lair, and every pack.</summary>
        public static int LeftBeforeLair(WorldRegionData region, RegionLayout layout)
        {
            if (!IsOverrun(region)) return 0;
            int packs = PacksOf(region, layout).Count(p => !region.Horde.Broken.Contains(p.Id));
            // A wild warlord waits for its warbands only: the region's sites open with its fall (row P2-2).
            if (IsWild(region)) return packs;
            return HeldSites(layout).Count(s => !IsLair(layout, s.SiteId) && !region.Horde.Freed.Contains(s.SiteId)) + packs;
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
            bool wild = IsWild(region);
            if (!wild && !region.IsOwnedByPlayer(userId)) return "Only its holder can take this land back.";
            var site = layout?.Sites?.FirstOrDefault(s => s.SiteId == siteId);
            if (site == null || !CanBeHeld(site)) return "The horde does not hold that.";
            if (region.Horde.Freed.Contains(siteId)) return "That is already free.";
            if (IsLair(layout, siteId, wild))
            {
                int left = LeftBeforeLair(region, layout);
                if (left > 0)
                    return wild
                        ? $"The warlord waits until its warbands are broken: {left} left."
                        : $"The warlord waits until the rest of the land is free: {left} left.";
            }
            else if (wild) return WildSiteHeld;
            return null;
        }

        /// <summary>The words for a refusal to march out of overrun land, to attack it, or to work a held site.</summary>
        public const string RoadsHeld = "The roads out are held by the horde. Take back your land first.";
        public const string Protected = "That land is overrun, and its holder is taking it back. It can't be attacked yet.";
        public const string SiteHeld = "The horde holds it. Free it first.";
        public const string WildSiteHeld = "The horde holds this land. Break its warbands and kill its warlord, and it opens.";
        public const string KeepHeld = "The horde holds this land. Cleanse it before you claim it.";

        // -----------------------------------------------------------------
        // Packs on the roads (phase 1b), fought where they are met on a walk
        // -----------------------------------------------------------------

        /// <summary>A pack's run is a pseudo-site too: <c>hordepack:{packId}</c>, the pack being <c>{regionId}:p{n}</c>.</summary>
        public const string PackPrefix = "hordepack:";

        /// <summary>
        /// How many packs a horde puts on the roads, at most. Row 2 said about ten; Mike 2026-11-12, "the more the merrier":
        /// with packs beside the roads too, a seat now has 15 (it was 3 to 10, about 6).
        /// </summary>
        public const int PackCount = 15;

        /// <summary>A pack's size: 3 to 6 (row 2; Mike: "rather small, but since it's level 1 this might be OK").</summary>
        public const int PackSmallest = 3, PackLargest = 6;

        /// <summary>
        /// Packs stand more than this many region cells from any site, and at least this far from each other. A region
        /// cell is 6 walk tiles and a sleeper sees 9, so packs 2 cells apart (12 tiles) rarely wake each other (when they
        /// do, it is one engagement, paid together), and one clear of a site stands about 12 tiles from its door.
        /// </summary>
        public const int PackClearOfSites = 1, PackSpacing = 2;

        /// <summary>
        /// The keep keeps them further off: a walk starts at its door, and a pack one cell away saw the party and
        /// charged three seconds in, before a newcomer had their bearings (2026-11-12).
        /// </summary>
        public const int PackClearOfKeep = 2;

        /// <summary>Packs may stand on open land this many cells off a road (6 walk tiles: in sight from it).</summary>
        public const int PackBesideRoad = 1;

        /// <summary>One pack: where it stands (a region cell on a road), how many, and how strong.</summary>
        public sealed class Pack
        {
            public string Id;
            public GridCell Cell;
            public int Size;
            public int Level;
        }

        /// <summary>
        /// The warlord's guard (row 4, phase 1 wrap-up): one more warband, the largest, at the lair's door, a level under
        /// the warlord. It is a pack like the rest, so the lair waits for it too.
        /// </summary>
        public const string GuardSuffix = ":guard";
        public const int GuardLevelsAbove = LairLevelsAbove - 1;

        /// <summary>The guard stands this many region cells from the lair, at most (and clear of it, as every pack is).</summary>
        public const int GuardReach = 3;

        public static bool IsGuard(string packId) => packId != null && packId.EndsWith(GuardSuffix, StringComparison.Ordinal);

        /// <summary>About how many a camp's waves bring (the pay a pack's size is measured against).</summary>
        public const int CampEnemies = 20;

        /// <summary>A pack pays its share of a camp: its size against a camp's waves.</summary>
        public static double PackShareOfACamp(int size) => Math.Max(1, size) / (double)CampEnemies;

        public static string PackSiteId(string packId) => PackPrefix + packId;

        public static bool IsPackSiteId(string siteId) =>
            !string.IsNullOrEmpty(siteId) && siteId.StartsWith(PackPrefix, StringComparison.Ordinal);

        public static string PackOf(string packSiteId) =>
            IsPackSiteId(packSiteId) ? packSiteId.Substring(PackPrefix.Length) : null;

        /// <summary>The region's packs, at its horde's level, whether broken or not.</summary>
        public static List<Pack> Packs(WorldRegionData region) =>
            region == null ? new List<Pack>() : PacksOf(region, RegionGenerator.Generate(region));

        /// <summary>The region's packs over a layout already made: a seat's, or a wild horde's (fewer, by tier).</summary>
        public static List<Pack> PacksOf(WorldRegionData region, RegionLayout layout) =>
            region == null ? new List<Pack>() : Packs(layout, region.Seed, Math.Max(1, region.Horde?.BaseLevel ?? 1),
                IsWild(region), Math.Max(1, region.Tier));

        /// <summary>
        /// The packs a horde puts on a region's roads: seeded by the region, so every reader agrees. They stand on the
        /// roads between sites (laid before the roads out, so a neighbour never moves one), clear of the sites and of
        /// each other, the nearest the keep weakest.
        /// </summary>
        public static List<Pack> Packs(RegionLayout layout, int seed, int baseLevel, bool wild = false, int tier = 1)
        {
            int count = wild ? WildPacks(tier) : PackCount;
            var packs = new List<Pack>();
            if (layout?.Sites == null || layout.Sites.Count == 0) return packs;
            var roads = RegionRoadNetwork.Build(layout, seed, _ => false);
            bool ClearOfSites(GridCell cell) => layout.Sites.All(s =>
                Chebyshev(s.Cell, cell) > (s.Type == LocationType.Castle ? PackClearOfKeep : PackClearOfSites));
            var onRoad = new List<GridCell>();
            var seen = new HashSet<GridCell>();
            foreach (var path in roads.SitePaths)
                foreach (var cell in path)
                    if (seen.Add(cell) && ClearOfSites(cell))
                        onRoad.Add(cell);

            // Beside the road too (Mike 2026-11-12, more warbands): open land a cell off it, still in sight of the road,
            // so a small seat with short roads fills up as a large one does. Road cells are taken first.
            var besideRoad = new List<GridCell>();
            foreach (var path in roads.SitePaths)
                foreach (var cell in path)
                    for (int dx = -PackBesideRoad; dx <= PackBesideRoad; dx++)
                        for (int dy = -PackBesideRoad; dy <= PackBesideRoad; dy++)
                        {
                            var near = new GridCell(cell.X + dx, cell.Y + dy);
                            if (near.X < 0 || near.Y < 0 || near.X >= layout.Width || near.Y >= layout.Height) continue;
                            if (!seen.Add(near) || !ClearOfSites(near) || !RegionLayout.IsBuildable(layout.TerrainAt(near))) continue;
                            besideRoad.Add(near);
                        }

            var random = DeterministicRandom.ForSubject(unchecked((ulong)(uint)seed), Naming.Hash("horde-packs"));
            var candidates = Shuffled(onRoad, random).Concat(Shuffled(besideRoad, random)).ToList();

            // The guard first, so the other warbands keep their distance from it: the nearest road cell to the lair's
            // door, else open land, on the side facing home.
            var keepSite = layout.Sites.FirstOrDefault(s => s.Type == LocationType.Castle) ?? layout.Sites[0];
            var lair = LairOf(layout, wild);
            GridCell? guard = null;
            if (lair != null)
            {
                var roadCells = new HashSet<GridCell>(onRoad);
                guard = onRoad.Concat(besideRoad)
                    .Where(c => Chebyshev(c, lair.Cell) <= GuardReach)
                    .OrderBy(c => roadCells.Contains(c) ? 0 : 1)
                    .ThenBy(c => Chebyshev(c, lair.Cell))
                    .ThenBy(c => Distance(c, keepSite.Cell))
                    .ThenBy(c => c.Y).ThenBy(c => c.X)
                    .Cast<GridCell?>()
                    .FirstOrDefault();
            }

            var chosen = new List<GridCell>();
            if (guard.HasValue) candidates.Remove(guard.Value);
            foreach (var cell in candidates)
            {
                if (chosen.Count >= count) break;
                if (chosen.Any(c => Chebyshev(c, cell) < PackSpacing)) continue;
                if (guard.HasValue && Chebyshev(guard.Value, cell) < PackSpacing) continue;
                chosen.Add(cell);
            }

            var keep = layout.Sites.FirstOrDefault(s => s.Type == LocationType.Castle) ?? layout.Sites[0];
            chosen.Sort((a, b) => Distance(a, keep.Cell).CompareTo(Distance(b, keep.Cell)));
            string regionId = SiteSpec.RegionIdOf(layout.Sites[0].SiteId);
            for (int n = 0; n < chosen.Count; n++)
            {
                double t = chosen.Count <= 1 ? 0 : n / (double)(chosen.Count - 1);
                packs.Add(new Pack
                {
                    Id = regionId + ":p" + n,
                    Cell = chosen[n],
                    Size = PackSmallest + random.Next(PackLargest - PackSmallest + 1),
                    Level = baseLevel + (int)Math.Round(NearLevelsAbove + t * (FarLevelsAbove - NearLevelsAbove)),
                });
            }
            if (guard.HasValue)
                packs.Add(new Pack { Id = regionId + GuardSuffix, Cell = guard.Value, Size = PackLargest, Level = baseLevel + GuardLevelsAbove });
            return packs;
        }

        /// <summary>A seeded shuffle, in a fixed order first so every reader agrees.</summary>
        private static List<GridCell> Shuffled(List<GridCell> cells, DeterministicRandom random)
        {
            var list = cells.OrderBy(c => c.Y).ThenBy(c => c.X).ToList();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                var swap = list[i];
                list[i] = list[j];
                list[j] = swap;
            }
            return list;
        }

        /// <summary>The packs still on the roads: overrun land, not yet broken.</summary>
        public static List<Pack> StandingPacks(WorldRegionData region)
        {
            if (!IsOverrun(region)) return new List<Pack>();
            return Packs(region).Where(p => !region.Horde.Broken.Contains(p.Id)).ToList();
        }

        /// <summary>Why this pack can't be fought now, or null when it can.</summary>
        public static string WhyNotBreak(WorldRegionData region, string packId, string userId)
        {
            if (!IsOverrun(region)) return "No horde holds this land.";
            if (!IsWild(region) && !region.IsOwnedByPlayer(userId)) return "Only its holder can take this land back.";
            if (Packs(region).All(p => p.Id != packId)) return "There is no such warband.";
            if (region.Horde.Broken.Contains(packId)) return "That warband is already broken.";
            return null;
        }

        // -----------------------------------------------------------------
        // Wild land (phase 2): neutral regions overrun, cleansed by anyone
        // -----------------------------------------------------------------

        /// <summary>A wild horde's warbands by the region's tier, 1 to 4 (row P2-2), not counting the warlord's guard.</summary>
        public static readonly int[] WildPacksByTier = { 4, 6, 8, 10 };

        public static int WildPacks(int tier) => WildPacksByTier[Math.Max(1, Math.Min(WildPacksByTier.Length, tier)) - 1];

        /// <summary>
        /// A wild horde's base by tier (row P2-5): the region's own lowest site level (5 x tier - 4). Its warbands ramp
        /// up from it and its warlord stands <see cref="LairLevelsAbove"/> over: tier 1 is 2-5 and 6, tier 2 is 7-10
        /// and 11, tier 3 is 12-15 and 16, tier 4 is 17-20 and 21.
        /// </summary>
        public static int WildBaseLevel(int tier) => 5 * Math.Max(1, Math.Min(4, tier)) - 4;

        /// <summary>A wild horde for neutral land of this tier.</summary>
        public static HordeState NewWildHorde(int tier) => new HordeState { BaseLevel = WildBaseLevel(tier), Wild = true };

        /// <summary>Whether a region is wild land, which starts overrun (row P2-1): unowned, nobody's seat, no faction's.</summary>
        public static bool IsWildLand(WorldRegionData region) =>
            region != null && region.Ownership == LocationOwnership.Neutral && string.IsNullOrEmpty(region.OwnerUserId)
            && region.Faction == FactionId.None && !region.IsCapital;

        public static bool IsWild(WorldRegionData region) => region?.Horde?.Wild == true;

        /// <summary>A wild region whose warlord has fallen: its sites are open to everyone, and its keep may be claimed.</summary>
        public static bool IsCleansed(WorldRegionData region) => IsWild(region) && IsReclaimed(region);

        /// <summary>The keep of overrun wild land can't be claimed until the land is cleansed (row P2-6).</summary>
        public static bool KeepLocked(WorldRegionData region, SiteSpec site) =>
            IsWild(region) && IsOverrun(region) && site != null && site.Type == LocationType.Castle;

        /// <summary>A player shares in the cleansing with this many warbands broken there (row P2-4).</summary>
        public const int StakeToShare = 2;

        /// <summary>This player's stake in a wild region, or null.</summary>
        public static HordeStake StakeOf(WorldRegionData region, string userId) =>
            string.IsNullOrEmpty(userId) ? null : region?.Horde?.Stakes?.FirstOrDefault(s => s.UserId == userId);

        /// <summary>Everyone who shares in a wild region's cleansing: at least <see cref="StakeToShare"/> warbands broken.</summary>
        public static List<string> Stakeholders(WorldRegionData region) =>
            region?.Horde?.Stakes == null ? new List<string>()
                : region.Horde.Stakes.Where(s => s.Broken >= StakeToShare).Select(s => s.UserId).ToList();

        /// <summary>The cleansing chest (row P2-7): Magic, Rare at tier 3 and 4.</summary>
        public static Enums.ItemRarityTypes CleansingChest(int tier) =>
            tier >= 3 ? Enums.ItemRarityTypes.Rare : Enums.ItemRarityTypes.Magic;

        /// <summary>The season points each stakeholder gets for a region cleansed (row P2-7), by its chest.</summary>
        public static double CleansingPoints(int tier) => CleansingChest(tier) >= Enums.ItemRarityTypes.Rare ? 100 : 50;

        private static int Chebyshev(GridCell a, GridCell b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        private static int Distance(GridCell a, GridCell b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    }
}
