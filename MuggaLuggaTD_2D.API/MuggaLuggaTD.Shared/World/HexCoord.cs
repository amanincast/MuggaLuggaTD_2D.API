using System;
using System.Collections.Generic;

namespace MuggaLuggaTD.Shared.World
{
    /// <summary>
    /// A hex on the world map, in axial coordinates (q, r).
    ///
    /// Axial rather than offset coordinates because the two things the map needs most — distance and
    /// neighbours — are arithmetic here and special-cased-by-row there. Supply lines are a walk over
    /// <see cref="Neighbours"/>, and spacing players apart at world generation is a comparison of
    /// <see cref="Distance"/>, so both stay honest.
    ///
    /// Persisted in the world blob as two integers; Unity maps them to cell positions for rendering.
    /// </summary>
    [Serializable]
    public struct HexCoord : IEquatable<HexCoord>
    {
        public int Q;
        public int R;

        public HexCoord(int q, int r)
        {
            Q = q;
            R = r;
        }

        /// <summary>The implied third cube coordinate. q + r + s == 0 always.</summary>
        public int S => -Q - R;

        private static readonly HexCoord[] Directions =
        {
            new HexCoord(1, 0), new HexCoord(1, -1), new HexCoord(0, -1),
            new HexCoord(-1, 0), new HexCoord(-1, 1), new HexCoord(0, 1)
        };

        public static HexCoord Direction(int index) => Directions[((index % 6) + 6) % 6];

        public HexCoord Neighbour(int direction)
        {
            var d = Direction(direction);
            return new HexCoord(Q + d.Q, R + d.R);
        }

        /// <summary>The six adjacent hexes, in a fixed order so callers stay deterministic.</summary>
        public IEnumerable<HexCoord> Neighbours()
        {
            for (int i = 0; i < 6; i++)
                yield return Neighbour(i);
        }

        /// <summary>Steps between two hexes — the length of the shortest path.</summary>
        public static int Distance(HexCoord a, HexCoord b)
        {
            return (Math.Abs(a.Q - b.Q) + Math.Abs(a.R - b.R) + Math.Abs(a.S - b.S)) / 2;
        }

        /// <summary>
        /// Hexes laid out in rings around the origin, innermost first, enough to hold
        /// <paramref name="count"/> of them. The order is fixed, which is what lets a world of a
        /// given size regenerate identically.
        /// </summary>
        public static List<HexCoord> Spiral(int count)
        {
            var result = new List<HexCoord>(Math.Max(count, 0));
            if (count <= 0) return result;

            result.Add(new HexCoord(0, 0));

            for (int radius = 1; result.Count < count; radius++)
            {
                // Start on the ring's western hex and walk it side by side.
                var hex = new HexCoord(-radius, radius);
                for (int side = 0; side < 6 && result.Count < count; side++)
                {
                    for (int step = 0; step < radius && result.Count < count; step++)
                    {
                        result.Add(hex);
                        hex = hex.Neighbour(side);
                    }
                }
            }

            return result;
        }

        public bool Equals(HexCoord other) => Q == other.Q && R == other.R;

        public override bool Equals(object obj) => obj is HexCoord other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (Q * 397) ^ R;
            }
        }

        public static bool operator ==(HexCoord a, HexCoord b) => a.Equals(b);

        public static bool operator !=(HexCoord a, HexCoord b) => !a.Equals(b);

        public override string ToString() => $"({Q}, {R})";
    }
}
