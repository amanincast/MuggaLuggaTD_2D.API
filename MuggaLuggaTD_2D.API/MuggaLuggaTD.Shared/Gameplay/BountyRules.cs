using System;
using System.Collections.Generic;
using System.Linq;
using Enums;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// A wanted champion (Active Content A): who it is, where it walks, and what it fights with. A pure
    /// function of the realm, its slot and the world's map, so the server and the client agree on it
    /// without storing it. Only a claim (and a debug pin) is stored.
    /// </summary>
    public sealed class BountyChampion
    {
        /// <summary>Its slot: the 12-hour window it appeared in (<see cref="BountyRules.SlotOf"/>).</summary>
        public long Slot;

        /// <summary>Its people ("Goblin"): drawn from the land it first appeared in.</summary>
        public string People;

        /// <summary>The biome it came from, whose roster escorts it wherever it walks.</summary>
        public BiomeType Biome;

        /// <summary>"Grukk Ashmaw".</summary>
        public string Name;

        /// <summary>"Goblin Warlord".</summary>
        public string Title;

        /// <summary>Its two affixes, from the rift mutator list.</summary>
        public List<RiftMutator> Mutators = new List<RiftMutator>();

        /// <summary>The regions it walks through, one per <see cref="BountyRules.StepEvery"/>; the first is where it appeared.</summary>
        public List<string> Path = new List<string>();
    }

    /// <summary>
    /// Bounties: wanted champions (Active Content spec, A; Mike 2026-10-08: "make the named champions
    /// extra difficult"). Two a day, at 00:00 and 12:00 UTC. Each walks a seeded path over the realm's
    /// land for 36 hours, then escapes. The first player to beat one claims it.
    /// </summary>
    public static class BountyRules
    {
        // -----------------------------------------------------------------
        // When
        // -----------------------------------------------------------------

        /// <summary>A new champion every 12 hours: two a day.</summary>
        public static readonly TimeSpan SlotLength = TimeSpan.FromHours(12);

        /// <summary>How long a champion roams unclaimed before it escapes.</summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(36);

        /// <summary>How long it stands in one region before it moves on to a neighbour.</summary>
        public static readonly TimeSpan StepEvery = TimeSpan.FromHours(6);

        /// <summary>One attempt per champion per player in this long: lose, and wait.</summary>
        public static readonly TimeSpan AttemptEvery = TimeSpan.FromHours(2);

        /// <summary>How many regions its path has: one per step of its lifetime.</summary>
        public static int PathLength => (int)Math.Ceiling(Lifetime.TotalHours / StepEvery.TotalHours);

        /// <summary>The slot <paramref name="utc"/> falls in.</summary>
        public static long SlotOf(DateTime utc) => (long)Math.Floor((utc - DateTime.UnixEpoch).TotalHours / SlotLength.TotalHours);

        public static DateTime SlotStart(long slot) => DateTime.UnixEpoch.AddHours(slot * SlotLength.TotalHours);

        public static DateTime EscapesAt(long slot) => SlotStart(slot) + Lifetime;

        /// <summary>
        /// The slots whose champions roam at <paramref name="now"/>, oldest first: those that appeared at or
        /// after the season began, and have not yet escaped.
        /// </summary>
        public static List<long> LiveSlots(DateTime now, DateTime seasonStart)
        {
            var slots = new List<long>();
            long newest = SlotOf(now);
            int back = (int)Math.Ceiling(Lifetime.TotalHours / SlotLength.TotalHours);
            for (long slot = newest - back; slot <= newest; slot++)
            {
                if (SlotStart(slot) < seasonStart) continue;
                if (now >= SlotStart(slot) && now < EscapesAt(slot)) slots.Add(slot);
            }
            return slots;
        }

        public static bool IsLive(long slot, DateTime now, DateTime seasonStart) =>
            SlotStart(slot) >= seasonStart && now >= SlotStart(slot) && now < EscapesAt(slot);

        /// <summary>Which step of its path a champion is on at <paramref name="now"/>.</summary>
        public static int StepAt(long slot, DateTime now)
        {
            int step = (int)Math.Floor((now - SlotStart(slot)).TotalHours / StepEvery.TotalHours);
            return Math.Max(0, Math.Min(PathLength - 1, step));
        }

        /// <summary>When it moves on from where it stands at <paramref name="now"/> (or escapes, on its last step).</summary>
        public static DateTime MovesAt(long slot, DateTime now)
        {
            var next = SlotStart(slot) + TimeSpan.FromTicks(StepEvery.Ticks * (StepAt(slot, now) + 1));
            return next < EscapesAt(slot) ? next : EscapesAt(slot);
        }

        // -----------------------------------------------------------------
        // The fight's id
        // -----------------------------------------------------------------

        public const string SitePrefix = "bounty:";

        public static string SiteId(long slot) => $"{SitePrefix}{slot}";

        public static bool IsSiteId(string siteId) =>
            !string.IsNullOrEmpty(siteId) && siteId.StartsWith(SitePrefix, StringComparison.Ordinal);

        public static bool TryParseSiteId(string siteId, out long slot)
        {
            slot = 0;
            return IsSiteId(siteId) && long.TryParse(siteId.Substring(SitePrefix.Length), out slot);
        }

        /// <summary>A champion's fight is paid as a portal clear, like a rift's.</summary>
        public const LocationType SiteType = LocationType.Portal;

        // -----------------------------------------------------------------
        // Who, and where it walks
        // -----------------------------------------------------------------

        /// <summary>It first appears within this many rings of the map's centre (anywhere, on a map too small).</summary>
        public const int StartRing = 3;

        private static readonly string[] Titles =
            { "Warlord", "Chieftain", "Butcher", "Tyrant", "Marauder", "Dreadlord", "Headtaker", "Scourge" };

        /// <summary>
        /// The champion of <paramref name="slot"/> in this realm, or null when the map has no land it may
        /// walk. <paramref name="peoples"/> are each biome's enemy peoples, from content.
        /// </summary>
        public static BountyChampion Champion(string realmId, long slot, IReadOnlyList<WorldRegionData> regions,
            IReadOnlyDictionary<BiomeType, IReadOnlyList<string>> peoples)
        {
            var path = Path(realmId, slot, regions);
            if (path.Count == 0) return null;

            var start = regions.First(r => r.RegionId == path[0]);
            var random = RandomFor(realmId, slot, "who");
            IReadOnlyList<string> folk = null;
            if (peoples != null) peoples.TryGetValue(start.Biome, out folk);
            string people = folk != null && folk.Count > 0 ? folk[random.Next(folk.Count)] : "Bandit";

            ulong seed = Naming.Hash($"{(realmId ?? "").ToLowerInvariant()}:bounty:{slot}");
            var pool = RiftRules.RolledMutators.ToList();
            var shuffle = RandomFor(realmId, slot, "mutators");
            shuffle.Shuffle(pool);

            return new BountyChampion
            {
                Slot = slot,
                People = people,
                Biome = start.Biome,
                Name = Naming.ForBoss($"Enemy_{start.Biome}_{people}_Boss", start.Biome, seed),
                Title = $"{people} {Titles[random.Next(Titles.Length)]}",
                Mutators = pool.Take(MutatorCount).ToList(),
                Path = path,
            };
        }

        /// <summary>
        /// Where a champion walks: it appears near the map's middle and steps to a neighbouring region
        /// every <see cref="StepEvery"/>, never onto a capital, and back the way it came only when there
        /// is nowhere else. Capitals and the map never change in a season, so neither does the path.
        /// </summary>
        public static List<string> Path(string realmId, long slot, IReadOnlyList<WorldRegionData> regions)
        {
            var path = new List<string>();
            var land = (regions ?? Array.Empty<WorldRegionData>())
                .Where(r => r != null && !r.IsCapital)
                .OrderBy(r => r.RegionId, StringComparer.Ordinal)
                .ToList();
            if (land.Count == 0) return path;

            var middle = land.Where(r => HexCoord.Distance(new HexCoord(0, 0), r.Hex) <= StartRing).ToList();
            var pool = middle.Count > 0 ? middle : land;
            var random = RandomFor(realmId, slot, "walk");
            var here = pool[random.Next(pool.Count)];
            WorldRegionData previous = null;
            path.Add(here.RegionId);

            var byHex = land.ToDictionary(r => r.Hex);
            while (path.Count < PathLength)
            {
                var next = here.Hex.Neighbours().Where(byHex.ContainsKey).Select(h => byHex[h])
                    .OrderBy(r => r.RegionId, StringComparer.Ordinal).ToList();
                var onward = next.Where(r => r != previous).ToList();
                var choices = onward.Count > 0 ? onward : next;
                if (choices.Count > 0)
                {
                    previous = here;
                    here = choices[random.Next(choices.Count)];
                }
                path.Add(here.RegionId);
            }
            return path;
        }

        /// <summary>Where the champion stands at <paramref name="now"/>.</summary>
        public static string RegionAt(BountyChampion champion, DateTime now) =>
            champion == null || champion.Path.Count == 0 ? null : champion.Path[Math.Min(champion.Path.Count - 1, StepAt(champion.Slot, now))];

        // -----------------------------------------------------------------
        // Its teeth (Mike: "extra difficult")
        // -----------------------------------------------------------------

        /// <summary>A champion fights a tier above the land it stands in...</summary>
        public const int TierBonus = 1;

        /// <summary>...and never below this tier, wherever it wanders.</summary>
        public const int LeastTier = 3;

        /// <summary>And this many levels above a site of its tier.</summary>
        public const int LevelBonus = 2;

        public const int MutatorCount = 2;

        /// <summary>On top of a boss's own health and damage (<see cref="BossRules"/>).</summary>
        public const float HealthMultiplier = 3f;
        public const float DamageMultiplier = 1.3f;

        /// <summary>Drawn larger than its people, with a red rim.</summary>
        public const float Scale = 1.15f;

        public static int TierIn(int regionTier) =>
            Math.Min(RiftRules.HighestTier, Math.Max(LeastTier, regionTier + TierBonus));

        public static int LevelOf(int tier) => RiftRules.SiteLevel(tier) + LevelBonus;

        // -----------------------------------------------------------------
        // The bounty
        // -----------------------------------------------------------------

        /// <summary>The bounty is this many times the gold of a clear of the champion's level and tier.</summary>
        public const double GoldMultiplier = 3.0;

        /// <summary>A later winner (one who went in before it was claimed) gets this share of the gold, and no chest.</summary>
        public const double ConsolationShare = 0.5;

        /// <summary>Season points for the claim (<see cref="SeasonDeed.BountyClaimed"/>), counted as clearing.</summary>
        public const double SeasonPoints = 80.0;

        /// <summary>The chest is Rare, and one in this many is Legendary.</summary>
        public const int LegendaryOneIn = 5;

        public static long Bounty(long clearGold) => clearGold <= 0 ? 0 : (long)Math.Round(clearGold * GoldMultiplier);

        public static long Consolation(long bounty) => bounty <= 0 ? 0 : (long)Math.Round(bounty * ConsolationShare);

        public static ItemRarityTypes ChestRarity(Random random) =>
            random != null && random.Next(LegendaryOneIn) == 0 ? ItemRarityTypes.Legendary : ItemRarityTypes.Rare;

        private static DeterministicRandom RandomFor(string realmId, long slot, string purpose) =>
            DeterministicRandom.ForSubject(
                Naming.Hash((realmId ?? "").ToLowerInvariant()),
                Naming.Hash($"bounty:{slot}:{purpose}"));
    }
}
