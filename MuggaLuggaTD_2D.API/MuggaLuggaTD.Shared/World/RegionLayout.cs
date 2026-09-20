using System;
using System.Collections.Generic;

namespace MuggaLuggaTD.Shared.World
{
    /// <summary>A cell inside a region's interior. Plain integers — the shared assembly has no Unity types.</summary>
    [Serializable]
    public struct GridCell : IEquatable<GridCell>
    {
        public int X;
        public int Y;

        public GridCell(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool Equals(GridCell other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is GridCell other && Equals(other);
        public override int GetHashCode() { unchecked { return (X * 397) ^ Y; } }
        public override string ToString() => $"({X}, {Y})";

        public static bool operator ==(GridCell a, GridCell b) => a.Equals(b);
        public static bool operator !=(GridCell a, GridCell b) => !a.Equals(b);
    }

    /// <summary>
    /// One place inside a region, as the seed generates it. This is the shape the server validates a
    /// PvE target against, and the shape the client turns into a marker.
    ///
    /// Nothing here is persisted: a site is recreated from the region's seed every time. Whatever has
    /// since happened to it lives in <see cref="SiteOverride"/>.
    /// </summary>
    [Serializable]
    public class SiteSpec
    {
        /// <summary>"<regionId>:<index>". Unique across the world, and parseable back to its region.</summary>
        public string SiteId;

        public LocationType Type;
        public GridCell Cell;

        /// <summary>Drives wave count and reward budget, as it does today.</summary>
        public int Tier = 1;

        /// <summary>Enemy level for a fightable site.</summary>
        public int Level = 1;

        /// <summary>True for the site types a player can enter and fight in.</summary>
        public bool IsFightable => Type == LocationType.Dungeon || Type == LocationType.Portal;

        /// <summary>The region id embedded in a site id, or null when it is not a site id.</summary>
        public static string RegionIdOf(string siteId)
        {
            if (string.IsNullOrEmpty(siteId)) return null;
            int split = siteId.LastIndexOf(':');
            return split <= 0 ? null : siteId.Substring(0, split);
        }
    }

    /// <summary>
    /// A region's interior: the ground, and what stands on it.
    ///
    /// The terrain grid is deliberately coarse — see <see cref="TerrainClass"/>. The server uses it
    /// only to agree with the client about where a site may stand; the client decides which tile to
    /// paint for each class.
    /// </summary>
    public class RegionLayout
    {
        public int Width { get; }
        public int Height { get; }

        private readonly TerrainClass[] _terrain;

        public IReadOnlyList<SiteSpec> Sites { get; }

        public RegionLayout(int width, int height, TerrainClass[] terrain, IReadOnlyList<SiteSpec> sites)
        {
            Width = width;
            Height = height;
            _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
            Sites = sites ?? Array.Empty<SiteSpec>();
        }

        public TerrainClass TerrainAt(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height)
                return TerrainClass.Water; // Off the edge reads as impassable rather than throwing.

            return _terrain[(y * Width) + x];
        }

        public TerrainClass TerrainAt(GridCell cell) => TerrainAt(cell.X, cell.Y);

        /// <summary>True where a site may stand: dry, flat ground.</summary>
        public static bool IsBuildable(TerrainClass terrain) => terrain == TerrainClass.Land;

        public SiteSpec FindSite(string siteId)
        {
            if (string.IsNullOrEmpty(siteId)) return null;

            for (int i = 0; i < Sites.Count; i++)
            {
                if (string.Equals(Sites[i].SiteId, siteId, StringComparison.Ordinal))
                    return Sites[i];
            }

            return null;
        }
    }
}
