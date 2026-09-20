using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// Whether a region can still be supplied from its owner's capital (design 7d).
    ///
    /// <para>A region is supplied when an unbroken chain of regions that same owner holds leads back
    /// to their capital. Cut that chain and the region's hold drops — the design's "cut off from
    /// Hakkar's capital, so supply drags their hold down 15%".</para>
    ///
    /// <para>This is what turns a scatter of owned hexes into territory. Without it, holdings are
    /// independent dots and there is no reason to care which of them touches which; with it, a
    /// salient can be pinched off, and where you expand starts to matter.</para>
    /// </summary>
    public static class SupplyCalculator
    {
        /// <summary>Full supply: a chain of the owner's own regions reaches their capital.</summary>
        public const double Supplied = 1.00;

        /// <summary>Cut off from the capital — the design's 15% drag.</summary>
        public const double CutOff = 0.85;

        /// <summary>A capital supplies itself, and cannot be cut off from itself.</summary>
        public static double SupplyFor(WorldRegionData region, IReadOnlyCollection<WorldRegionData> allRegions)
        {
            if (region == null) return Supplied;

            // Only a player's holdings have a capital to be cut off from. NPC factions and unclaimed
            // land are always treated as supplied, because there is no line to cut.
            if (region.Ownership != LocationOwnership.Player || string.IsNullOrEmpty(region.OwnerUserId))
                return Supplied;

            return IsConnectedToCapital(region, allRegions) ? Supplied : CutOff;
        }

        /// <summary>
        /// Walks outward from the region through neighbours the same player owns, looking for their
        /// capital. Breadth-first, so it stops as soon as a chain exists rather than mapping the
        /// whole holding.
        /// </summary>
        public static bool IsConnectedToCapital(WorldRegionData region, IReadOnlyCollection<WorldRegionData> allRegions)
        {
            if (region == null || allRegions == null) return false;
            if (region.IsCapital) return true;

            string owner = region.OwnerUserId;
            if (string.IsNullOrEmpty(owner)) return false;

            // Index the owner's regions by hex so neighbour lookups are a dictionary hit rather than
            // a scan of every region in the world for each step.
            var owned = new Dictionary<HexCoord, WorldRegionData>();
            bool ownerHasCapital = false;
            foreach (var candidate in allRegions)
            {
                if (candidate == null || !candidate.IsOwnedByPlayer(owner)) continue;
                owned[candidate.Hex] = candidate;
                if (candidate.IsCapital) ownerHasCapital = true;
            }

            // A player with no capital at all has nothing to be supplied from. Treating that as
            // "supplied" would quietly exempt a player who has lost their seat from the whole rule.
            if (!ownerHasCapital) return false;

            var seen = new HashSet<HexCoord> { region.Hex };
            var queue = new Queue<HexCoord>();
            queue.Enqueue(region.Hex);

            while (queue.Count > 0)
            {
                var hex = queue.Dequeue();

                foreach (var neighbour in hex.Neighbours())
                {
                    if (!seen.Add(neighbour)) continue;
                    if (!owned.TryGetValue(neighbour, out var next)) continue;
                    if (next.IsCapital) return true;

                    queue.Enqueue(neighbour);
                }
            }

            return false;
        }

        /// <summary>
        /// Supply for every one of a player's regions at once. Cheaper than asking region by region,
        /// which re-walks the same holding each time — the map's dossier and the world overview both
        /// want the whole picture.
        /// </summary>
        public static Dictionary<string, double> SupplyForAll(string ownerUserId, IReadOnlyCollection<WorldRegionData> allRegions)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(ownerUserId) || allRegions == null) return result;

            var owned = new Dictionary<HexCoord, WorldRegionData>();
            var capitals = new List<HexCoord>();
            foreach (var region in allRegions)
            {
                if (region == null || !region.IsOwnedByPlayer(ownerUserId)) continue;
                owned[region.Hex] = region;
                if (region.IsCapital) capitals.Add(region.Hex);
            }

            // One walk outward from the capital marks everything it can still reach.
            var reachable = new HashSet<HexCoord>();
            var queue = new Queue<HexCoord>();
            foreach (var capital in capitals)
            {
                if (reachable.Add(capital))
                    queue.Enqueue(capital);
            }

            while (queue.Count > 0)
            {
                foreach (var neighbour in queue.Dequeue().Neighbours())
                {
                    if (!owned.ContainsKey(neighbour)) continue;
                    if (reachable.Add(neighbour))
                        queue.Enqueue(neighbour);
                }
            }

            foreach (var pair in owned)
                result[pair.Value.RegionId] = reachable.Contains(pair.Key) ? Supplied : CutOff;

            return result;
        }
    }
}
