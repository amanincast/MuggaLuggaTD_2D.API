using System;
using System.Collections.Generic;
using System.Linq;
using Enums;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>The two kinds of rift (Invasions spec, revision R1; Mike 2026-10-10).</summary>
    public enum RiftKind
    {
        /// <summary>A realm event: everyone's wins wear its Breach down; unsealed, it erupts.</summary>
        Great = 0,

        /// <summary>A small tear in one player's region; one win seals it; unsealed, the region is invaded.</summary>
        Lesser = 1,
    }

    /// <summary>Where a rift is in its life.</summary>
    public enum RiftStage
    {
        /// <summary>Announced: it opens at <c>OpensAt</c>.</summary>
        Omen = 0,

        /// <summary>Open, and can be fought until <c>ClosesAt</c>.</summary>
        Open = 1,

        /// <summary>Closed by the players. Final.</summary>
        Sealed = 2,

        /// <summary>A Great Rift that was not sealed in time. Final.</summary>
        Erupted = 3,

        /// <summary>
        /// A Lesser Rift that was not sealed in time: its region is invaded until it is sealed or the
        /// season ends (Mike 2026-10-10, Q6). It can still be sealed.
        /// </summary>
        Invaded = 4,
    }

    /// <summary>
    /// A twist on a rift fight (Invasions spec §4.3). Each is a client-side combat rule; the server only
    /// names which ones a fight has. Appended only: the integers go over the wire.
    /// </summary>
    public enum RiftMutator
    {
        /// <summary>Riftborn burst on death: a ring telegraph, then damage to anyone in it.</summary>
        Unstable = 0,

        /// <summary>Enemies move 25% faster.</summary>
        Hasted = 1,

        /// <summary>One enemy in five splits in two on death.</summary>
        Splitting = 2,

        /// <summary>Elites carry a Ward that must be broken first.</summary>
        Warded = 3,

        /// <summary>Every few seconds void hands tug the hero toward the nearest Riftborn; a dodge slips them.</summary>
        Grasping = 4,

        /// <summary>The field is dark beyond a small light round the hero.</summary>
        Starless = 5,

        /// <summary>Each wave, the first Riftborn to die comes back once as a ghost.</summary>
        Echoing = 6,

        /// <summary>A gift: every Riftborn is weak to two elements, twice over, and immune to none.</summary>
        Fractured = 7,
    }

    /// <summary>What a Great Rift's eruption did (Invasions R8). Appended only: stored and sent as integers.</summary>
    public enum RiftEruption
    {
        /// <summary>Not erupted, or it found nothing to do.</summary>
        None = 0,

        /// <summary>Wild land: it became the Riftborn's at once.</summary>
        Seized = 1,

        /// <summary>Held land: the Riftborn laid siege to it, with an army sized by the Breach left.</summary>
        Besieged = 2,

        /// <summary>Past the cap on land eruptions give, or nothing to take: the Riftborn grew stronger instead.</summary>
        Surged = 3,
    }

    /// <summary>
    /// A Rift contract's two kinds (R7). <b>Sell:</b> the taker pays the lord for the right to close the rift.
    /// <b>Hire:</b> the lord pays the taker to close it. Either way the closer gets the chest.
    /// </summary>
    public enum RiftContractKind
    {
        Sell = 0,
        Hire = 1,
    }

    /// <summary>Where a Rift contract stands. Only <see cref="Posted"/> and <see cref="Taken"/> are live.</summary>
    public enum RiftContractStatus
    {
        /// <summary>Up for anyone in the realm to take.</summary>
        Posted = 0,

        /// <summary>Someone has taken it, and holds the right to close the rift until its hold runs out.</summary>
        Taken = 1,

        /// <summary>The holder closed the rift, and the gold went where the contract said.</summary>
        Fulfilled = 2,

        /// <summary>The lord took it down before anyone took it.</summary>
        Withdrawn = 3,

        /// <summary>It ended another way (the lord closed it, the land changed hands, the season ended): gold went back.</summary>
        Void = 4,
    }

    /// <summary>
    /// A region's invasion (R4): from when its Lesser Rift ran out to when it was sealed, or still going
    /// (<see cref="Until"/> null). The season's end clears it with the rest of the rifts.
    /// </summary>
    public sealed class Invasion
    {
        public string RiftId { get; set; }
        public string RegionId { get; set; }
        public DateTime From { get; set; }
        public DateTime? Until { get; set; }

        public bool Covers(DateTime at) => at >= From && (!Until.HasValue || at < Until.Value);

        /// <summary>Whether any of the hour <paramref name="hourIndex"/> (<c>HarassmentRules.HourOf</c>) fell inside it.</summary>
        public bool CoversHour(long hourIndex)
        {
            var start = new DateTime(hourIndex * TimeSpan.TicksPerHour, DateTimeKind.Utc);
            var end = start.AddHours(1);
            return end > From && (!Until.HasValue || start < Until.Value);
        }
    }

    /// <summary>
    /// Rifts from other realms (Invasions spec, revision R1–R7). When rifts come, where they open, how
    /// big a Great Rift's Breach is, and what sealing one pays. Every number here is a first pass
    /// (tune).
    ///
    /// <para>Times are random ("keep it random", Mike 2026-10-10) but not <c>System.Random</c>: each
    /// rift's gap and place are rolled from the realm and the rift's number, so a restart never re-rolls
    /// one already announced, and the server and its tests agree.</para>
    /// </summary>
    public static class RiftRules
    {
        // -----------------------------------------------------------------
        // Timing (R2)
        // -----------------------------------------------------------------

        /// <summary>A Great Rift's warning before it opens.</summary>
        public static readonly TimeSpan GreatOmenLead = TimeSpan.FromHours(1);

        /// <summary>How long a Great Rift stays open before it erupts.</summary>
        public static readonly TimeSpan GreatOpenFor = TimeSpan.FromHours(4);

        /// <summary>The gap between two Great Rifts opening is uniform in [min, max) minutes: 4 a day on average.</summary>
        public const int GreatGapMinMinutes = 3 * 60;
        public const int GreatGapMaxMinutes = 9 * 60;

        /// <summary>A Lesser Rift's warning before it opens.</summary>
        public static readonly TimeSpan LesserOmenLead = TimeSpan.FromMinutes(20);

        /// <summary>How long a Lesser Rift stays open before its region is invaded.</summary>
        public static readonly TimeSpan LesserOpenFor = TimeSpan.FromMinutes(90);

        /// <summary>The gap between two Lesser Rifts in a realm: about 6–8 a day.</summary>
        public const int LesserGapMinMinutes = 2 * 60;
        public const int LesserGapMaxMinutes = 4 * 60;

        /// <summary>No rift opens this soon after a season starts.</summary>
        public static readonly TimeSpan QuietAtSeasonStart = TimeSpan.FromHours(2);

        /// <summary>No rift opens this close to a season's end.</summary>
        public static readonly TimeSpan QuietAtSeasonEnd = TimeSpan.FromHours(6);

        /// <summary>A realm nobody has played for this long gets no rifts until someone comes back.</summary>
        public static readonly TimeSpan DeadRealmAfter = TimeSpan.FromHours(24);

        /// <summary>A Lesser Rift only opens for a player seen this recently (Q2).</summary>
        public static readonly TimeSpan OnlineWithin = TimeSpan.FromMinutes(30);

        public static TimeSpan OmenLeadOf(RiftKind kind) => kind == RiftKind.Great ? GreatOmenLead : LesserOmenLead;

        public static TimeSpan OpenForOf(RiftKind kind) => kind == RiftKind.Great ? GreatOpenFor : LesserOpenFor;

        /// <summary>Whether a rift at this stage is still part of the map: announced, open, or invading.</summary>
        public static bool IsLive(RiftStage stage) =>
            stage == RiftStage.Omen || stage == RiftStage.Open || stage == RiftStage.Invaded;

        /// <summary>Whether a rift at this stage can be sealed by a win.</summary>
        public static bool CanSeal(RiftStage stage) => stage == RiftStage.Open || stage == RiftStage.Invaded;

        /// <summary>
        /// When rift number <paramref name="eventNumber"/> of this kind is due to open, before any
        /// adjustment for a server that was down or a realm that was quiet. The first of a season comes
        /// one gap after the quiet start; each later one a gap after the previous opened. A Great Rift
        /// never opens while the one before it could still be open.
        /// </summary>
        public static DateTime DueToOpen(
            RiftKind kind, string realmId, int eventNumber, DateTime seasonStart,
            DateTime? previousOpensAt, DateTime? previousClosesAt)
        {
            var random = RandomFor(realmId, kind, eventNumber, "gap");
            int gap = kind == RiftKind.Great
                ? random.Next(GreatGapMinMinutes, GreatGapMaxMinutes)
                : random.Next(LesserGapMinMinutes, LesserGapMaxMinutes);

            var from = previousOpensAt ?? seasonStart + QuietAtSeasonStart;
            var due = from.AddMinutes(gap);
            if (kind == RiftKind.Great && previousClosesAt.HasValue && due < previousClosesAt.Value)
                due = previousClosesAt.Value;
            return due;
        }

        /// <summary>
        /// When the rift actually opens, given that its omen is being written at <paramref name="now"/>:
        /// on time if the omen is on time, or a full omen from now if the rift is overdue (the server was
        /// down, or nobody was there to receive it). A player always gets the whole warning.
        /// </summary>
        public static DateTime OpensAt(RiftKind kind, DateTime due, DateTime now)
        {
            var earliest = now + OmenLeadOf(kind);
            return due > earliest ? due : earliest;
        }

        /// <summary>
        /// No Great Rift opens in a season's last day (R8): its eruption, a siege or new Riftborn land, needs time to
        /// matter before the season ends.
        /// </summary>
        public static readonly TimeSpan QuietGreatAtSeasonEnd = TimeSpan.FromHours(24);

        /// <summary>Whether a rift opening at <paramref name="opensAt"/> falls inside the season's open part.</summary>
        public static bool InSeason(DateTime opensAt, DateTime seasonStart, DateTime seasonEnd) =>
            opensAt >= seasonStart + QuietAtSeasonStart && opensAt <= seasonEnd - QuietAtSeasonEnd;

        /// <summary>
        /// Whether a rift of <paramref name="kind"/> opening at <paramref name="opensAt"/> falls inside the season's open
        /// part: a Great Rift's ends a day before the season does, a Lesser Rift's six hours.
        /// </summary>
        public static bool InSeason(RiftKind kind, DateTime opensAt, DateTime seasonStart, DateTime seasonEnd) =>
            InSeason(opensAt, seasonStart, seasonEnd) &&
            (kind != RiftKind.Great || opensAt <= seasonEnd - QuietGreatAtSeasonEnd);

        // -----------------------------------------------------------------
        // Where (§3.3, R4)
        // -----------------------------------------------------------------

        /// <summary>Great Rifts open in these rings from the map's centre.</summary>
        public const int GreatInnerRing = 2;
        public const int GreatOuterRing = 4;

        /// <summary>A candidate bordering a player's region is this many times as likely.</summary>
        public const int BordersPlayerWeight = 3;

        /// <summary>
        /// Where a Great Rift may open: not a capital nor next to one, in rings 2–4 (or anywhere else
        /// that qualifies, on a map too small to have them), weighted toward land that borders a player.
        /// </summary>
        public static List<(WorldRegionData Region, int Weight)> GreatCandidates(IReadOnlyList<WorldRegionData> regions)
        {
            var capitals = regions.Where(r => r.IsCapital).Select(r => r.Hex).ToList();
            var held = regions.Where(IsPlayerHeld).Select(r => r.Hex).ToList();

            // Not on land the Riftborn already hold (§3.3): a rift there would only feed them.
            bool Allowed(WorldRegionData r) =>
                !r.IsCapital && !capitals.Any(c => HexCoord.Distance(c, r.Hex) <= 1) &&
                !FactionStrengthRules.Holds(FactionId.Riftborn, r);

            var allowed = regions.Where(Allowed).ToList();
            var inRings = allowed.Where(r =>
            {
                int ring = HexCoord.Distance(new HexCoord(0, 0), r.Hex);
                return ring >= GreatInnerRing && ring <= GreatOuterRing;
            }).ToList();
            var pool = inRings.Count > 0 ? inRings : allowed;

            return pool
                .Select(r => (r, held.Any(h => HexCoord.Distance(h, r.Hex) == 1) ? BordersPlayerWeight : 1))
                .ToList();
        }

        /// <summary>The region Great Rift number <paramref name="eventNumber"/> opens in; null if none qualifies.</summary>
        public static WorldRegionData PickGreat(IReadOnlyList<WorldRegionData> regions, string realmId, int eventNumber)
        {
            var candidates = GreatCandidates(regions)
                .OrderBy(c => c.Region.RegionId, StringComparer.Ordinal)
                .ToList();
            if (candidates.Count == 0) return null;

            var random = RandomFor(realmId, RiftKind.Great, eventNumber, "where");
            return candidates[random.NextWeighted(candidates.Select(c => c.Weight).ToArray())].Region;
        }

        /// <summary>
        /// Where a Lesser Rift may open: a region a player holds who has been online lately and has no
        /// live rift already, never a capital (Q3, "for now").
        /// </summary>
        public static List<WorldRegionData> LesserCandidates(
            IReadOnlyList<WorldRegionData> regions, ICollection<string> onlineUserIds, ICollection<string> usersWithRift)
        {
            return regions
                .Where(r => IsPlayerHeld(r) && !r.IsCapital
                    && onlineUserIds.Contains(r.OwnerUserId)
                    && !usersWithRift.Contains(r.OwnerUserId))
                .ToList();
        }

        /// <summary>
        /// The region Lesser Rift number <paramref name="eventNumber"/> opens in; null if none qualifies.
        /// The player is chosen first and then one of their regions, so holding more land does not
        /// bring more rifts.
        /// </summary>
        public static WorldRegionData PickLesser(IReadOnlyList<WorldRegionData> candidates, string realmId, int eventNumber)
        {
            if (candidates == null || candidates.Count == 0) return null;
            var players = candidates.Select(r => r.OwnerUserId).Distinct().OrderBy(u => u, StringComparer.Ordinal).ToList();

            var random = RandomFor(realmId, RiftKind.Lesser, eventNumber, "where");
            string player = players[random.Next(players.Count)];
            var theirs = candidates.Where(r => r.OwnerUserId == player)
                .OrderBy(r => r.RegionId, StringComparer.Ordinal)
                .ToList();
            return theirs[random.Next(theirs.Count)];
        }

        private static bool IsPlayerHeld(WorldRegionData region) =>
            region.Ownership == LocationOwnership.Player && !string.IsNullOrEmpty(region.OwnerUserId);

        // -----------------------------------------------------------------
        // Breach (R3)
        // -----------------------------------------------------------------

        public const int BreachPerActivePlayer = 600;
        public const int LeastBreach = 1200;

        /// <summary>A Great Rift's Breach, sized for four hours of a realm's players (those seen in the last day).</summary>
        public static int BreachMax(int activePlayers) => Math.Max(LeastBreach, BreachPerActivePlayer * Math.Max(0, activePlayers));

        // -----------------------------------------------------------------
        // Pay (row 6, §5.5, R1)
        // -----------------------------------------------------------------

        /// <summary>
        /// A contributor's Rift chest for a sealed Great Rift, by their share of the Breach and their
        /// place among contributors (1 = most). Null for a share too small to count.
        /// </summary>
        public static ItemRarityTypes? GreatChestFor(double share, int rank)
        {
            if (share <= 0) return null;
            if (rank == 1 && share >= 0.25) return ItemRarityTypes.Legendary;
            if (rank <= 3 || share >= 0.25) return ItemRarityTypes.Rare;
            if (share >= 0.05) return ItemRarityTypes.Magic;
            return ItemRarityTypes.Uncommon;
        }

        /// <summary>Whoever seals a Lesser Rift gets this chest (R1).</summary>
        public const ItemRarityTypes LesserChest = ItemRarityTypes.Magic;

        /// <summary>Season points for a share of a sealed Great Rift: 200 × share, at least 50 (row 6).</summary>
        public static double GreatSealPoints(double share) => Math.Max(50.0, 200.0 * Math.Max(0, share));

        /// <summary>Season points for sealing a Lesser Rift: "a little", as a site cleared.</summary>
        public const double LesserSealPoints = 40.0;

        // -----------------------------------------------------------------
        // Fighting it (§4, R3, R6 phase 3)
        // -----------------------------------------------------------------

        /// <summary>
        /// A rift site's id: <c>rift:{riftId}:{index}</c>. It is not a region's site (those are
        /// <c>{regionId}:{index}</c>), so it never resolves in the world blob and is never generated.
        /// It is also the id a player's lockout is kept under (<c>PlayerSiteClear</c>).
        /// </summary>
        public const string SitePrefix = "rift:";

        public static string SiteId(string riftId, int index) => $"{SitePrefix}{riftId}:{index}";

        public static bool IsSiteId(string siteId) =>
            siteId != null && siteId.StartsWith(SitePrefix, StringComparison.Ordinal);

        /// <summary>Splits a rift site's id; false for any other id.</summary>
        public static bool TryParseSiteId(string siteId, out string riftId, out int index)
        {
            riftId = null;
            index = -1;
            if (!IsSiteId(siteId)) return false;

            int split = siteId.LastIndexOf(':');
            if (split <= SitePrefix.Length) return false;
            if (!int.TryParse(siteId.Substring(split + 1), out index) || index < 0) return false;
            riftId = siteId.Substring(SitePrefix.Length, split - SitePrefix.Length);
            return riftId.Length > 0;
        }

        /// <summary>A Great Rift has three sites (Breach, Hollow, Heart); a Lesser Rift one.</summary>
        public static int SiteCount(RiftKind kind) => kind == RiftKind.Great ? 3 : 1;

        public const int HighestTier = 4;

        /// <summary>
        /// A rift site's tier: the region's for the Breach and a Lesser Rift, one more (at most IV) for
        /// the Hollow and the Heart (§4.2).
        /// </summary>
        public static int SiteTier(RiftKind kind, int regionTier, int index)
        {
            int tier = Math.Max(1, Math.Min(HighestTier, regionTier));
            return kind == RiftKind.Great && index > 0 ? Math.Min(HighestTier, tier + 1) : tier;
        }

        /// <summary>
        /// A rift site's enemy level: the middle of a cave's spread at that tier (a cave is
        /// <c>tier × 5 − 4</c> plus 0–3).
        /// </summary>
        public static int SiteLevel(int tier) => Math.Max(1, tier) * 5 - 4 + 2;

        /// <summary>
        /// How a rift fight is fought and priced: an open-field wave clearing, as a portal is. The
        /// type is never written anywhere; it is what the arena and the reward roll are told.
        /// </summary>
        public const LocationType SiteType = LocationType.Portal;

        /// <summary>
        /// Where the Riftborn come from: another biome's roster, never the region's own (§4.1), rolled
        /// from the rift.
        /// </summary>
        public static BiomeType DonorBiome(string riftId, BiomeType regionBiome)
        {
            var others = ((BiomeType[])Enum.GetValues(typeof(BiomeType))).Where(b => b != regionBiome).ToList();
            var random = RandomForRift(riftId, "donor");
            return others[random.Next(others.Count)];
        }

        /// <summary>Every mutator a rift rolls from (all eight since phase 7).</summary>
        public static readonly RiftMutator[] RolledMutators =
        {
            RiftMutator.Unstable, RiftMutator.Hasted, RiftMutator.Splitting, RiftMutator.Warded,
            RiftMutator.Grasping, RiftMutator.Starless, RiftMutator.Echoing, RiftMutator.Fractured,
        };

        /// <summary>How many mutators a site has: the Breach and a Lesser Rift 1, the Hollow and the Heart 2.</summary>
        public static int MutatorCount(RiftKind kind, int index) => kind == RiftKind.Great && index > 0 ? 2 : 1;

        /// <summary>
        /// A rift site's mutators. Each rift rolls two from its seed (§4.3); the Breach and a Lesser
        /// Rift take the first, the Hollow and the Heart both.
        /// </summary>
        public static List<RiftMutator> Mutators(string riftId, RiftKind kind, int index) =>
            Shuffled(riftId).Take(MutatorCount(kind, index)).ToList();

        private static List<RiftMutator> Shuffled(string riftId)
        {
            var pool = RolledMutators.ToList();
            var random = RandomForRift(riftId, "mutators");
            random.Shuffle(pool);
            return pool;
        }

        // -----------------------------------------------------------------
        // The Rift Lord (§4.1, §4.2, §6.3; phase 7)
        // -----------------------------------------------------------------

        /// <summary>The Rift Heart: a Great Rift's third site, where the Rift Lord always waits.</summary>
        public const int HeartIndex = 2;

        /// <summary>Whether this site's last wave brings the Rift Lord: the Heart's, whatever its tier.</summary>
        public static bool HasRiftLord(RiftKind kind, int index) => kind == RiftKind.Great && index == HeartIndex;

        /// <summary>The Rift Lord stands this much larger than the boss it was.</summary>
        public const float LordScale = 1.2f;

        /// <summary>
        /// The third mutator the Rift Lord brings (§4.3: "the Heart's boss adds 1 more"): the rift's next roll after
        /// the Heart's two, so it is never one already on.
        /// </summary>
        public static RiftMutator LordMutator(string riftId) => Shuffled(riftId)[MutatorCount(RiftKind.Great, HeartIndex)];

        // -----------------------------------------------------------------
        // The four mutators of phase 7 (client rules; the numbers live here so tests and the Codex agree)
        // -----------------------------------------------------------------

        /// <summary>Grasping: how often the hands reach, the warning first, how far they tug and how fast.</summary>
        public const float GraspEverySeconds = 8f;
        public const float GraspWarningSeconds = 1f;
        public const float GraspPullDistance = 2.5f;
        public const float GraspPullSeconds = 0.35f;

        /// <summary>Grasping reaches only for a Riftborn this close; with none near, the hands wait.</summary>
        public const float GraspReach = 12f;

        /// <summary>Starless: the light round the hero, in world units, and how dark the rest is (1 is black). The
        /// shade is blended in linear colour, so it reads lighter than its number: 0.96 leaves the dark at about a sixth of the
        /// light's brightness on screen (Mike chose it from a trial of 0.82-0.99, 2026-10-12).</summary>
        public const float StarlessLightRadius = 5.5f;
        public const float StarlessDarkness = 0.96f;

        /// <summary>Echoing: how long before the ghost rises, and the share of its health it comes back with.</summary>
        public const float EchoDelaySeconds = 1f;
        public const float EchoHealthShare = 0.5f;

        /// <summary>Fractured: a weakness's damage multiplier (twice a boss's usual +25%).</summary>
        public const float FracturedWeakMultiplier = 1.5f;

        /// <summary>A rift fight pays a run at its level and tier, times this (§4.4).</summary>
        public const double RewardMultiplier = 1.25;

        public static long Boosted(long amount) => amount <= 0 ? 0 : (long)Math.Round(amount * RewardMultiplier);

        /// <summary>A player who won at a rift site may fight there again after this (R3).</summary>
        public static readonly TimeSpan SiteLockout = TimeSpan.FromMinutes(20);

        public static DateTime LockedUntil(DateTime? lastWonUtc) =>
            lastWonUtc.HasValue ? lastWonUtc.Value + SiteLockout : DateTime.MinValue;

        /// <summary>
        /// The Breach one win at a Great Rift's site wears away: Breach 40, Hollow 60, Heart 100 (the
        /// spec's 1 : 1.5 : 2.5). A fixed figure rather than the run's experience ÷ 10, so a rift
        /// sized for its realm's players (<see cref="BreachMax"/>) takes the same work at any level.
        /// A Lesser Rift has no Breach to speak of: one win seals it.
        /// </summary>
        public static int BreachPerWin(RiftKind kind, int index)
        {
            if (kind == RiftKind.Lesser) return int.MaxValue;
            switch (index)
            {
                case 0: return 40;
                case 1: return 60;
                default: return 100;
            }
        }

        // Combat numbers for the mutators (client-side rules; here so the Codex and the tests agree).

        /// <summary>Hasted: enemy move speed ×1.25.</summary>
        public const float HastedSpeed = 1.25f;

        /// <summary>Splitting: one enemy in five splits on death, into two at this share of its health.</summary>
        public const int SplitOneIn = 5;
        public const float SplitHealthShare = 0.4f;

        /// <summary>
        /// Unstable: the burst's radius (world units), its warning, and its damage before the enemy level's
        /// scaling (<c>EnemyStatScaling.EnemyAbilityDamage</c>, as a gauntlet's hazards are scaled).
        /// </summary>
        public const float UnstableRadius = 1.6f;
        public const float UnstableDelaySeconds = 0.9f;
        public const long UnstableBaseDamage = 40;

        /// <summary>Warded: an elite's Ward takes this much off each hit, until it has taken this share of its health.</summary>
        public const float WardReduction = 0.6f;
        public const float WardBreaksAfter = 0.25f;

        // -----------------------------------------------------------------
        // Rift contracts (R7, R6 phase 5)
        // -----------------------------------------------------------------

        /// <summary>How long a taker holds the right to close the rift (R7: "30 minutes, tune").</summary>
        public static readonly TimeSpan ContractHold = TimeSpan.FromMinutes(30);

        /// <summary>
        /// How long past its hold a taker's fight already under way keeps the contract theirs, so a fight begun in the
        /// hold's last minute can still be won and paid.
        /// </summary>
        public static readonly TimeSpan ContractFightGrace = TimeSpan.FromMinutes(20);

        /// <summary>The least and most a contract can name, in gold, and what the book offers first.</summary>
        public const long ContractMinPrice = 50;
        public const long ContractMaxPrice = 50000;
        public const long ContractSuggestedPrice = 500;

        public static bool IsLive(RiftContractStatus status) => status == RiftContractStatus.Posted || status == RiftContractStatus.Taken;

        /// <summary>Whether a contract can be posted on this rift: a Lesser Rift, open or invading.</summary>
        public static bool CanPostContract(RiftKind kind, RiftStage stage) => kind == RiftKind.Lesser && CanSeal(stage);

        public static bool IsValidPrice(long price) => price >= ContractMinPrice && price <= ContractMaxPrice;

        /// <summary>Whether a taker's hold still stands at <paramref name="now"/>.</summary>
        public static bool IsHeld(RiftContractStatus status, DateTime? heldUntil, DateTime now) =>
            status == RiftContractStatus.Taken && heldUntil.HasValue && now < heldUntil.Value;

        /// <summary>
        /// Who may close a Lesser Rift (R4, R7): whoever holds its contract while the hold stands, and otherwise its
        /// lord. <paramref name="holderUserId"/> is null when nobody holds it.
        /// </summary>
        public static bool MayCloseLesser(string userId, string lordUserId, string holderUserId, DateTime? heldUntil, DateTime now)
        {
            if (string.IsNullOrEmpty(userId)) return false;
            if (!string.IsNullOrEmpty(holderUserId) && heldUntil.HasValue && now < heldUntil.Value)
                return string.Equals(userId, holderUserId, StringComparison.Ordinal);
            return string.Equals(userId, lordUserId, StringComparison.Ordinal);
        }

        /// <summary>Who a contract's held gold goes back to if it is not fulfilled: a Sell's taker, a Hire's lord.</summary>
        public static string RefundTo(RiftContractKind kind, string ownerUserId, string holderUserId) =>
            kind == RiftContractKind.Sell ? holderUserId : ownerUserId;

        /// <summary>Who a fulfilled contract pays: a Sell pays the lord, a Hire pays the closer.</summary>
        public static string PaysTo(RiftContractKind kind, string ownerUserId, string holderUserId) =>
            kind == RiftContractKind.Sell ? ownerUserId : holderUserId;

        // -----------------------------------------------------------------
        // The Invasion (R4, R6 phase 4)
        // -----------------------------------------------------------------

        /// <summary>An invaded region's roads are this many times as likely to be ambushed (R4: "twice as often").</summary>
        public const double InvadedAmbushFactor = 2.0;

        /// <summary>The invasion holding <paramref name="regionId"/> at <paramref name="at"/>, or null.</summary>
        public static Invasion InvasionAt(IEnumerable<Invasion> invasions, string regionId, DateTime at) =>
            invasions?.FirstOrDefault(i => i != null && i.RegionId == regionId && i.Covers(at));

        /// <summary>The ambush factor for a road through <paramref name="regionId"/> at <paramref name="at"/>: 2 while it is invaded, else 1.</summary>
        public static double AmbushChanceFactor(IEnumerable<Invasion> invasions, string regionId, DateTime at) =>
            InvasionAt(invasions, regionId, at) != null ? InvadedAmbushFactor : 1.0;

        /// <summary>
        /// Whether a region's diggings are harried in an hour: <b>every</b> hour of an invasion, patrol or not
        /// ("only closing the rift ends it"), else the raiders' one hour in ten while nobody patrols it
        /// (<see cref="HarassmentRules"/>).
        /// </summary>
        public static bool IsHarried(string realmId, string regionId, long hourIndex, bool patrolled, IEnumerable<Invasion> invasions)
        {
            if (invasions != null && invasions.Any(i => i != null && i.RegionId == regionId && i.CoversHour(hourIndex))) return true;
            return !patrolled && HarassmentRules.IsHarried(realmId, regionId, hourIndex);
        }

        /// <summary>A sealed Great Rift's top three contributors are its Wardens: a mark on their standings row for the season (row 6).</summary>
        public const int WardenPlaces = 3;

        /// <summary>
        /// The mutator a Riftborn warband brings to an ambush in an invaded region: the rift's first, as its own site
        /// has (R4: "the tint, a mutator").
        /// </summary>
        public static List<RiftMutator> AmbushMutators(string riftId) => Mutators(riftId, RiftKind.Lesser, 0);

        // -----------------------------------------------------------------
        // The eruption and the Riftborn (R8, R6 phase 6)
        // -----------------------------------------------------------------

        /// <summary>
        /// Eruptions hand the Riftborn land (wild land seized, or held land besieged) this many times a season (Q4, tune).
        /// Past it they surge instead. Their own spreading has no cap (R8, option b).
        /// </summary>
        public const int EruptionLandCap = 6;

        /// <summary>
        /// What erupting does to a region: wild land is seized, held land besieged, until eruptions have handed the
        /// Riftborn <see cref="EruptionLandCap"/> regions this season; past that, or on land of their own, they surge.
        /// <paramref name="besiegeable"/> is false for land a siege cannot be laid on now (already under one, or truce).
        /// </summary>
        public static RiftEruption EruptionFor(WorldRegionData region, int landErupted, bool besiegeable)
        {
            if (region == null) return RiftEruption.None;
            if (landErupted >= EruptionLandCap || FactionStrengthRules.Holds(FactionId.Riftborn, region)) return RiftEruption.Surged;

            bool player = region.Ownership == LocationOwnership.Player && !string.IsNullOrEmpty(region.OwnerUserId);
            bool faction = FactionStrengthRules.IsFaction(region.Faction) && string.IsNullOrEmpty(region.OwnerUserId);
            if (!player && !faction) return region.IsCapital ? RiftEruption.Surged : RiftEruption.Seized;
            return besiegeable && !region.IsCapital ? RiftEruption.Besieged : RiftEruption.Surged;
        }

        /// <summary>Whether an eruption's outcome counts toward <see cref="EruptionLandCap"/>.</summary>
        public static bool HandsLand(RiftEruption eruption) => eruption == RiftEruption.Seized || eruption == RiftEruption.Besieged;

        /// <summary>
        /// How large the eruption's siege is against a normal faction siege (R8): × (0.5 + Breach left ÷ Breach max), so a
        /// nearly sealed rift brings half an army and an untouched one half again as much.
        /// </summary>
        public static double EruptionSiegeFactor(int breachLeft, int breachMax) =>
            0.5 + (breachMax <= 0 ? 0 : Math.Max(0, Math.Min(1.0, breachLeft / (double)breachMax)));

        /// <summary>
        /// The eruption siege's march. A normal faction siege marches what the region holds against it (the hold, the
        /// bar any siege must answer), so the march is that hold × <see cref="EruptionSiegeFactor"/>: an untouched rift
        /// clears the gate with room to spare, a nearly sealed one falls below it and is turned away at the walls.
        /// </summary>
        public static double EruptionMarch(long hold, int breachLeft, int breachMax) =>
            Math.Max(1, hold) * EruptionSiegeFactor(breachLeft, breachMax);

        /// <summary>
        /// A Riftborn region's fights (R8): the Riftborn people of another biome, rolled from the region rather than a
        /// rift, so the same land always pours the same strangers through.
        /// </summary>
        public static string LandKey(string regionId) => "land:" + regionId;

        /// <summary>Whose people a Riftborn region's fights are (<see cref="DonorBiome"/> of the land).</summary>
        public static BiomeType LandDonorBiome(string regionId, BiomeType regionBiome) => DonorBiome(LandKey(regionId), regionBiome);

        /// <summary>A Riftborn region's one mutator.</summary>
        public static List<RiftMutator> LandMutators(string regionId) => Mutators(LandKey(regionId), RiftKind.Lesser, 0);

        // -----------------------------------------------------------------
        // The factions chip the rift (row 7, §3.2)
        // -----------------------------------------------------------------

        /// <summary>On each of its turns, a faction bordering an open Great Rift strikes at it with this chance…</summary>
        public const double FactionChipChance = 1.0 / 6.0;

        /// <summary>…taking this share of the Breach's whole off it…</summary>
        public const double FactionChipShare = 0.02;

        /// <summary>…and losing this share of its strength.</summary>
        public const double FactionChipCost = 0.05;

        /// <summary>What one strike takes off the Breach.</summary>
        public static int FactionChip(int breachMax) => Math.Max(1, (int)Math.Round(breachMax * FactionChipShare));

        /// <summary>The Breach after a strike. A faction never seals a rift: the last point is a player's to take.</summary>
        public static int Chipped(int breach, int chip) => Math.Max(1, breach - Math.Max(0, chip));

        /// <summary>
        /// Whether <paramref name="faction"/> strikes at a rift in <paramref name="riftRegion"/>: it holds land on or beside
        /// it, and is not the Riftborn, who are what comes out of it.
        /// </summary>
        public static bool Chips(FactionId faction, WorldRegionData riftRegion, IReadOnlyCollection<WorldRegionData> regions)
        {
            if (riftRegion == null || faction == FactionId.Riftborn || !FactionStrengthRules.IsFaction(faction)) return false;
            if (FactionStrengthRules.Holds(faction, riftRegion)) return true;
            foreach (var r in regions)
                if (FactionStrengthRules.Holds(faction, r) && HexCoord.Distance(r.Hex, riftRegion.Hex) == 1) return true;
            return false;
        }

        // -----------------------------------------------------------------

        private static DeterministicRandom RandomForRift(string riftId, string purpose) =>
            DeterministicRandom.ForSubject(
                Naming.Hash((riftId ?? "").ToLowerInvariant()),
                Naming.Hash($"rift-fight:{purpose}"));

        private static DeterministicRandom RandomFor(string realmId, RiftKind kind, int eventNumber, string purpose) =>
            DeterministicRandom.ForSubject(
                Naming.Hash((realmId ?? "").ToLowerInvariant()),
                Naming.Hash($"rift:{kind}:{eventNumber}:{purpose}"));
    }
}
