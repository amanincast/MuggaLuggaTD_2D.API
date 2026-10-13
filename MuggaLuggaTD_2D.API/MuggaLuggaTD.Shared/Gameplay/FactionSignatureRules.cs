using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>The one thing a faction does that no other does (Factions 2 spec §2.2).</summary>
    public enum FactionSignature
    {
        /// <summary>Behaves exactly as every faction did before step 3.</summary>
        None = 0,

        /// <summary>The Grimjaw: a raid that wins rides on into a second bordering region at half its march.</summary>
        Warband = 1,

        /// <summary>The Ashkin: their fortify restores <see cref="FactionSignatureRules.DeepRootsResolveBonus"/> resolve, not 15.</summary>
        DeepRoots = 2,

        /// <summary>The Drowned Court: a siege they win also floods one neighbour (<see cref="FactionSignatureRules.TideResolveLoss"/>).</summary>
        TheTide = 3,

        /// <summary>The Bone Synod: a region they lose raids its new holder once, from the grave.</summary>
        TheDeadRise = 4,

        /// <summary>The Thornwood Pact: they expand into tier 2 land, where others need tier 3 (<see cref="FactionInfo.ExpandMinimumTier"/>).</summary>
        Overgrowth = 5,

        /// <summary>The Horned Kin: once a day a raid of theirs ignores the per-region cooldown.</summary>
        Stampede = 6
    }

    /// <summary>
    /// The factions' signature acts (Factions 2 spec §2.2, step 3): one rule each. The server wires each into the turn it
    /// belongs to (raid, siege, fortify, expand); a faction without one behaves exactly as before.
    ///
    /// <para>None keeps state of its own. A stampede is read off the faction's own raids (a raid on a region it raided
    /// less than a cooldown before), and the Bone Synod's dead are a mark on the region they lost, cleared when they rise.</para>
    /// </summary>
    public static class FactionSignatureRules
    {
        /// <summary>The Grimjaw's second raid marches with this share of the first's march.</summary>
        public const double WarbandMarchShare = 0.5;

        /// <summary>Resolve an Ashkin fortify restores (others: <see cref="FactionGrowthRules.FortifyResolveBonus"/>).</summary>
        public const int DeepRootsResolveBonus = 25;

        /// <summary>Resolve the Drowned Court's tide takes from one neighbour of land it has just taken.</summary>
        public const int TideResolveLoss = 20;

        /// <summary>The least tier the Thornwood Pact expands into.</summary>
        public const int OvergrowthMinimumTier = 2;

        /// <summary>How often the Horned Kin may stampede.</summary>
        public static readonly TimeSpan StampedeEvery = TimeSpan.FromDays(1);

        /// <summary>How long the Bone Synod's dead wait to rise: a region lost longer ago than this stays quiet.</summary>
        public static readonly TimeSpan RisenFor = TimeSpan.FromDays(1);

        /// <summary>The faction's signature, from the catalogue.</summary>
        public static FactionSignature Of(FactionId faction) => FactionCatalog.Of(faction)?.Signature ?? FactionSignature.None;

        public static bool Has(FactionId faction, FactionSignature signature) =>
            signature != FactionSignature.None && Of(faction) == signature;

        /// <summary>One line for the Powers panel and the dossier: what the faction does that no other does.</summary>
        public static string Describe(FactionSignature signature)
        {
            switch (signature)
            {
                case FactionSignature.Warband: return "Warband: a raid that lands rides on into a second region at half strength.";
                case FactionSignature.DeepRoots: return $"Deep roots: their walls restore {DeepRootsResolveBonus} resolve, not {FactionGrowthRules.FortifyResolveBonus}.";
                case FactionSignature.TheTide: return $"The tide: a siege they win floods a neighbour, resolve -{TideResolveLoss}.";
                case FactionSignature.TheDeadRise: return "The dead rise: land taken from them raids its new holder once, truce or no truce.";
                case FactionSignature.Overgrowth: return $"Overgrowth: they spread into tier {OvergrowthMinimumTier} land, where others need tier 3.";
                case FactionSignature.Stampede: return "Stampede: once a day they raid land they raided only hours before.";
                default: return string.Empty;
            }
        }

        /// <summary>The name alone: "Warband", "The tide".</summary>
        public static string NameOf(FactionSignature signature)
        {
            string line = Describe(signature);
            int colon = line.IndexOf(':');
            return colon > 0 ? line.Substring(0, colon) : string.Empty;
        }

        // --- Warband -------------------------------------------------------------------------------------------

        /// <summary>The march a Grimjaw warband rides on with, after a raid of <paramref name="march"/> lands.</summary>
        public static double WarbandMarch(double march) => Math.Max(0, march) * WarbandMarchShare;

        // --- Deep roots ----------------------------------------------------------------------------------------

        /// <summary>The resolve a fortify by <paramref name="faction"/> restores.</summary>
        public static int FortifyResolveBonus(FactionId faction) =>
            Has(faction, FactionSignature.DeepRoots) ? DeepRootsResolveBonus : FactionGrowthRules.FortifyResolveBonus;

        // --- The tide ------------------------------------------------------------------------------------------

        /// <summary>
        /// The neighbour of <paramref name="taken"/> the Drowned Court's tide floods, chosen by a roll in [0, 1): land a
        /// player or another faction holds that a raid could reach (<see cref="FactionDecisionRules.IsRaidable"/>: no
        /// capital, nothing under truce) with resolve left to lose. Null when there is none.
        /// </summary>
        public static WorldRegionData PickTide(FactionId faction, WorldRegionData taken, IReadOnlyCollection<WorldRegionData> regions,
            DateTime utcNow, double roll)
        {
            if (taken == null || regions == null) return null;
            var open = new List<WorldRegionData>();
            foreach (var region in regions)
            {
                if (HexCoord.Distance(region.Hex, taken.Hex) != 1) continue;
                if (region.Resolve <= RegionResolveRules.Minimum) continue;
                if (FactionDecisionRules.IsRaidable(faction, region, utcNow)) open.Add(region);
            }
            if (open.Count == 0) return null;

            open.Sort((a, b) => string.CompareOrdinal(a.RegionId, b.RegionId));
            int at = (int)(Math.Max(0, Math.Min(0.999999, roll)) * open.Count);
            return open[at];
        }

        /// <summary>A region's resolve once the tide has passed over it.</summary>
        public static int Flooded(int resolve) => RegionResolveRules.Clamp(resolve - TideResolveLoss);

        // --- The dead rise -------------------------------------------------------------------------------------

        /// <summary>Whether losing a region to anyone leaves dead behind to raid its new holder (the Bone Synod's).</summary>
        public static bool RisesOnLoss(FactionId previousHolder) => Has(previousHolder, FactionSignature.TheDeadRise);

        /// <summary>Whether dead left at <paramref name="lostAt"/> may still rise at <paramref name="utcNow"/>.</summary>
        public static bool MayRise(DateTime lostAt, DateTime utcNow) => utcNow >= lostAt && utcNow - lostAt <= RisenFor;

        /// <summary>
        /// Whether the risen may raid this region: it is still in other hands and it is no capital. Truce does not stop
        /// them; that is the act.
        /// </summary>
        public static bool MayRaidBack(FactionId faction, WorldRegionData region) =>
            region != null && !region.IsCapital && !FactionStrengthRules.Holds(faction, region) &&
            (!string.IsNullOrEmpty(region.OwnerUserId) || FactionStrengthRules.IsFaction(region.Faction));

        // --- Stampede ------------------------------------------------------------------------------------------

        /// <summary>
        /// Whether a raid on <paramref name="regionId"/> at <paramref name="at"/> was a stampede: the same faction had
        /// raided it less than <paramref name="cooldown"/> before. <paramref name="ownRaids"/> are that faction's raids.
        /// </summary>
        public static bool WasStampede(string regionId, DateTime at, IEnumerable<(string RegionId, DateTime At)> ownRaids, TimeSpan cooldown)
        {
            foreach (var (other, when) in ownRaids)
                if (other == regionId && when < at && when + cooldown > at) return true;
            return false;
        }

        /// <summary>
        /// Whether the faction may stampede now: none of its raids in the last <see cref="StampedeEvery"/> was one.
        /// <paramref name="ownRaids"/> must reach back <see cref="StampedeEvery"/> plus <paramref name="cooldown"/>.
        /// </summary>
        public static bool MayStampede(IReadOnlyList<(string RegionId, DateTime At)> ownRaids, DateTime utcNow, TimeSpan cooldown)
        {
            if (ownRaids == null) return true;
            foreach (var (regionId, at) in ownRaids)
                if (at > utcNow - StampedeEvery && at <= utcNow && WasStampede(regionId, at, ownRaids, cooldown)) return false;
            return true;
        }
    }
}
