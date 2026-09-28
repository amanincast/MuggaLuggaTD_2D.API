using System.Collections.Generic;

namespace MuggaLuggaTD.Shared.World
{
    /// <summary>
    /// What a player sees <b>now</b> on the world map: the regions they hold and one ring beyond.
    ///
    /// <para>The client's fog (<c>RegionVisibilityService</c>) lights these regions and keeps anything
    /// once seen as a dimmed memory; the server asks the same question before it shows a player
    /// another player's companies (<c>docs/design/parties-and-travel.md</c> §5, phase 5), because a
    /// company is live news, not memory. One rule, so the map and what walks on it cannot disagree
    /// about where the fog lies.</para>
    /// </summary>
    public static class RegionSight
    {
        /// <summary>
        /// How many rings beyond a held region are lit. One: you can see who your neighbours are, and
        /// must take ground to see past them.
        /// </summary>
        public const int Radius = 1;

        /// <summary>The ids of the regions <paramref name="userId"/> can see now.</summary>
        public static HashSet<string> Lit(IEnumerable<WorldRegionData> regions, string userId, int radius = Radius)
        {
            var lit = new HashSet<string>();
            if (regions == null) return lit;

            var all = new List<WorldRegionData>();
            var held = new List<HexCoord>();
            foreach (var region in regions)
            {
                if (region == null || string.IsNullOrEmpty(region.RegionId)) continue;
                all.Add(region);
                if (!string.IsNullOrEmpty(userId) && region.IsOwnedByPlayer(userId)) held.Add(region.Hex);
            }

            foreach (var region in all)
            {
                foreach (var seat in held)
                {
                    if (HexCoord.Distance(seat, region.Hex) > radius) continue;
                    lit.Add(region.RegionId);
                    break;
                }
            }
            return lit;
        }
    }
}
