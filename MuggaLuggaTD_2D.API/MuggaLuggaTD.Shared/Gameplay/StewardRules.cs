using System;
using System.Collections.Generic;
using System.Linq;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// Claiming wild land (Reclaiming phase 2a-ii, Mike 2026-11-12). Cleansing opens a region to everyone; claiming its
    /// keep is a second step, and it is braked two ways:
    /// <list type="bullet">
    /// <item><b>The border</b> (row P2-9): a cleansed region can be claimed only if it touches land you hold.</item>
    /// <item><b>A steward</b> (row P2-10): wild land claimed beyond your seat's ring needs a hero strong enough to
    /// oversee it, stationed at its keep. Without one it is <b>unruly</b>: it earns no gold or points, and you can't
    /// claim more wild land until every region you hold has its steward. The regions touching your seat need none
    /// (Mike: "a 1-hex aura of overseeing"). Heroes only.</item>
    /// </list>
    /// </summary>
    public static class StewardRules
    {
        /// <summary>The seat oversees the regions this many hexes from it: they need no steward.</summary>
        public const int SeatAura = 1;

        /// <summary>A steward stands this many levels under the region's warlord at most.</summary>
        public const int LevelsUnderWarlord = 2;

        /// <summary>The level a steward needs for land of this tier: 4, 9, 14 or 19.</summary>
        public static int LevelFor(int tier) => HordeRules.WildBaseLevel(tier) + HordeRules.LairLevelsAbove - LevelsUnderWarlord;

        /// <summary>The player's seat, or null.</summary>
        public static WorldRegionData SeatOf(string userId, IEnumerable<WorldRegionData> regions) =>
            string.IsNullOrEmpty(userId) || regions == null ? null
                : regions.FirstOrDefault(r => r != null && r.IsCapital && r.IsOwnedByPlayer(userId));

        /// <summary>Whether land claimed by <paramref name="userId"/> here would need a steward: beyond the seat's ring.</summary>
        public static bool WouldNeedSteward(WorldRegionData region, string userId, IEnumerable<WorldRegionData> regions)
        {
            if (region == null || region.IsCapital) return false;
            var seat = SeatOf(userId, regions);
            return seat == null || HexCoord.Distance(seat.Hex, region.Hex) > SeatAura;
        }

        /// <summary>
        /// A steward stands there: the hero appointed is still in a garrison of the region (a garrison stands only at a
        /// keep or an outpost, and the appointment is made at the keep).
        /// </summary>
        public static bool HasSteward(WorldRegionData region) =>
            region != null && !string.IsNullOrEmpty(region.StewardId) && region.SiteOverrides != null &&
            region.SiteOverrides.Values.Any(o => o?.GarrisonCharacterIds != null && o.GarrisonCharacterIds.Contains(region.StewardId));

        /// <summary>Held, in need of a steward, and without one.</summary>
        public static bool IsUnruly(WorldRegionData region) =>
            region != null && region.StewardNeeded && region.Ownership == LocationOwnership.Player && !HasSteward(region);

        /// <summary>The first of this player's regions that is unruly, or null.</summary>
        public static WorldRegionData FirstUnruly(string userId, IEnumerable<WorldRegionData> regions) =>
            regions?.FirstOrDefault(r => r != null && r.IsOwnedByPlayer(userId) && IsUnruly(r));

        /// <summary>Whether the region touches land this player holds.</summary>
        public static bool Borders(WorldRegionData region, string userId, IEnumerable<WorldRegionData> regions) =>
            region != null && regions != null &&
            regions.Any(r => r != null && r.IsOwnedByPlayer(userId) && HexCoord.Distance(r.Hex, region.Hex) == 1);

        /// <summary>Whether claiming this region's keep is a wild claim, braked by these rules: cleansed land, nobody's.</summary>
        public static bool IsWildClaim(WorldRegionData region) =>
            HordeRules.IsCleansed(region) && region.Ownership == LocationOwnership.Neutral && string.IsNullOrEmpty(region.OwnerUserId);

        public const string NotBordering = "Wild land is claimed from your border. Take land that touches your own first.";

        /// <summary>
        /// Why this player can't claim the region now, or null when they can (or it is not a wild claim).
        /// <paramref name="bestFreeHeroLevel"/> is the highest level among their heroes not stationed, held or in a siege;
        /// null skips the check (a reader who doesn't know the roster).
        /// </summary>
        public static string WhyNotClaim(WorldRegionData region, string userId, IReadOnlyCollection<WorldRegionData> regions,
            int? bestFreeHeroLevel = null)
        {
            if (!IsWildClaim(region)) return null;
            if (!Borders(region, userId, regions)) return NotBordering;
            var unruly = FirstUnruly(userId, regions);
            if (unruly != null)
                return $"{Name(unruly)} has no steward. Station a hero of level {LevelFor(unruly.Tier)} at its keep before you claim more wild land.";
            if (bestFreeHeroLevel.HasValue && WouldNeedSteward(region, userId, regions) && bestFreeHeroLevel.Value < LevelFor(region.Tier))
                return $"This land needs a steward: a hero of level {LevelFor(region.Tier)}, free to stand at its keep. You have none.";
            return null;
        }

        /// <summary>
        /// The steward a garrison gives the region: its highest hero at <see cref="LevelFor"/> or above, or null.
        /// <paramref name="levelOf"/> reads a hero's level.
        /// </summary>
        public static string ChooseSteward(WorldRegionData region, IEnumerable<string> garrison, Func<string, int> levelOf)
        {
            if (region == null || garrison == null || levelOf == null) return null;
            int needed = LevelFor(region.Tier);
            return garrison.Where(id => !string.IsNullOrEmpty(id))
                .Select(id => (Id: id, Level: levelOf(id)))
                .Where(h => h.Level >= needed)
                .OrderByDescending(h => h.Level).ThenBy(h => h.Id, StringComparer.Ordinal)
                .Select(h => h.Id)
                .FirstOrDefault();
        }

        private static string Name(WorldRegionData region)
        {
            string name = region == null ? null : Naming.RegionName(region);
            return string.IsNullOrEmpty(name) ? "One of your regions" : name;
        }
    }
}
