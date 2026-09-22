using System;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>Why a siege may not be declared. <see cref="None"/> means it may.</summary>
    public enum SiegeRefusal
    {
        None = 0,

        /// <summary>The region is not held by another player.</summary>
        NotAPlayerRegion,

        /// <summary>The region is the declaring player's own.</summary>
        OwnRegion,

        /// <summary>A player's seat. Capitals are off the board (siege.md §7).</summary>
        Capital,

        /// <summary>The region changed hands recently and is under truce (siege.md §7a).</summary>
        UnderTruce,

        /// <summary>Its resolve has not been worn down far enough. Raid it first.</summary>
        ResolveTooHigh,

        /// <summary>The marching army does not reach the siege gate.</summary>
        BelowGate,

        /// <summary>Too little of the season remains for a siege to finish before the bell.</summary>
        SeasonClosing
    }

    /// <summary>
    /// When a siege may be declared, and how long each part of it lasts (<c>docs/design/siege.md</c>).
    ///
    /// <para>A siege is the decisive action — the one that actually moves a region between players —
    /// so it is gated behind attrition: only once raids have worn the region's resolve down, and only
    /// with an army that reaches the full gate. Declaring it opens a muster window that belongs to the
    /// defender, and after that an assault window in which the attacker must show.</para>
    ///
    /// <para>Shared so the dossier can tell a player whether they may declare, and why not, under
    /// exactly the rules the server will apply when they try. Pure: every time-dependent answer takes
    /// "now" as an argument.</para>
    /// </summary>
    public static class SiegeRules
    {
        /// <summary>
        /// Resolve at or below which a region may be besieged.
        ///
        /// <para>Raids take 5-15 each, so from full morale this is roughly five successful raids —
        /// the design's "five-plus raids across a day or more", every one of them visible to the
        /// defender before anything is at stake.</para>
        /// </summary>
        public const int DeclareResolveThreshold = 50;

        /// <summary>
        /// The longest the defender's muster window runs. The defender may close it early by
        /// declaring ready, to pull the fight to a time they are awake for.
        /// </summary>
        public const int MusterHours = 8;

        /// <summary>How long the attacker has to show for the assault once muster closes.</summary>
        public const int AssaultWindowHours = 12;

        /// <summary>
        /// How long a region is untouchable after it changes hands (siege.md §7a).
        ///
        /// <para>Exists to stop the same region ping-ponging between two players, and to give a new
        /// owner time to garrison and tend what they took. Applies to raids as well as sieges.</para>
        /// </summary>
        public const int TruceHours = 24;

        /// <summary>
        /// Declarations close once fewer than this many hours of the season remain.
        ///
        /// <para>Chosen to exceed <see cref="MusterHours"/> + <see cref="AssaultWindowHours"/>, so
        /// that every siege declared before the cutoff resolves before the bell. The last day cannot
        /// become a scramble to grab ground, and no siege is ever left half-fought when the realm
        /// resets.</para>
        /// </summary>
        public const int SeasonCutoffHours = 24;

        /// <summary>
        /// How long after a siege ends without taking the region before the same attacker may
        /// declare on it again. Attacking must be able to cost something (siege.md §6).
        /// </summary>
        public const int RedeclareCooldownHours = 24;

        public static TimeSpan Muster => TimeSpan.FromHours(MusterHours);
        public static TimeSpan AssaultWindow => TimeSpan.FromHours(AssaultWindowHours);
        public static TimeSpan Truce => TimeSpan.FromHours(TruceHours);
        public static TimeSpan SeasonCutoff => TimeSpan.FromHours(SeasonCutoffHours);
        public static TimeSpan RedeclareCooldown => TimeSpan.FromHours(RedeclareCooldownHours);

        // -----------------------------------------------------------------
        // Truce
        // -----------------------------------------------------------------

        /// <summary>When the region last changed hands, or null when it never has (or before this rule).</summary>
        public static DateTime? ClaimedAt(WorldRegionData region)
        {
            if (region == null || region.ClaimedAtUtcTicks <= 0) return null;
            return new DateTime(region.ClaimedAtUtcTicks, DateTimeKind.Utc);
        }

        /// <summary>When the truce on a region lifts. Null when it is not under one.</summary>
        public static DateTime? TruceEndsAt(WorldRegionData region)
        {
            var claimed = ClaimedAt(region);
            return claimed.HasValue ? claimed.Value + Truce : (DateTime?)null;
        }

        /// <summary>
        /// True while a region that recently changed hands may not be raided or besieged.
        ///
        /// <para>A region with no claim stamp — every region that has never changed hands, and every
        /// one captured before this rule existed — is not under truce. That is the harmless
        /// direction: the rule only ever protects, it never retroactively locks land that was
        /// already contestable.</para>
        /// </summary>
        public static bool IsUnderTruce(WorldRegionData region, DateTime utcNow)
        {
            var ends = TruceEndsAt(region);
            return ends.HasValue && utcNow < ends.Value;
        }

        // -----------------------------------------------------------------
        // Declaring
        // -----------------------------------------------------------------

        /// <summary>True once too little of the season is left to finish a siege before the bell.</summary>
        public static bool SeasonIsClosing(DateTime utcNow, DateTime seasonEndsAt)
        {
            return seasonEndsAt - utcNow < SeasonCutoff;
        }

        /// <summary>
        /// Whether <paramref name="attackerUserId"/> may declare a siege on <paramref name="region"/>
        /// with an army of <paramref name="marchingPower"/>, given its current hold.
        ///
        /// <para>Checked in the order a player would want to be told: first whether the region is a
        /// target at all, then whether it has been worn down, then whether the army is enough, then
        /// whether there is time. The server applies further rules this cannot see — one siege per
        /// region, one per attacker, the re-declare cooldown, and whether the army is really theirs
        /// and uncommitted — because those live in its tables rather than in the world.</para>
        /// </summary>
        public static SiegeRefusal CheckDeclare(
            WorldRegionData region,
            string attackerUserId,
            double marchingPower,
            long hold,
            DateTime utcNow,
            DateTime seasonEndsAt)
        {
            if (region == null
                || region.Ownership != LocationOwnership.Player
                || string.IsNullOrEmpty(region.OwnerUserId))
                return SiegeRefusal.NotAPlayerRegion;

            if (string.Equals(region.OwnerUserId, attackerUserId, StringComparison.Ordinal))
                return SiegeRefusal.OwnRegion;

            if (region.IsCapital)
                return SiegeRefusal.Capital;

            if (IsUnderTruce(region, utcNow))
                return SiegeRefusal.UnderTruce;

            if (region.Resolve > DeclareResolveThreshold)
                return SiegeRefusal.ResolveTooHigh;

            if (!RegionHoldCalculator.ClearsGate(marchingPower, hold))
                return SiegeRefusal.BelowGate;

            if (SeasonIsClosing(utcNow, seasonEndsAt))
                return SiegeRefusal.SeasonClosing;

            return SiegeRefusal.None;
        }

        /// <summary>A sentence for the player explaining a refusal.</summary>
        public static string Explain(SiegeRefusal refusal)
        {
            switch (refusal)
            {
                case SiegeRefusal.None: return "You may declare a siege.";
                case SiegeRefusal.NotAPlayerRegion: return "Only a region held by another player can be besieged — take a neutral region's keep instead.";
                case SiegeRefusal.OwnRegion: return "That region is already yours.";
                case SiegeRefusal.Capital: return "A player's seat cannot be besieged.";
                case SiegeRefusal.UnderTruce: return "That region changed hands recently and is under truce.";
                case SiegeRefusal.ResolveTooHigh: return $"Its resolve must be worn down to {DeclareResolveThreshold} or below before it can be besieged. Raid it first.";
                case SiegeRefusal.BelowGate: return "Your army does not reach the siege gate.";
                case SiegeRefusal.SeasonClosing: return "Too little of the season remains for a siege to finish before the bell.";
                default: return "A siege cannot be declared here.";
            }
        }
    }
}
