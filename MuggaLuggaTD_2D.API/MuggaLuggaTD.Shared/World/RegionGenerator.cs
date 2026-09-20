using System.Collections.Generic;

namespace MuggaLuggaTD.Shared.World
{
    /// <summary>
    /// Turns a region's seed into its interior: the ground, and the sites standing on it (design 8a).
    ///
    /// <para><b>This runs on both sides and must agree exactly.</b> The client generates a region to
    /// draw it; the server generates the same region to decide whether the site a player claims to
    /// have cleared exists, is fightable, and is not already spent. Divergence would read as a
    /// cheating player rather than as a bug, so everything here is integer arithmetic driven by
    /// <see cref="DeterministicRandom"/> and nothing consults a clock, a culture or a platform.</para>
    ///
    /// <para>The terrain is coarse on purpose. It exists so both sides agree about where a site may
    /// legitimately stand; choosing which tile to paint for each class is the client's business.</para>
    /// </summary>
    public static class RegionGenerator
    {
        public const int Width = 24;
        public const int Height = 24;

        // Sites are pushed apart so a region reads as a place rather than a pile, and so markers do
        // not overlap once the client draws them.
        private const int MinimumSiteSpacing = 4;

        /// <summary>Generates the interior of a region. The same arguments always give the same result.</summary>
        public static RegionLayout Generate(string regionId, int seed, BiomeType biome, int tier)
        {
            var terrain = GenerateTerrain(seed, biome);
            var sites = PlaceSites(regionId, seed, biome, tier, terrain);
            return new RegionLayout(Width, Height, terrain, sites);
        }

        /// <summary>Convenience overload for a region that already exists.</summary>
        public static RegionLayout Generate(WorldRegionData region)
        {
            if (region == null) return null;
            return Generate(region.RegionId, region.Seed, region.Biome, region.Tier);
        }

        // -----------------------------------------------------------------
        // Terrain
        // -----------------------------------------------------------------

        private static TerrainClass[] GenerateTerrain(int seed, BiomeType biome)
        {
            var terrain = new TerrainClass[Width * Height];
            var random = DeterministicRandom.ForSubject((ulong)(uint)seed, 0x7E44A1);

            // Start from dry ground and carve features into it. Growing features from seeds keeps
            // them contiguous, which a per-cell roll would not: scattered single water tiles read as
            // noise rather than as a lake.
            for (int i = 0; i < terrain.Length; i++)
                terrain[i] = TerrainClass.Land;

            var profile = ProfileFor(biome);

            Carve(terrain, ref random, TerrainClass.Water, profile.WaterBlobs, profile.WaterSize);
            Carve(terrain, ref random, TerrainClass.Mountain, profile.MountainBlobs, profile.MountainSize);
            Carve(terrain, ref random, TerrainClass.Forest, profile.ForestBlobs, profile.ForestSize);

            return terrain;
        }

        /// <summary>
        /// Grows <paramref name="blobs"/> patches of one class by random walk. A walk can cross
        /// ground it has already painted, so patches are ragged rather than circular.
        /// </summary>
        private static void Carve(TerrainClass[] terrain, ref DeterministicRandom random, TerrainClass what, int blobs, int size)
        {
            for (int blob = 0; blob < blobs; blob++)
            {
                int x = random.Next(Width);
                int y = random.Next(Height);

                for (int step = 0; step < size; step++)
                {
                    if (x >= 0 && y >= 0 && x < Width && y < Height)
                        terrain[(y * Width) + x] = what;

                    switch (random.Next(4))
                    {
                        case 0: x++; break;
                        case 1: x--; break;
                        case 2: y++; break;
                        default: y--; break;
                    }

                    // Reflect off the edges rather than clamping, which would smear a walk along them.
                    if (x < 0) x = 1;
                    if (y < 0) y = 1;
                    if (x >= Width) x = Width - 2;
                    if (y >= Height) y = Height - 2;
                }
            }
        }

        private struct BiomeProfile
        {
            public int WaterBlobs, WaterSize;
            public int MountainBlobs, MountainSize;
            public int ForestBlobs, ForestSize;
        }

        private static BiomeProfile ProfileFor(BiomeType biome)
        {
            switch (biome)
            {
                case BiomeType.Fenland:
                    return new BiomeProfile { WaterBlobs = 5, WaterSize = 90, MountainBlobs = 0, MountainSize = 0, ForestBlobs = 2, ForestSize = 40 };
                case BiomeType.Marsh:
                    return new BiomeProfile { WaterBlobs = 6, WaterSize = 80, MountainBlobs = 0, MountainSize = 0, ForestBlobs = 3, ForestSize = 30 };
                case BiomeType.Highland:
                    return new BiomeProfile { WaterBlobs = 1, WaterSize = 30, MountainBlobs = 5, MountainSize = 70, ForestBlobs = 2, ForestSize = 30 };
                case BiomeType.Volcanic:
                    return new BiomeProfile { WaterBlobs = 0, WaterSize = 0, MountainBlobs = 6, MountainSize = 80, ForestBlobs = 1, ForestSize = 20 };
                case BiomeType.Thornwood:
                    return new BiomeProfile { WaterBlobs = 2, WaterSize = 35, MountainBlobs = 1, MountainSize = 25, ForestBlobs = 6, ForestSize = 90 };
                default: // RiverVale — the gentle one players start in.
                    return new BiomeProfile { WaterBlobs = 2, WaterSize = 60, MountainBlobs = 1, MountainSize = 25, ForestBlobs = 3, ForestSize = 45 };
            }
        }

        // -----------------------------------------------------------------
        // Sites
        // -----------------------------------------------------------------

        /// <summary>
        /// How many of each kind of site a region of a given tier holds. Richer regions are worth
        /// more and cost more to take, which is what gives the map somewhere to push toward.
        /// </summary>
        private static Dictionary<LocationType, int> BudgetFor(int tier, BiomeType biome)
        {
            int t = tier < 1 ? 1 : (tier > 4 ? 4 : tier);

            var budget = new Dictionary<LocationType, int>
            {
                { LocationType.Castle, 1 },                 // the keep — every region has a seat
                { LocationType.Dungeon, 1 + t },            // 2..5
                { LocationType.NeutralHome, 1 + (t / 2) },  // 1..3 settlements
                { LocationType.ResourceNode, 1 + t },       // 2..5
                { LocationType.Ruin, t >= 3 ? 1 : 0 }
            };

            // A portal is the rarer, harder fight, and only worth placing where the land is already
            // dangerous.
            if (biome == BiomeType.Volcanic || biome == BiomeType.Highland)
                budget[LocationType.Portal] = 1;

            return budget;
        }

        private static List<SiteSpec> PlaceSites(string regionId, int seed, BiomeType biome, int tier, TerrainClass[] terrain)
        {
            var sites = new List<SiteSpec>();
            var random = DeterministicRandom.ForSubject((ulong)(uint)seed, 0x5175E5);

            var candidates = BuildableCells(terrain);
            random.Shuffle(candidates);

            var budget = BudgetFor(tier, biome);
            int index = 0;

            // A fixed order over the budget keeps placement deterministic: a Dictionary's own order
            // is not guaranteed, and the keep must be placed first so it takes the best ground.
            foreach (var type in PlacementOrder)
            {
                if (!budget.TryGetValue(type, out int count)) continue;

                for (int i = 0; i < count; i++)
                {
                    var cell = TakeSpacedCell(candidates, sites);
                    if (cell == null) return sites; // Region is full; better a smaller region than an overlap.

                    sites.Add(new SiteSpec
                    {
                        SiteId = regionId + ":" + index,
                        Type = type,
                        Cell = cell.Value,
                        Tier = TierForSite(type, tier, ref random),
                        Level = LevelForSite(type, tier, ref random)
                    });

                    index++;
                }
            }

            return sites;
        }

        private static readonly LocationType[] PlacementOrder =
        {
            LocationType.Castle,
            LocationType.Portal,
            LocationType.Dungeon,
            LocationType.NeutralHome,
            LocationType.ResourceNode,
            LocationType.Ruin
        };

        private static List<GridCell> BuildableCells(TerrainClass[] terrain)
        {
            var cells = new List<GridCell>();
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                if (RegionLayout.IsBuildable(terrain[(y * Width) + x]))
                    cells.Add(new GridCell(x, y));
            }

            return cells;
        }

        /// <summary>Takes the first shuffled cell far enough from everything placed so far.</summary>
        private static GridCell? TakeSpacedCell(List<GridCell> candidates, List<SiteSpec> placed)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var cell = candidates[i];
                if (!IsClearOf(cell, placed)) continue;

                candidates.RemoveAt(i);
                return cell;
            }

            return null;
        }

        private static bool IsClearOf(GridCell cell, List<SiteSpec> placed)
        {
            for (int i = 0; i < placed.Count; i++)
            {
                var other = placed[i].Cell;
                int dx = cell.X - other.X;
                int dy = cell.Y - other.Y;
                if ((dx * dx) + (dy * dy) < MinimumSiteSpacing * MinimumSiteSpacing)
                    return false;
            }

            return true;
        }

        private static int TierForSite(LocationType type, int regionTier, ref DeterministicRandom random)
        {
            if (type == LocationType.Portal)
                return regionTier; // The region's hardest fight.

            if (type == LocationType.Dungeon)
                return random.Next(1, regionTier + 1); // Somewhere up to the region's own tier.

            return regionTier;
        }

        private static int LevelForSite(LocationType type, int regionTier, ref DeterministicRandom random)
        {
            // Enemy level tracks the region's tier with a little spread, so neighbouring dungeons in
            // one region are not identical fights.
            int baseLevel = (regionTier * 5) - 4;      // tier 1 -> 1, tier 4 -> 16
            if (type == LocationType.Portal)
                return baseLevel + 4;

            return baseLevel + random.Next(0, 4);
        }
    }
}
