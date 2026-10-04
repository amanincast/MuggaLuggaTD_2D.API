using System;
using System.Collections.Generic;
using System.Linq;
using Enums;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>What a company in auto mode has been told to do.</summary>
    public enum AutoOrder
    {
        /// <summary>Not in auto mode, or waiting for an order.</summary>
        None = 0,

        /// <summary>Fight the region's portals and dungeons in a rotation of its own.</summary>
        Roam = 1,

        /// <summary>Walk the region's roads and keep down its mobs, which makes its roads safer.</summary>
        Patrol = 2,
    }

    /// <summary>What a company in auto mode is doing now, as its card shows it.</summary>
    public enum AutoStatus
    {
        /// <summary>In auto mode with no order yet.</summary>
        Ready = 0,

        /// <summary>On the road to its next fight, or to the region it was ordered to.</summary>
        Walking = 1,

        /// <summary>In a fight at a site; it ends at the step's end.</summary>
        Fighting = 2,

        /// <summary>Walking a region's roads, keeping the mobs down.</summary>
        Patrolling = 3,

        /// <summary>Bloodied by a loss, resting until its members recover. It resumes by itself.</summary>
        Resting = 4,

        /// <summary>Stopped: the Grain (or a dungeon's Hides) ran out. It waits for its player's word.</summary>
        OutOfProvisions = 5,

        /// <summary>Waiting: nothing in its region is below its level, or it has just fought the only site.</summary>
        NothingToFight = 6,

        /// <summary>Stopped: the region it was ordered to is no longer held.</summary>
        RegionLost = 7,

        /// <summary>Stopped: none of its members is free to fight (garrisoned, held, or in a siege).</summary>
        NobodyFree = 8,
    }

    /// <summary>A site an auto company might fight next, as the server knows it.</summary>
    public readonly struct AutoFightCandidate
    {
        public AutoFightCandidate(string siteId, LocationType type, int level, TimeSpan road)
        {
            SiteId = siteId;
            Type = type;
            Level = level;
            Road = road;
        }

        public string SiteId { get; }
        public LocationType Type { get; }
        public int Level { get; }

        /// <summary>How long the walk there takes from where the company stands.</summary>
        public TimeSpan Road { get; }
    }

    /// <summary>
    /// Side companies that fight on their own (<c>docs/design/auto-fight.md</c>, Unity repo; Mike,
    /// 2026-10-04).
    ///
    /// <para><b>A company in auto mode fights only what is beneath it.</b> Its level is its members'
    /// average, rounded down, and it may only take on a site below that. The wider the gap, the better
    /// its chance, but nothing is ever certain. A loss costs nothing the player holds; the company is
    /// Bloodied instead (<see cref="BloodiedRules"/>).</para>
    ///
    /// <para><b>It pays a third.</b> Experience, gold and materials come at <see cref="RewardShare"/>
    /// of a hand-played clear, and gear one rarity down, so playing by hand always stays the better
    /// use of a player's time. It never pays the realm rewards (<see cref="SiteRotationRules"/>), and
    /// it never starts a lockout.</para>
    ///
    /// <para><b>It roams.</b> It never fights the same site twice in a row, and avoids its last
    /// <see cref="RecentSitesAvoided"/> when the region has enough, so it walks a loop of its own.</para>
    ///
    /// <para>Shared so the company card can show the odds and the next fight the server will pick.</para>
    /// </summary>
    public static class AutoFightRules
    {
        /// <summary>The share of a hand-played clear's experience, gold, items and materials an auto win pays.</summary>
        public const double RewardShare = 0.33;

        /// <summary>How many rarities below a hand drop an auto clear's gear comes.</summary>
        public const int RarityStepsDown = 1;

        /// <summary>The chance against a site one level below the company.</summary>
        public const double BaseChance = 0.55;

        /// <summary>Added for every further level the site is below the company.</summary>
        public const double ChancePerLevel = 0.08;

        /// <summary>No auto-fight is surer than this.</summary>
        public const double MaximumChance = 0.95;

        /// <summary>The sites it remembers having fought, and avoids while it has other choices.</summary>
        public const int RecentSitesAvoided = 3;

        /// <summary>How long a fight takes: a portal, a dungeon, and a dungeon that ends on a boss.</summary>
        public static readonly TimeSpan PortalFight = TimeSpan.FromMinutes(4);
        public static readonly TimeSpan DungeonFight = TimeSpan.FromMinutes(6);
        public static readonly TimeSpan BossFight = TimeSpan.FromMinutes(8);

        /// <summary>The most of a company's day a single settle replays, so a long absence is bounded.</summary>
        public static readonly TimeSpan MaximumReplay = TimeSpan.FromHours(48);

        /// <summary>A company's level: its members' average, rounded down. An empty company is level 0.</summary>
        public static int CompanyLevel(IEnumerable<int> memberLevels)
        {
            var levels = memberLevels?.ToList() ?? new List<int>();
            if (levels.Count == 0) return 0;
            return (int)Math.Floor(levels.Sum(l => (long)l) / (double)levels.Count);
        }

        /// <summary>
        /// The level of a region's mobs, which a patrol fights: its sites' average, as an ambush there
        /// is fought. A patrol is only ordered where this is below the company's level.
        /// </summary>
        public static int MobLevel(IEnumerable<SiteSpec> sites)
        {
            var levels = (sites ?? Enumerable.Empty<SiteSpec>()).Select(s => Math.Max(1, s.Level)).ToList();
            return levels.Count == 0 ? 1 : Math.Max(1, (int)Math.Round(levels.Average()));
        }

        /// <summary>Whether auto mode may fight this kind of site at all: portals and dungeons (caves are dungeons).</summary>
        public static bool IsFightable(LocationType type) =>
            type == LocationType.Portal || type == LocationType.Dungeon;

        /// <summary>Whether a company of this level may auto-fight a site of that one: only below it.</summary>
        public static bool CanFight(int companyLevel, int siteLevel) => siteLevel < companyLevel;

        /// <summary>The chance of winning; zero when the site is not below the company.</summary>
        public static double WinChance(int companyLevel, int siteLevel)
        {
            int gap = companyLevel - siteLevel;
            if (gap <= 0) return 0;
            return Math.Min(MaximumChance, BaseChance + ChancePerLevel * (gap - 1));
        }

        /// <summary>
        /// Rolls one fight. Seeded from the company and its fight count through
        /// <see cref="DeterministicRandom"/>, so settling the same stretch twice cannot re-roll it.
        /// </summary>
        public static bool RollWin(double chance, string companyId, long fightNumber)
        {
            var dice = DeterministicRandom.ForSubject(Naming.Hash(companyId), unchecked((ulong)fightNumber));
            return dice.Chance((int)Math.Round(chance * 10000));
        }

        /// <summary>How long a fight at this site takes.</summary>
        public static TimeSpan FightDuration(LocationType type, bool endsOnBoss) =>
            type == LocationType.Portal ? PortalFight
            : endsOnBoss ? BossFight
            : DungeonFight;

        /// <summary>
        /// The site a roaming company fights next, or null when there is nothing it may fight.
        /// <paramref name="recent"/> is the sites it has fought, most recent first.
        /// <list type="bullet">
        /// <item>Only fightable sites below the company's level.</item>
        /// <item>Never the site it just fought.</item>
        /// <item>Sites outside its last <see cref="RecentSitesAvoided"/> first, if there are any.</item>
        /// <item>Then the nearest by road, and of equals the one fought longest ago.</item>
        /// </list>
        /// </summary>
        public static string NextSite(IEnumerable<AutoFightCandidate> candidates, int companyLevel, IReadOnlyList<string> recent)
        {
            recent ??= Array.Empty<string>();
            string last = recent.Count > 0 ? recent[0] : null;

            var eligible = (candidates ?? Enumerable.Empty<AutoFightCandidate>())
                .Where(c => IsFightable(c.Type) && CanFight(companyLevel, c.Level) && c.SiteId != last)
                .ToList();
            if (eligible.Count == 0) return null;

            int Recency(string siteId)
            {
                int at = -1;
                for (int i = 0; i < recent.Count && i < RecentSitesAvoided; i++)
                    if (recent[i] == siteId) { at = i; break; }
                return at;
            }

            var fresh = eligible.Where(c => Recency(c.SiteId) < 0).ToList();
            var pool = fresh.Count > 0 ? fresh : eligible;

            return pool
                .OrderBy(c => c.Road)
                .ThenByDescending(c => Recency(c.SiteId) < 0 ? int.MaxValue : Recency(c.SiteId))
                .ThenBy(c => c.SiteId, StringComparer.Ordinal)
                .First().SiteId;
        }

        /// <summary>
        /// A share of a reward, rounded by chance rather than down, so a third of one is sometimes one
        /// and never always nothing.
        /// </summary>
        public static long Share(long amount, Random random)
        {
            double exact = amount * RewardShare;
            long whole = (long)Math.Floor(exact);
            if (random != null && random.NextDouble() < exact - whole) whole++;
            return whole;
        }

        /// <summary>A rarity, <see cref="RarityStepsDown"/> lower. Common stays Common.</summary>
        public static ItemRarityTypes StepDown(ItemRarityTypes rarity) =>
            (ItemRarityTypes)Math.Max((int)ItemRarityTypes.Common, (int)rarity - RarityStepsDown);
    }
}
