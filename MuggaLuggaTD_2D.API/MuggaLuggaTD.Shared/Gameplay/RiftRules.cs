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

        /// <summary>Whether a rift opening at <paramref name="opensAt"/> falls inside the season's open part.</summary>
        public static bool InSeason(DateTime opensAt, DateTime seasonStart, DateTime seasonEnd) =>
            opensAt >= seasonStart + QuietAtSeasonStart && opensAt <= seasonEnd - QuietAtSeasonEnd;

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

            bool Allowed(WorldRegionData r) =>
                !r.IsCapital && !capitals.Any(c => HexCoord.Distance(c, r.Hex) <= 1);

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

        private static DeterministicRandom RandomFor(string realmId, RiftKind kind, int eventNumber, string purpose) =>
            DeterministicRandom.ForSubject(
                Naming.Hash((realmId ?? "").ToLowerInvariant()),
                Naming.Hash($"rift:{kind}:{eventNumber}:{purpose}"));
    }
}
