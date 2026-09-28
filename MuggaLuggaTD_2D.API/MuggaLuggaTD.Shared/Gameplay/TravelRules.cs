using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// How a company gets from one site to another inside a region, and how long it takes
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
        /// Seconds spent crossing from one region into the next before the company is seen on the far
        /// side. The crossing itself is not walked on any map yet (§6, phase 4); it is a leg of time.
        /// </summary>
        public const double CrossingSeconds = 60;

        /// <summary>
        /// A journey from another region into this one: across the border (<see cref="CrossingSeconds"/>),
        /// in by the road that leaves this region on the side facing where the company came from, and
        /// on to <paramref name="to"/>. The first cell is reached at <see cref="CrossingSeconds"/>, not 0.
        /// </summary>
        public static Journey PlanArrival(RegionRoadNetwork roads, HexCoord fromHex, HexCoord toHex, GridCell to)
        {
            if (roads?.Layout == null) return null;
            var entry = EntryCell(roads, SideFacing(toHex, fromHex));
            if (entry == null) return null;
            if (entry.Value == to)
                return new Journey(new List<GridCell> { to }, new[] { MinimumJourney.TotalSeconds });
            return Plan(roads, entry.Value, to, CrossingSeconds);
        }

        /// <summary>Which side of the region at <paramref name="here"/> faces the region at <paramref name="there"/>.</summary>
        public static RegionSide SideFacing(HexCoord here, HexCoord there)
        {
            // The world map lays hexes out pointy-top with +r running south-east (HexLayout.ToWorld).
            double dx = Math.Sqrt(3) * ((there.Q - here.Q) + (there.R - here.R) * 0.5);
            double dy = -1.5 * (there.R - here.R);
            if (Math.Abs(dx) >= Math.Abs(dy)) return dx >= 0 ? RegionSide.East : RegionSide.West;
            return dy >= 0 ? RegionSide.North : RegionSide.South;
        }

        /// <summary>Where a company from beyond <paramref name="side"/> comes in: that side's road, else the nearest road out, else the keep.</summary>
        private static GridCell? EntryCell(RegionRoadNetwork roads, RegionSide side)
        {
            RoadExit best = null;
            int bestScore = int.MinValue;
            foreach (var exit in roads.Exits)
            {
                if (exit.Path == null || exit.Path.Count == 0) continue;
                int score = exit.Side == side ? 2 : Opposite(exit.Side) == side ? 0 : 1;
                if (score > bestScore) { best = exit; bestScore = score; }
            }
            if (best != null) return best.Path[0];

            foreach (var site in roads.Layout.Sites)
                if (site.Type == LocationType.Castle) return site.Cell;
            return roads.Layout.Sites.Count > 0 ? roads.Layout.Sites[0].Cell : (GridCell?)null;
        }

        private static RegionSide Opposite(RegionSide side)
        {
            switch (side)
            {
                case RegionSide.West: return RegionSide.East;
                case RegionSide.East: return RegionSide.West;
                case RegionSide.South: return RegionSide.North;
                default: return RegionSide.South;
            }
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
}
