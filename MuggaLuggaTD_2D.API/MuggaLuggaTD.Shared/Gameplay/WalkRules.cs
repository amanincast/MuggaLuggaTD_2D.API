using System;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// Walking a region on foot (Exploration spec, phase 1; Mike 2026-11-04): a company you lead walks its
    /// region at the fight's scale, and reaching a site is an arrival like a march's.
    ///
    /// <para><b>Walking is travel, and it is allowed to be faster</b> (row 3): walking costs your attention,
    /// sending costs none. The server only keeps a floor, so a client cannot teleport its company across the
    /// region: an arrival is accepted once enough time has passed, since the company last changed, to walk the
    /// shortest way there at <see cref="CellsPerSecond"/>.</para>
    ///
    /// <para>The pace is generous on purpose (the phases are feelers): a hero's run is about 4 fight cells a
    /// second and dodging adds about 2, so 0.7 to 1 region cell a second (6 fight cells a cell). The floor is
    /// paced at <see cref="CellsPerSecond"/>, comfortably above either, so an honest walk is never refused.</para>
    /// </summary>
    public static class WalkRules
    {
        /// <summary>The fastest a company may cross region cells on foot, for the server's floor.</summary>
        public const double CellsPerSecond = 1.2;

        /// <summary>Seconds forgiven off every floor, for the clock between the client and the server.</summary>
        public const double SlackSeconds = 2;

        /// <summary>How near a site's cell (in region cells) a walker must come for it to count as reached.</summary>
        public const int ReachCells = 1;

        /// <summary>
        /// The shortest walk between two cells of a region, in region cells (a diagonal counts 1.414), over every
        /// cell that is not water or mountain; null if there is none. Roads are not preferred: on foot, any open
        /// ground is as quick as a road.
        /// </summary>
        public static double? CellsBetween(RegionRoadNetwork roads, GridCell from, GridCell to)
        {
            var layout = roads?.Layout;
            if (layout == null) return null;
            if (from == to) return 0;

            float Step(int x, int y)
            {
                var cell = new GridCell(x, y);
                if (cell == from || cell == to) return 1f;
                var terrain = layout.TerrainAt(cell);
                return terrain == TerrainClass.Water || terrain == TerrainClass.Mountain ? 0f : 1f;
            }

            var path = roads.Search(from, cell => cell == to, Step);
            if (path == null || path.Count < 2) return null;

            double cells = 0;
            for (int i = 1; i < path.Count; i++)
            {
                var a = path[i - 1];
                var b = path[i];
                cells += a.X != b.X && a.Y != b.Y ? 1.414 : 1.0;
            }
            return cells;
        }

        /// <summary>The least time a walk of <paramref name="cells"/> region cells may take, slack forgiven.</summary>
        public static TimeSpan Floor(double cells) =>
            TimeSpan.FromSeconds(Math.Max(0, cells / CellsPerSecond - SlackSeconds));
    }
}
