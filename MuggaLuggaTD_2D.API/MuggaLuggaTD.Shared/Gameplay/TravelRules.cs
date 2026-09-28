using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// How a company gets from one site to another - in a region or across several - and how long it takes
    /// (<c>docs/design/parties-and-travel.md</c> §3, Unity repo).
    ///
    /// <para><b>A journey is an order, not a walk.</b> The server finds the route and stamps when it
    /// left and when it arrives; where the company is at any moment is worked out from the clock
    /// (<see cref="Progress"/>). Nothing ticks.</para>
    ///
    /// <para><b>One to five minutes</b> (Mike, 2026-09-27): travel is flavour and adventure, never a
    /// wait for content. A short hop is lifted to the floor so a journey is always a journey, and a
    /// long crossing is capped at the ceiling. Between the two, time follows the ground: a road is
    /// quickest, open land slower, woods slowest.</para>
    ///
    /// <para>Shared so the region view walks the company along the very route the server timed.</para>
    /// </summary>
    public static class TravelRules
    {
        /// <summary>Seconds to cross one region cell by road, over open land, and through woods.</summary>
        public const double RoadSecondsPerCell = 5;
        public const double LandSecondsPerCell = 11;
        public const double ForestSecondsPerCell = 18;

        /// <summary>No journey is shorter or longer than this.</summary>
        public static readonly TimeSpan MinimumJourney = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan MaximumJourney = TimeSpan.FromMinutes(5);

        /// <summary>
        /// The quickest walk between two cells of a region, or null if none (water or rock between).
        /// Roads are preferred because they are quicker, not because the walk is told to keep to them.
        /// </summary>
        public static Journey Plan(RegionRoadNetwork roads, GridCell from, GridCell to) => Plan(roads, from, to, 0);

        /// <summary>
        /// Seconds spent crossing from one region into the next: the edge of one map to the edge of
        /// the other. Not walked on either map; the company is between them.
        /// </summary>
        public const double CrossingSeconds = 20;

        /// <summary>
        /// A journey from a cell of one region to a cell of another - or of the same one - across the
        /// world (§6, phase 4). The regions are found hex by hex (fewest crossings; a hex with no region
        /// cannot be crossed), and each is walked on its own roads: in by the road from the region
        /// behind, out by the road toward the region ahead, <see cref="CrossingSeconds"/> between them.
        /// Every region's walk is its own one to five minutes. Null if there is no way.
        /// </summary>
        public static RoutePlan PlanRoute(Func<HexCoord, WorldRegionData> regionAt,
            WorldRegionData fromRegion, GridCell from, WorldRegionData toRegion, GridCell to)
        {
            if (regionAt == null || fromRegion == null || toRegion == null) return null;

            var regions = RegionPath(regionAt, fromRegion, toRegion);
            if (regions == null) return null;

            var legs = new List<RouteLeg>();
            double clock = 0;
            for (int i = 0; i < regions.Count; i++)
            {
                var region = regions[i];
                var roads = RegionRoadNetwork.For(region, hex => regionAt(hex) != null);
                if (roads.Layout == null) return null;

                GridCell? start = i == 0 ? from : GateToward(roads, DirectionTo(region, regions[i - 1]));
                GridCell? end = i == regions.Count - 1 ? to : GateToward(roads, DirectionTo(region, regions[i + 1]));
                if (start == null || end == null) return null;

                if (i > 0) clock += CrossingSeconds;

                List<GridCell> cells;
                double[] seconds;
                if (start.Value == end.Value)
                {
                    // In at the very door it leaves by (or already standing at the gate): a moment.
                    cells = new List<GridCell> { start.Value };
                    seconds = new[] { 0.0 };
                }
                else
                {
                    var walk = Plan(roads, start.Value, end.Value, 0);
                    if (walk == null) return null;
                    cells = walk.Cells;
                    seconds = walk.CumulativeSeconds;
                }

                var leg = new RouteLeg { RegionId = region.RegionId };
                for (int c = 0; c < cells.Count; c++)
                {
                    leg.Cells.Add(new[] { cells[c].X, cells[c].Y });
                    leg.Seconds.Add(Math.Round(clock + seconds[c], 2));
                }
                legs.Add(leg);
                clock = leg.Seconds[leg.Seconds.Count - 1];
            }

            // A walk of no length (to the cell it stands on) is not a journey.
            if (clock <= 0) return null;
            return new RoutePlan(legs);
        }

        /// <summary>The regions a journey passes through, first to last, by fewest crossings; null if cut off.</summary>
        public static List<WorldRegionData> RegionPath(Func<HexCoord, WorldRegionData> regionAt,
            WorldRegionData from, WorldRegionData to)
        {
            if (from == null || to == null) return null;
            if (from.RegionId == to.RegionId) return new List<WorldRegionData> { from };

            var cameFrom = new Dictionary<HexCoord, HexCoord> { [from.Hex] = from.Hex };
            var queue = new Queue<HexCoord>();
            queue.Enqueue(from.Hex);
            while (queue.Count > 0)
            {
                var here = queue.Dequeue();
                if (here == to.Hex) break;
                for (int d = 0; d < 6; d++)
                {
                    var next = here.Neighbour(d);
                    if (cameFrom.ContainsKey(next) || regionAt(next) == null) continue;
                    cameFrom[next] = here;
                    queue.Enqueue(next);
                }
            }
            if (!cameFrom.ContainsKey(to.Hex)) return null;

            var path = new List<WorldRegionData>();
            for (var hex = to.Hex; ; hex = cameFrom[hex])
            {
                path.Add(regionAt(hex));
                if (hex == from.Hex) break;
            }
            path.Reverse();
            return path;
        }

        /// <summary>The hex direction from <paramref name="here"/> to its neighbour <paramref name="there"/>.</summary>
        public static int DirectionTo(WorldRegionData here, WorldRegionData there)
        {
            for (int d = 0; d < 6; d++)
                if (here.Hex.Neighbour(d) == there.Hex) return d;
            return 0;
        }

        /// <summary>
        /// Where a road toward hex direction <paramref name="direction"/> meets the edge: that road, else
        /// one leaving by the same side, else any road out, else the keep.
        /// </summary>
        public static GridCell? GateToward(RegionRoadNetwork roads, int direction)
        {
            var exit = roads.ExitToward(direction);
            if (exit == null)
            {
                var side = RegionRoadNetwork.SideOf(direction);
                foreach (var e in roads.Exits) if (e.Side == side) { exit = e; break; }
            }
            if (exit == null && roads.Exits.Count > 0) exit = roads.Exits[0];
            if (exit != null && exit.Path.Count > 0) return exit.Path[0];

            foreach (var site in roads.Layout.Sites)
                if (site.Type == LocationType.Castle) return site.Cell;
            return roads.Layout.Sites.Count > 0 ? roads.Layout.Sites[0].Cell : (GridCell?)null;
        }

        private static Journey Plan(RegionRoadNetwork roads, GridCell from, GridCell to, double leadSeconds)
        {
            if (roads?.Layout == null) return null;
            if (from == to) return null;

            var layout = roads.Layout;
            // The site cells themselves are always enterable: a site may stand at the edge of woods.
            float StepCost(int x, int y)
            {
                var cell = new GridCell(x, y);
                if (cell == to || cell == from) return (float)RoadSecondsPerCell;
                return (float)SecondsFor(layout, roads, cell);
            }

            var path = roads.Search(from, cell => cell == to, StepCost);
            if (path == null || path.Count < 2) return null;

            // Cumulative seconds at each cell, by the same costs the search used.
            var seconds = new double[path.Count];
            seconds[0] = leadSeconds;
            for (int i = 1; i < path.Count; i++)
            {
                var a = path[i - 1];
                var b = path[i];
                double step = StepCost(b.X, b.Y);
                if (a.X != b.X && a.Y != b.Y) step *= 1.414;
                seconds[i] = seconds[i - 1] + step;
            }

            // Stretch or squeeze to the bounds, keeping the shape of the walk: the company still
            // slows in the woods and quickens on the road.
            double total = seconds[path.Count - 1];
            double target = Math.Max(MinimumJourney.TotalSeconds, Math.Min(MaximumJourney.TotalSeconds, total));
            double scale = total > 0 ? target / total : 1;
            for (int i = 0; i < seconds.Length; i++) seconds[i] *= scale;

            return new Journey(path, seconds);
        }

        /// <summary>Seconds to cross <paramref name="cell"/>, or 0 if it cannot be crossed.</summary>
        public static double SecondsFor(RegionLayout layout, RegionRoadNetwork roads, GridCell cell)
        {
            var terrain = layout.TerrainAt(cell);
            if (terrain == TerrainClass.Water || terrain == TerrainClass.Mountain) return 0;
            if (roads != null && roads.IsRoad(cell)) return RoadSecondsPerCell;
            return terrain == TerrainClass.Forest ? ForestSecondsPerCell : LandSecondsPerCell;
        }

        /// <summary>
        /// How far along a journey is at <paramref name="now"/>, as a fractional index into its cells:
        /// 0 at the start, <c>Cells.Count - 1</c> on arrival. Walks the cumulative seconds, so a company
        /// visibly slows through woods.
        /// </summary>
        public static double Progress(IReadOnlyList<double> cumulativeSeconds, DateTime departedAt, DateTime now)
        {
            if (cumulativeSeconds == null || cumulativeSeconds.Count == 0) return 0;
            double t = (now - departedAt).TotalSeconds;
            if (t <= cumulativeSeconds[0]) return 0;
            int last = cumulativeSeconds.Count - 1;
            if (t >= cumulativeSeconds[last]) return last;

            for (int i = 1; i <= last; i++)
            {
                if (t > cumulativeSeconds[i]) continue;
                double span = cumulativeSeconds[i] - cumulativeSeconds[i - 1];
                return i - 1 + (span > 0 ? (t - cumulativeSeconds[i - 1]) / span : 1);
            }
            return last;
        }
    }

    /// <summary>A planned walk: the cells, and the seconds since departure at which each is reached.</summary>
    public sealed class Journey
    {
        public List<GridCell> Cells { get; }
        public double[] CumulativeSeconds { get; }
        public TimeSpan Duration => TimeSpan.FromSeconds(CumulativeSeconds[CumulativeSeconds.Length - 1]);

        public Journey(List<GridCell> cells, double[] cumulativeSeconds)
        {
            Cells = cells;
            CumulativeSeconds = cumulativeSeconds;
        }
    }

    /// <summary>
    /// One region's stretch of a journey: the region, its cells as [x, y], and the seconds after
    /// departure at which each is reached. Between two legs is a border crossing, walked on no map.
    /// Stored as it is (the server's <c>RouteJson</c>) and sent as it is, so both sides walk the same.
    /// </summary>
    public sealed class RouteLeg
    {
        public string RegionId { get; set; }
        public List<int[]> Cells { get; set; } = new List<int[]>();
        public List<double> Seconds { get; set; } = new List<double>();
    }

    /// <summary>A journey across one region or several, leg by leg.</summary>
    public sealed class RoutePlan
    {
        public List<RouteLeg> Legs { get; }

        public TimeSpan Duration
        {
            get
            {
                var last = Legs.Count > 0 ? Legs[Legs.Count - 1] : null;
                return TimeSpan.FromSeconds(last == null || last.Seconds.Count == 0 ? 0 : last.Seconds[last.Seconds.Count - 1]);
            }
        }

        public RoutePlan(List<RouteLeg> legs) => Legs = legs ?? new List<RouteLeg>();

        /// <summary>The leg a company is on <paramref name="seconds"/> after setting out: the last one begun.</summary>
        public static int LegAt(IReadOnlyList<RouteLeg> legs, double seconds)
        {
            if (legs == null || legs.Count == 0) return -1;
            int at = 0;
            for (int i = 0; i < legs.Count; i++)
                if (legs[i].Seconds.Count > 0 && legs[i].Seconds[0] <= seconds) at = i;
            return at;
        }
    }
}
