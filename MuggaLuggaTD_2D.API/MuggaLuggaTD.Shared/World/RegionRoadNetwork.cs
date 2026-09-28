using System;
using System.Collections.Generic;

namespace MuggaLuggaTD.Shared.World
{
    /// <summary>
    /// The roads of a region, at region-cell resolution: tracks from every site to the keep, and a
    /// few leading out of the region (<c>docs/design/parties-and-travel.md</c> §3, Unity repo).
    ///
    /// <para><b>Why it is shared.</b> The client paints these roads and companies walk them; the
    /// server times the walk. Both must see the same road, or a company would arrive before (or after)
    /// it visibly reached the door. So the <i>cells</i> are decided here, once, and the client only
    /// smooths and paints them.</para>
    ///
    /// <para><b>A network, not spokes.</b> The keep is joined first; every other site, nearest first,
    /// joins the <i>nearest point already on a road</i>, so tracks merge into trunks and fork. Paths
    /// are found over the region's cells, eight ways: open land cheap, woods dearer, water and
    /// mountains closed, a road already laid cheapest of all.</para>
    ///
    /// <para><b>A road to every neighbour</b> (phase 4, §6). Roads leave by the side facing each hex
    /// neighbour - east and west by those edges, the two northern neighbours by the left and right
    /// halves of the top edge, the southern two by the bottom - so the road that leaves a region's
    /// east side arrives in the region to its east. That is what lets a journey cross the map.</para>
    ///
    /// <para>Computed from the layout and the seed; nothing is stored. The generator is untouched.</para>
    /// </summary>
    public sealed class RegionRoadNetwork
    {
        /// <summary>How much a step through woods costs against one over open land.</summary>
        public const float ForestCost = 2.2f;

        /// <summary>Stepping onto a road already laid costs this much of a fresh step, so roads share trunks.</summary>
        public const float RoadReuseCost = 0.35f;


        private readonly HashSet<GridCell> _cells = new HashSet<GridCell>();
        private readonly List<List<GridCell>> _sitePaths = new List<List<GridCell>>();
        private readonly List<RoadExit> _exits = new List<RoadExit>();

        public RegionLayout Layout { get; }

        /// <summary>Every cell a road runs through.</summary>
        public IReadOnlyCollection<GridCell> Cells => _cells;

        /// <summary>Each site's road, from the site to where it joins the network.</summary>
        public IReadOnlyList<List<GridCell>> SitePaths => _sitePaths;

        /// <summary>The roads that leave the region, each from its edge cell to where it joins.</summary>
        public IReadOnlyList<RoadExit> Exits => _exits;

        public bool IsRoad(GridCell cell) => _cells.Contains(cell);

        private RegionRoadNetwork(RegionLayout layout)
        {
            Layout = layout;
        }

        /// <summary>
        /// A region's roads, with a road out toward each neighbour the world has
        /// (<paramref name="regionAt"/> says whether a region stands at a hex).
        /// </summary>
        public static RegionRoadNetwork For(WorldRegionData region, Func<HexCoord, bool> regionAt)
        {
            if (region == null) return new RegionRoadNetwork(null);
            var layout = RegionGenerator.Generate(region);
            return Build(layout, region.Seed,
                regionAt == null ? (Func<int, bool>)null : d => regionAt(region.Hex.Neighbour(d)));
        }

        /// <summary>
        /// Lays the roads. <paramref name="leadsToward"/> says which hex directions (0-5, as
        /// <see cref="HexCoord.Direction"/>) have a neighbour to lead to; null means all six.
        /// </summary>
        public static RegionRoadNetwork Build(RegionLayout layout, int seed, Func<int, bool> leadsToward = null)
        {
            var net = new RegionRoadNetwork(layout);
            if (layout == null || layout.Sites == null || layout.Sites.Count == 0) return net;

            SiteSpec hub = null;
            foreach (var site in layout.Sites)
                if (site.Type == LocationType.Castle) { hub = site; break; }
            hub = hub ?? layout.Sites[0];
            net._cells.Add(hub.Cell);

            // Nearest the keep first, so the trunk grows outward and the far sites branch off it.
            var others = new List<SiteSpec>(layout.Sites);
            others.Remove(hub);
            others.Sort((a, b) => DistanceSq(a.Cell, hub.Cell).CompareTo(DistanceSq(b.Cell, hub.Cell)));

            foreach (var site in others)
            {
                var path = net.Join(site.Cell);
                if (path == null) continue;   // on an island or ringed by rock: no road reaches it
                foreach (var cell in path) net._cells.Add(cell);
                net._sitePaths.Add(path);
            }

            // A road out toward each neighbour, on the side that faces it. Each is rolled from its own
            // stream, so whether one neighbour exists never moves another's road.
            for (int direction = 0; direction < 6; direction++)
            {
                if (leadsToward != null && !leadsToward(direction)) continue;
                var rng = new DeterministicRandom(unchecked((ulong)(uint)seed * 53UL + 0x40ADUL + (ulong)direction * 7919UL));
                var gate = EdgeCell(layout, direction, ref rng);
                if (gate == null) continue;   // water or rock the whole length of that edge
                var path = net.Join(gate.Value);
                if (path == null) continue;
                foreach (var cell in path) net._cells.Add(cell);
                net._exits.Add(new RoadExit(direction, SideOf(direction), path));
            }
            return net;
        }

        /// <summary>The road out toward hex direction <paramref name="direction"/>, or null.</summary>
        public RoadExit ExitToward(int direction)
        {
            foreach (var exit in _exits) if (exit.Direction == direction) return exit;
            return null;
        }

        /// <summary>The edge a hex direction leaves by (pointy-top hexes; +r runs south-east).</summary>
        public static RegionSide SideOf(int direction)
        {
            switch (((direction % 6) + 6) % 6)
            {
                case 0: return RegionSide.East;
                case 1: case 2: return RegionSide.North;
                case 3: return RegionSide.West;
                default: return RegionSide.South;
            }
        }

        private static int DistanceSq(GridCell a, GridCell b)
        {
            int dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        /// <summary>The cheapest walk from <paramref name="from"/> to any cell already on the network, or null.</summary>
        private List<GridCell> Join(GridCell from)
        {
            if (_cells.Contains(from)) return new List<GridCell> { from };
            return Search(from, cell => _cells.Contains(cell), (x, y) => BuildCost(x, y));
        }

        /// <summary>What one step onto a cell costs while roads are being laid, or 0 if it cannot be stepped on.</summary>
        private float BuildCost(int x, int y)
        {
            switch (Layout.TerrainAt(x, y))
            {
                case TerrainClass.Water:
                case TerrainClass.Mountain:
                    return 0f;
                case TerrainClass.Forest:
                    return _cells.Contains(new GridCell(x, y)) ? RoadReuseCost : ForestCost;
                default:
                    return _cells.Contains(new GridCell(x, y)) ? RoadReuseCost : 1f;
            }
        }

        /// <summary>
        /// Dijkstra over the region's cells, eight ways, never cutting a blocked corner. A step's cost
        /// is <paramref name="stepCost"/>(x, y) (0 = cannot enter), ×√2 on a diagonal. Returns the path
        /// from <paramref name="from"/> to the first cell <paramref name="isGoal"/> accepts, or null.
        /// </summary>
        public List<GridCell> Search(GridCell from, Func<GridCell, bool> isGoal, Func<int, int, float> stepCost)
        {
            int w = Layout.Width, h = Layout.Height;
            if (from.X < 0 || from.Y < 0 || from.X >= w || from.Y >= h) return null;

            var cost = new float[w * h];
            var came = new int[w * h];
            for (int i = 0; i < cost.Length; i++) { cost[i] = float.MaxValue; came[i] = -1; }

            // Small enough (576 cells) that a plain sorted-set Dijkstra is instant.
            var open = new SortedSet<(float Cost, int Index)>();
            int start = from.Y * w + from.X;
            cost[start] = 0f;
            open.Add((0f, start));

            while (open.Count > 0)
            {
                var (c, index) = open.Min;
                open.Remove(open.Min);
                if (c > cost[index]) continue;

                var cell = new GridCell(index % w, index / w);
                if (isGoal(cell)) return Unwind(came, index, w);

                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = cell.X + dx, ny = cell.Y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        float step = stepCost(nx, ny);
                        if (step <= 0f) continue;
                        if (dx != 0 && dy != 0)
                        {
                            if (stepCost(cell.X + dx, cell.Y) <= 0f || stepCost(cell.X, cell.Y + dy) <= 0f) continue;
                            step *= 1.414f;
                        }

                        int next = ny * w + nx;
                        float total = c + step;
                        if (total >= cost[next]) continue;
                        cost[next] = total;
                        came[next] = index;
                        open.Add((total, next));
                    }
            }
            return null;
        }

        private static List<GridCell> Unwind(int[] came, int index, int w)
        {
            var path = new List<GridCell>();
            for (int i = index; i >= 0; i = came[i]) path.Add(new GridCell(i % w, i / w));
            path.Reverse();
            return path;
        }

        /// <summary>
        /// An open cell on the stretch of edge that faces hex direction <paramref name="direction"/>:
        /// the middle of the east or west edge, or the right or left half of the top or bottom.
        /// </summary>
        private static GridCell? EdgeCell(RegionLayout layout, int direction, ref DeterministicRandom rng)
        {
            int w = layout.Width, h = layout.Height;
            int half = w / 2;
            for (int tries = 0; tries < 16; tries++)
            {
                GridCell cell;
                switch (direction)
                {
                    case 0: cell = new GridCell(w - 1, h / 4 + rng.Next(Math.Max(1, h / 2))); break;
                    case 3: cell = new GridCell(0, h / 4 + rng.Next(Math.Max(1, h / 2))); break;
                    case 1: cell = new GridCell(half + 2 + rng.Next(Math.Max(1, half - 6)), h - 1); break;
                    case 2: cell = new GridCell(4 + rng.Next(Math.Max(1, half - 6)), h - 1); break;
                    case 4: cell = new GridCell(4 + rng.Next(Math.Max(1, half - 6)), 0); break;
                    default: cell = new GridCell(half + 2 + rng.Next(Math.Max(1, half - 6)), 0); break;
                }
                var t = layout.TerrainAt(cell);
                if (t == TerrainClass.Land || t == TerrainClass.Forest) return cell;
            }
            return null;
        }
    }

    /// <summary>A region's edge (the order <see cref="RegionRoadNetwork"/> numbers them).</summary>
    public enum RegionSide
    {
        West = 0,
        East = 1,
        South = 2,
        North = 3,
    }

    /// <summary>A road leaving the region: toward which neighbour, by which side, and its cells from the edge inward.</summary>
    public sealed class RoadExit
    {
        /// <summary>The hex direction (0-5, <see cref="HexCoord.Direction"/>) of the region it leads to.</summary>
        public int Direction { get; }
        public RegionSide Side { get; }
        public List<GridCell> Path { get; }

        public RoadExit(int direction, RegionSide side, List<GridCell> path)
        {
            Direction = direction;
            Side = side;
            Path = path;
        }
    }
}
