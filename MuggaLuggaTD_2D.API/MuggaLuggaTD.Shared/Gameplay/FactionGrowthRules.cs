using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// A faction growing without a fight (<c>docs/design/npc-factions.md</c> §4, phase 4): it
    /// <b>expands</b> into wild land on its border, or <b>fortifies</b> its own.
    ///
    /// <para><b>Expand.</b> Only into land nobody holds, of tier <see cref="ExpandMinimumTier"/> or
    /// more, and never on a capital's doorstep: the same ground world generation seats factions on.
    /// That keeps the gentle country round the seats, and the land a new player is seated on, for
    /// players. It costs <see cref="ExpandShare"/> of the faction's strength, and the land it takes
    /// raises its cap, so a faction that grows comes back stronger.</para>
    ///
    /// <para><b>Fortify.</b> On its own land, the most threatened region first: frontier before
    /// heartland, then the most worn, then the least walled. Its walls rise a level and its people
    /// steady (<see cref="FortifyResolveBonus"/> resolve). Nobody else tends a faction's land, so this
    /// is also how a faction recovers from being raided. It costs <see cref="FortifyShare"/> of its
    /// strength and is done at once: a faction pays in men, not goods, and has no works to wait on.</para>
    /// </summary>
    public static class FactionGrowthRules
    {
        /// <summary>Expanding costs this share of strength (tune).</summary>
        public const double ExpandShare = 0.2;

        /// <summary>Fortifying costs this share of strength (tune).</summary>
        public const double FortifyShare = 0.15;

        /// <summary>A faction expands only into land of this tier or harder, as generation seats it.</summary>
        public const int ExpandMinimumTier = 3;

        /// <summary>Resolve a fortified region regains: as much as a siege held.</summary>
        public const int FortifyResolveBonus = 15;

        /// <summary>What expanding costs.</summary>
        public static double ExpandCost(double strength) => Math.Max(0, strength) * ExpandShare;

        /// <summary>What fortifying costs.</summary>
        public static double FortifyCost(double strength) => Math.Max(0, strength) * FortifyShare;

        /// <summary>
        /// Whether a faction may claim this region: nobody holds it, it is hard country, and it does
        /// not touch a capital.
        /// </summary>
        public static bool IsExpandable(WorldRegionData region, IReadOnlyCollection<WorldRegionData> regions)
        {
            if (region == null) return false;
            if (region.Ownership != LocationOwnership.Neutral || !string.IsNullOrEmpty(region.OwnerUserId)) return false;
            if (FactionStrengthRules.IsFaction(region.Faction) || region.IsCapital) return false;
            if (region.Tier < ExpandMinimumTier) return false;
            return !BordersCapital(region, regions);
        }

        /// <summary>
        /// A bordering region the faction may expand into, chosen by a roll in [0, 1). Null when its
        /// border holds no wild hard country.
        /// </summary>
        public static WorldRegionData PickExpansion(FactionId faction, IReadOnlyCollection<WorldRegionData> regions, double roll)
        {
            var open = new List<WorldRegionData>();
            foreach (var region in FactionDecisionRules.Bordering(faction, regions))
                if (IsExpandable(region, regions)) open.Add(region);
            if (open.Count == 0) return null;

            int at = (int)(Math.Max(0, Math.Min(0.999999, roll)) * open.Count);
            return open[at];
        }

        /// <summary>Whether fortifying would change anything here: walls to raise or resolve to restore.</summary>
        public static bool NeedsWork(WorldRegionData region) =>
            region != null && (region.Entrenchment < FortifyRules.MaxLevel || region.Resolve < RegionResolveRules.Maximum);

        /// <summary>
        /// The faction's region most in need of fortifying, or null when every one is whole or under
        /// siege (<paramref name="besieged"/>; nobody builds with an army at the gate). Frontier
        /// before heartland, then the most worn, then the least walled, then the richer.
        /// </summary>
        public static WorldRegionData PickFortify(FactionId faction, IReadOnlyCollection<WorldRegionData> regions,
            ICollection<string> besieged = null)
        {
            var byHex = new Dictionary<HexCoord, WorldRegionData>();
            foreach (var r in regions) byHex[r.Hex] = r;

            WorldRegionData best = null;
            bool bestFrontier = false;
            foreach (var region in regions)
            {
                if (!FactionStrengthRules.Holds(faction, region) || !NeedsWork(region)) continue;
                if (besieged != null && besieged.Contains(region.RegionId)) continue;

                bool frontier = IsFrontier(faction, region, byHex);
                if (best == null || Before(region, frontier, best, bestFrontier))
                {
                    best = region;
                    bestFrontier = frontier;
                }
            }
            return best;
        }

        /// <summary>The region's entrenchment and resolve once fortified.</summary>
        public static (int Entrenchment, int Resolve) Fortified(WorldRegionData region) => (
            Math.Min(FortifyRules.MaxLevel, region.Entrenchment + 1),
            Math.Min(RegionResolveRules.Maximum, region.Resolve + FortifyResolveBonus));

        /// <summary>Whether a region the faction holds touches land it does not.</summary>
        private static bool IsFrontier(FactionId faction, WorldRegionData region, Dictionary<HexCoord, WorldRegionData> byHex)
        {
            foreach (var hex in region.Hex.Neighbours())
                if (byHex.TryGetValue(hex, out var next) && !FactionStrengthRules.Holds(faction, next))
                    return true;
            return false;
        }

        private static bool Before(WorldRegionData a, bool aFrontier, WorldRegionData b, bool bFrontier)
        {
            if (aFrontier != bFrontier) return aFrontier;
            if (a.Resolve != b.Resolve) return a.Resolve < b.Resolve;
            if (a.Entrenchment != b.Entrenchment) return a.Entrenchment < b.Entrenchment;
            if (a.Tier != b.Tier) return a.Tier > b.Tier;
            return string.CompareOrdinal(a.RegionId, b.RegionId) < 0;
        }

        private static bool BordersCapital(WorldRegionData region, IReadOnlyCollection<WorldRegionData> regions)
        {
            foreach (var other in regions)
                if (other.IsCapital && HexCoord.Distance(other.Hex, region.Hex) == 1)
                    return true;
            return false;
        }
    }
}
