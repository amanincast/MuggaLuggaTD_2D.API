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
        /// <summary>
        /// A region is 32x18 cells — deliberately 16:9, and deliberately 576 cells.
        ///
        /// <para>It was 24x24. A square region on a widescreen display cannot both be seen whole and
        /// fill the view: framed to fit its height it left more than half the screen empty on either
        /// side, and the territory read as a picture of a place rather than a place. 32/18 is exactly
        /// 16/9, so the land now reaches both edges.</para>
        ///
        /// <para>32 x 18 is the same 576 cells as 24 x 24, which is why this reshape carries no
        /// balance with it: the biome profiles cover the same fraction of ground, and the site budget
        /// has the same room to space itself in.</para>
        /// </summary>
        public const int Width = 32;
        public const int Height = 18;

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

            // Start from dry ground and grow features into it.
            for (int i = 0; i < terrain.Length; i++)
                terrain[i] = TerrainClass.Land;

            var profile = ProfileFor(biome);

            // Shared across all three classes: a wood should be pushed away from a lake as firmly as
            // from another wood, or the features pile into one corner and leave the rest blank.
            var seeds = new List<int>();

            Grow(terrain, ref random, TerrainClass.Water, profile.WaterBlobs, profile.WaterSize, seeds);
            Grow(terrain, ref random, TerrainClass.Mountain, profile.MountainBlobs, profile.MountainSize, seeds);
            Grow(terrain, ref random, TerrainClass.Forest, profile.ForestBlobs, profile.ForestSize, seeds);

            Tidy(terrain);

            return terrain;
        }

        /// <summary>
        /// Grows <paramref name="blobs"/> patches of one class by accretion, each of
        /// <paramref name="size"/> cells.
        ///
        /// <para>A cell joins the patch with a weight of <c>n²</c>, where <c>n</c> is how many of its
        /// four neighbours already belong to that class. Squaring is what makes the shapes read as
        /// terrain: a cell nestled in a concavity has three or four neighbours and is nine to sixteen
        /// times likelier to fill than a cell dangling off a tip with one, so hollows close and
        /// tendrils never get going. The randomness survives in <i>which</i> of the equally-nestled
        /// cells wins, which is what keeps an edge irregular.</para>
        ///
        /// <para>Squared rather than cubed, which was tried first: cubing made filling an edge evenly
        /// so dominant that patches converged on rectangles, and a rectangular forest looks more
        /// artificial than a ragged one. Squaring leaves enough room for a coastline to wander.</para>
        ///
        /// <para>This replaced a random walk. A walk paints a path, and a path one cell wide is all
        /// edge — the old regions measured 17-32% interior, so two thirds of every lake was shore,
        /// with single-cell spurs and pinholes throughout. Size here is a count of cells, not of
        /// steps, so a patch is also exactly as big as the profile asks.</para>
        /// </summary>
        private static void Grow(TerrainClass[] terrain, ref DeterministicRandom random, TerrainClass what, int blobs, int size, List<int> seeds)
        {
            if (blobs <= 0 || size <= 0) return;

            var frontier = new List<int>();
            var queued = new bool[Width * Height];
            var weights = new List<int>();

            for (int blob = 0; blob < blobs; blob++)
            {
                frontier.Clear();
                System.Array.Clear(queued, 0, queued.Length);

                int start = FindOpenCell(terrain, ref random, seeds);
                if (start < 0) return; // No dry ground left to start from.

                seeds.Add(start);

                terrain[start] = what;
                Enqueue(terrain, frontier, queued, start);

                for (int grown = 1; grown < size && frontier.Count > 0; grown++)
                {
                    weights.Clear();
                    for (int i = 0; i < frontier.Count; i++)
                    {
                        int n = CountNeighbours(terrain, frontier[i], what);
                        weights.Add(n * n);
                    }

                    int pick = random.NextWeighted(weights.ToArray());
                    int cell = frontier[pick];
                    frontier.RemoveAt(pick);

                    terrain[cell] = what;
                    Enqueue(terrain, frontier, queued, cell);
                }
            }
        }

        /// <summary>Adds a cell's dry neighbours to the frontier, each at most once.</summary>
        private static void Enqueue(TerrainClass[] terrain, List<int> frontier, bool[] queued, int cell)
        {
            int x = cell % Width;
            int y = cell / Width;

            if (x > 0) Offer(terrain, frontier, queued, cell - 1);
            if (x < Width - 1) Offer(terrain, frontier, queued, cell + 1);
            if (y > 0) Offer(terrain, frontier, queued, cell - Width);
            if (y < Height - 1) Offer(terrain, frontier, queued, cell + Width);
        }

        private static void Offer(TerrainClass[] terrain, List<int> frontier, bool[] queued, int cell)
        {
            // Only dry ground is up for grabs, so a later class cannot eat an earlier one and leave
            // it ragged. Squeezed-out patches come up short instead, which Tidy then cleans.
            if (queued[cell] || terrain[cell] != TerrainClass.Land) return;

            queued[cell] = true;
            frontier.Add(cell);
        }

        private static int CountNeighbours(TerrainClass[] terrain, int cell, TerrainClass what)
        {
            int x = cell % Width;
            int y = cell / Width;
            int n = 0;

            if (x > 0 && terrain[cell - 1] == what) n++;
            if (x < Width - 1 && terrain[cell + 1] == what) n++;
            if (y > 0 && terrain[cell - Width] == what) n++;
            if (y < Height - 1 && terrain[cell + Width] == what) n++;

            return n;
        }

        /// <summary>
        /// Picks a dry cell to start a patch from: inside a margin, so a blob has room to round out
        /// instead of being clipped by the border, and as far as it can manage from the patches
        /// already placed.
        ///
        /// <para>Best-candidate sampling — offer a handful of cells, keep the one whose nearest
        /// existing seed is furthest away. Purely random seeds clump, and a region is only six or so
        /// patches, which is few enough that one unlucky roll leaves half of it bare grass. Sampling
        /// rather than enforcing a minimum distance means this can never fail to place a patch; it
        /// just does the best it can in a crowded region.</para>
        /// </summary>
        private static int FindOpenCell(TerrainClass[] terrain, ref DeterministicRandom random, List<int> seeds)
        {
            const int margin = 3;
            const int samples = 12;

            int best = -1;
            int bestDistance = -1;

            for (int attempt = 0; attempt < samples; attempt++)
            {
                int x = random.Next(margin, Width - margin);
                int y = random.Next(margin, Height - margin);
                int cell = (y * Width) + x;

                if (terrain[cell] != TerrainClass.Land) continue;

                int nearest = NearestSeedDistanceSquared(cell, seeds);
                if (nearest <= bestDistance) continue;

                best = cell;
                bestDistance = nearest;
            }

            if (best >= 0) return best;

            // Crowded region: take the first dry cell anywhere rather than skipping the patch.
            for (int cell = 0; cell < terrain.Length; cell++)
                if (terrain[cell] == TerrainClass.Land) return cell;

            return -1;
        }

        /// <summary>How far the nearest already-placed patch is, squared. Large when nothing is near.</summary>
        private static int NearestSeedDistanceSquared(int cell, List<int> seeds)
        {
            if (seeds.Count == 0) return int.MaxValue;

            int x = cell % Width;
            int y = cell / Width;
            int nearest = int.MaxValue;

            for (int i = 0; i < seeds.Count; i++)
            {
                int dx = x - (seeds[i] % Width);
                int dy = y - (seeds[i] / Width);
                int distance = (dx * dx) + (dy * dy);

                if (distance < nearest) nearest = distance;
            }

            return nearest;
        }

        /// <summary>
        /// Shaves the leftovers accretion cannot avoid: cells clinging on by a single edge, and
        /// pinholes of ground surrounded by one feature.
        ///
        /// <para>Both are artefacts rather than terrain — a one-cell island in a lake reads as a
        /// mistake, and a one-cell spit reads as a stray tile. Shaving a spur can expose another, so
        /// this runs to a fixed point, capped so it always terminates.</para>
        /// </summary>
        private static void Tidy(TerrainClass[] terrain)
        {
            const int maxPasses = 6;

            for (int pass = 0; pass < maxPasses; pass++)
            {
                bool changed = false;

                for (int cell = 0; cell < terrain.Length; cell++)
                {
                    var here = terrain[cell];

                    if (here == TerrainClass.Land)
                    {
                        // A pinhole: dry ground whose four neighbours are all one feature.
                        var surrounding = SurroundedBy(terrain, cell);
                        if (surrounding != TerrainClass.Land)
                        {
                            terrain[cell] = surrounding;
                            changed = true;
                        }

                        continue;
                    }

                    if (CountNeighbours(terrain, cell, here) <= 1)
                    {
                        terrain[cell] = TerrainClass.Land;
                        changed = true;
                    }
                }

                if (!changed) return;
            }
        }

        /// <summary>
        /// The one feature class filling all four of a cell's neighbours, or Land if they disagree
        /// or the cell is on the border.
        /// </summary>
        private static TerrainClass SurroundedBy(TerrainClass[] terrain, int cell)
        {
            int x = cell % Width;
            int y = cell / Width;

            if (x == 0 || y == 0 || x == Width - 1 || y == Height - 1) return TerrainClass.Land;

            var left = terrain[cell - 1];
            if (left == TerrainClass.Land) return TerrainClass.Land;

            if (terrain[cell + 1] != left) return TerrainClass.Land;
            if (terrain[cell - Width] != left) return TerrainClass.Land;
            if (terrain[cell + Width] != left) return TerrainClass.Land;

            return left;
        }

        private struct BiomeProfile
        {
            public int WaterBlobs, WaterSize;
            public int MountainBlobs, MountainSize;
            public int ForestBlobs, ForestSize;
        }

        /// <summary>
        /// How much of each feature a biome gets. <b>Size is a count of cells</b>, not of walk steps
        /// as it used to be, so these numbers are far smaller than the ones they replaced and mean
        /// what they say: a region is 576 cells, and these profiles cover roughly a quarter to a
        /// third of one.
        ///
        /// <para>Fewer, larger patches on purpose. Splitting the same budget into more blobs spends
        /// it on perimeter, and perimeter is the thing that made the old regions look like noise.</para>
        /// </summary>
        private static BiomeProfile ProfileFor(BiomeType biome)
        {
            switch (biome)
            {
                case BiomeType.Lakeland:
                    return new BiomeProfile { WaterBlobs = 3, WaterSize = 40, MountainBlobs = 0, MountainSize = 0, ForestBlobs = 2, ForestSize = 26 };
                case BiomeType.Swamp:
                    return new BiomeProfile { WaterBlobs = 3, WaterSize = 36, MountainBlobs = 0, MountainSize = 0, ForestBlobs = 2, ForestSize = 24 };
                case BiomeType.Highland:
                    return new BiomeProfile { WaterBlobs = 1, WaterSize = 22, MountainBlobs = 3, MountainSize = 38, ForestBlobs = 1, ForestSize = 26 };
                case BiomeType.Volcanic:
                    return new BiomeProfile { WaterBlobs = 0, WaterSize = 0, MountainBlobs = 3, MountainSize = 46, ForestBlobs = 1, ForestSize = 22 };
                case BiomeType.Forest:
                    return new BiomeProfile { WaterBlobs = 1, WaterSize = 22, MountainBlobs = 1, MountainSize = 20, ForestBlobs = 3, ForestSize = 40 };
                default: // Grassland — the gentle one players start in.
                    return new BiomeProfile { WaterBlobs = 2, WaterSize = 28, MountainBlobs = 1, MountainSize = 20, ForestBlobs = 2, ForestSize = 26 };
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
