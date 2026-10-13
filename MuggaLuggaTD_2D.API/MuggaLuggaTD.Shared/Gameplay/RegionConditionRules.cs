using System;
using System.Collections.Generic;
using System.Linq;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>What an hour has brought to a region (Active Content C, "Fairs and storms").</summary>
    public enum RegionCondition
    {
        None = 0,

        /// <summary>The region's workers bring in more goods.</summary>
        HarvestFair = 1,

        /// <summary>Warbands are out: ambushes in the region come more often and pay more.</summary>
        HuntingSeason = 2,

        /// <summary>Foul weather: travel through the region is slower, and an ambush beaten there counts twice for a quest.</summary>
        Storm = 3,

        /// <summary>The caves and ruins are crawling: more enemies in them, and their clears pay more.</summary>
        PlagueOfRats = 4,

        /// <summary>Pilgrims on the roads: both of the region's wandering quest-givers are out.</summary>
        Pilgrimage = 5,
    }

    /// <summary>
    /// Fairs and storms (Active Content C; Mike 2026-10-10: hourly, not daily). Each hour, some
    /// player-held regions carry a condition that changes where it pays to go. Each held region has its
    /// own chance each hour, so a player with three regions sees a fair on one of them now and then,
    /// not always; and the realm as a whole has at most <see cref="MostFairs"/> fairs and one Hunting
    /// Season in an hour.
    ///
    /// <para>Nothing is stored or rolled at random, as with <see cref="HarassmentRules"/>: the conditions
    /// are a function of the realm, the hour and which regions players hold, so the server's settle and
    /// the client's map always agree. A settle over past hours asks with today's holders: an hour a
    /// region changed hands may be read as the new holder's.</para>
    /// </summary>
    public static class RegionConditionRules
    {
        /// <summary>A Harvest Fair's goods, against an ordinary hour's (tune).</summary>
        public const double FairGoodsFactor = 1.5;

        /// <summary>Hunting Season's chance of an ambush on the region's road, against an ordinary hour's (tune).</summary>
        public const double HuntChanceFactor = 1.5;

        /// <summary>What an ambush won in Hunting Season pays, against an ordinary one (tune).</summary>
        public const double HuntRewardFactor = 1.5;

        /// <summary>A held region's chance of a Harvest Fair in an hour, in ten thousand: about 2 hours a day (tune).</summary>
        public const int FairChanceOutOfTenThousand = 833;

        /// <summary>A held region's chance of Hunting Season in an hour, in ten thousand: about 1.5 hours a day (tune).</summary>
        public const int HuntChanceOutOfTenThousand = 625;

        /// <summary>At most this many fairs in the realm in an hour (tune).</summary>
        public const int MostFairs = 2;

        /// <summary>A fair region's villages' Gather quests pay this much gold, against an ordinary hour's (tune).</summary>
        public const double FairGatherGoldFactor = 1.5;

        /// <summary>A storm's journeys: the walk through the region takes this long, against fair weather's (tune).</summary>
        public const double StormTravelFactor = 1.5;

        /// <summary>An ambush beaten in a storm counts this many times toward an Ambush quest (tune).</summary>
        public const int StormAmbushQuestCount = 2;

        /// <summary>A plague's caves and ruins hold this many enemies, against an ordinary hour's (tune).</summary>
        public const double RatsEnemyFactor = 1.25;

        /// <summary>A cave or ruin cleared in a plague pays this much, against an ordinary hour's (tune).</summary>
        public const double RatsPayFactor = 1.25;

        /// <summary>
        /// A held region's chance of a Storm, a Plague of Rats and a Pilgrimage in an hour, in ten thousand: each about
        /// 1.5 hours a day, and at most one of each in the realm (tune).
        /// </summary>
        public const int StormChanceOutOfTenThousand = 625;
        public const int RatsChanceOutOfTenThousand = 625;
        public const int PilgrimageChanceOutOfTenThousand = 625;

        /// <summary>
        /// The hour's conditions, by region id; a region not in it has none. <paramref name="heldRegionIds"/>
        /// are the regions players hold, in any order. Each rolls its own chance of a fair, and the lowest
        /// rolls win when more than <see cref="MostFairs"/> come up. Then the same for one Hunting Season,
        /// in a region with no fair; then one Storm, one Plague of Rats and one Pilgrimage (phase 2), each in a
        /// region with nothing yet. The later picks never move an earlier one, so adding them changed no fair or hunt.
        /// </summary>
        public static Dictionary<string, RegionCondition> For(string realmId, long hour, IEnumerable<string> heldRegionIds)
        {
            var result = new Dictionary<string, RegionCondition>(StringComparer.Ordinal);
            if (heldRegionIds == null) return result;

            string realm = (realmId ?? "").ToLowerInvariant();
            var held = heldRegionIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();

            ulong Roll(string id, string what) => Naming.Hash($"{realm}:{hour}:{id}:{what}") % 10000UL;

            foreach (var id in held
                         .Select(id => (Id: id, Roll: Roll(id, "fair")))
                         .Where(r => r.Roll < FairChanceOutOfTenThousand)
                         .OrderBy(r => r.Roll).ThenBy(r => r.Id, StringComparer.Ordinal)
                         .Take(MostFairs))
                result[id.Id] = RegionCondition.HarvestFair;

            var hunt = held
                .Where(id => !result.ContainsKey(id))
                .Select(id => (Id: id, Roll: Roll(id, "hunt")))
                .Where(r => r.Roll < HuntChanceOutOfTenThousand)
                .OrderBy(r => r.Roll).ThenBy(r => r.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (hunt.Id != null) result[hunt.Id] = RegionCondition.HuntingSeason;

            foreach (var (condition, what, chance) in new[]
                     {
                         (RegionCondition.Storm, "storm", StormChanceOutOfTenThousand),
                         (RegionCondition.PlagueOfRats, "rats", RatsChanceOutOfTenThousand),
                         (RegionCondition.Pilgrimage, "pilgrims", PilgrimageChanceOutOfTenThousand),
                     })
            {
                var pick = held
                    .Where(id => !result.ContainsKey(id))
                    .Select(id => (Id: id, Roll: Roll(id, what)))
                    .Where(r => r.Roll < (ulong)chance)
                    .OrderBy(r => r.Roll).ThenBy(r => r.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (pick.Id != null) result[pick.Id] = condition;
            }
            return result;
        }

        /// <summary>As <see cref="For(string,long,IEnumerable{string})"/>, the regions players hold read from the world.</summary>
        public static Dictionary<string, RegionCondition> For(string realmId, long hour, IEnumerable<WorldRegionData> regions)
            => For(realmId, hour, HeldIds(regions));

        /// <summary>One region's condition in an hour.</summary>
        public static RegionCondition Of(string realmId, long hour, string regionId, IEnumerable<string> heldRegionIds)
        {
            if (string.IsNullOrEmpty(regionId)) return RegionCondition.None;
            return For(realmId, hour, heldRegionIds).TryGetValue(regionId, out var condition) ? condition : RegionCondition.None;
        }

        /// <summary>The regions players hold: the only ones a condition can fall on.</summary>
        public static List<string> HeldIds(IEnumerable<WorldRegionData> regions)
            => regions == null
                ? new List<string>()
                : regions.Where(r => r != null && r.Ownership == LocationOwnership.Player && !string.IsNullOrEmpty(r.OwnerUserId))
                    .Select(r => r.RegionId).ToList();

        /// <summary>
        /// For <see cref="WorkerLevelRules.Gathered"/>: the factor on a region's goods in each hour, 1 for an
        /// ordinary hour. Remembers each hour it has worked out, as a settle asks once per worker.
        /// </summary>
        public static Func<long, double> GoodsFactor(string realmId, string regionId, IEnumerable<string> heldRegionIds)
        {
            var held = heldRegionIds?.ToList() ?? new List<string>();
            if (held.Count == 0 || !held.Contains(regionId)) return null;
            var seen = new Dictionary<long, double>();
            return hour =>
            {
                if (seen.TryGetValue(hour, out double factor)) return factor;
                factor = Of(realmId, hour, regionId, held) == RegionCondition.HarvestFair ? FairGoodsFactor : 1.0;
                seen[hour] = factor;
                return factor;
            };
        }

        /// <summary>A region's condition at an instant, the regions players hold read from the world.</summary>
        public static RegionCondition At(string realmId, string regionId, DateTime at, IEnumerable<WorldRegionData> regions)
            => Of(realmId, HarassmentRules.HourOf(at), regionId, HeldIds(regions));

        /// <summary>The factor on a road's chance of an ambush in a region that hour.</summary>
        public static double AmbushChanceFactor(RegionCondition condition)
            => condition == RegionCondition.HuntingSeason ? HuntChanceFactor : 1.0;

        /// <summary>The factor on what an ambush won in a region pays that hour.</summary>
        public static double AmbushRewardFactor(RegionCondition condition)
            => condition == RegionCondition.HuntingSeason ? HuntRewardFactor : 1.0;

        /// <summary>The factor on the walk through a region for a journey setting out that hour.</summary>
        public static double TravelFactor(RegionCondition condition)
            => condition == RegionCondition.Storm ? StormTravelFactor : 1.0;

        /// <summary>
        /// For <see cref="TravelRules.PlanRoute(Func{HexCoord, WorldRegionData}, WorldRegionData, GridCell, WorldRegionData, GridCell, Func{string, double})"/>:
        /// each region's walk factor in an hour, or null when no region on the map has a storm.
        /// </summary>
        public static Func<string, double> TravelFactors(IReadOnlyDictionary<string, RegionCondition> conditions)
        {
            if (conditions == null || !conditions.Values.Any(c => c == RegionCondition.Storm)) return null;
            return regionId => regionId != null && conditions.TryGetValue(regionId, out var c) ? TravelFactor(c) : 1.0;
        }

        /// <summary>How many times an ambush beaten in a region that hour counts toward an Ambush quest.</summary>
        public static int AmbushQuestCount(RegionCondition condition)
            => condition == RegionCondition.Storm ? StormAmbushQuestCount : 1;

        /// <summary>Whether a site is one a Plague of Rats fills: the caves and ruins, fought room by room.</summary>
        public static bool IsRatsSite(LocationType site) => site == LocationType.Dungeon || site == LocationType.Ruin;

        /// <summary>The factor on the enemies in a site that hour.</summary>
        public static double EnemyFactor(RegionCondition condition, LocationType site)
            => condition == RegionCondition.PlagueOfRats && IsRatsSite(site) ? RatsEnemyFactor : 1.0;

        /// <summary>The factor on what a site's clear pays (experience, gold, materials) for a run begun that hour.</summary>
        public static double ClearPayFactor(RegionCondition condition, LocationType site)
            => condition == RegionCondition.PlagueOfRats && IsRatsSite(site) ? RatsPayFactor : 1.0;

        /// <summary>The factor on a Gather quest's gold, offered by a giver in a region that hour.</summary>
        public static double GatherGoldFactor(RegionCondition condition)
            => condition == RegionCondition.HarvestFair ? FairGatherGoldFactor : 1.0;

        /// <summary>When the hour holding <paramref name="at"/> ends, and with it the conditions.</summary>
        public static DateTime HourEnds(DateTime at) => HarassmentRules.HourEnds(at);

        /// <summary>"Harvest Fair", "Hunting Season".</summary>
        public static string NameOf(RegionCondition condition) => condition switch
        {
            RegionCondition.HarvestFair => "Harvest Fair",
            RegionCondition.HuntingSeason => "Hunting Season",
            RegionCondition.Storm => "Storm",
            RegionCondition.PlagueOfRats => "Plague of Rats",
            RegionCondition.Pilgrimage => "Pilgrimage",
            _ => "",
        };

        /// <summary>What it does, in a line: "workers bring in +50% goods".</summary>
        public static string EffectOf(RegionCondition condition) => condition switch
        {
            RegionCondition.HarvestFair =>
                $"workers bring in +{(FairGoodsFactor - 1) * 100:0}% goods, and Gather quests pay +{(FairGatherGoldFactor - 1) * 100:0}% gold",
            RegionCondition.HuntingSeason => $"ambushes {HuntChanceFactor:0.#}× as likely, and pay {HuntRewardFactor:0.#}×",
            RegionCondition.Storm =>
                $"journeys through it take {StormTravelFactor:0.#}× as long, and an ambush beaten here counts twice for a quest",
            RegionCondition.PlagueOfRats =>
                $"caves and ruins hold +{(RatsEnemyFactor - 1) * 100:0}% enemies, and their clears pay +{(RatsPayFactor - 1) * 100:0}%",
            RegionCondition.Pilgrimage => "both of its wandering quest-givers are out",
            _ => "",
        };
    }
}
