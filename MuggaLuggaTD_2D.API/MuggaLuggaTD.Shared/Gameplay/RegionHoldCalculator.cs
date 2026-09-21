using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// How hard a region is to take (design 7d, floor per <c>docs/design/siege.md</c> §2).
    ///
    /// <code>
    /// Hold = (garrison + holdFloor(tier)) x entrench x supply x max(resolve, floor) / 100
    /// gate = Hold x 0.6
    /// </code>
    ///
    /// <para>Shared for the same reason <see cref="PartyPowerCalculator"/> is: the server has to
    /// judge a siege under exactly the rules the client showed the player when they decided to
    /// declare one.</para>
    ///
    /// <para><b>Raiding acts on these numbers; sieging does not yet.</b> <see cref="RaidResolver"/>
    /// measures a raid against the raid bar — half the gate — and wears resolve down, which lowers
    /// hold for next time. The gate itself is still only displayed: the design's declare,
    /// eight-hour muster and scheduled assault are not built, and that resolution rule will live
    /// beside this one once the win condition it depends on is settled.</para>
    /// </summary>
    public static class RegionHoldCalculator
    {
        /// <summary>The attacker must bring this share of a region's hold before they may declare.</summary>
        public const double SiegeGateFraction = 0.6;

        /// <summary>
        /// What a region defends itself with per tier when nobody is home — the walls and the locals.
        ///
        /// <para>Without this the hold of an ungarrisoned region is zero, so the gate is zero and
        /// anyone walks in with nothing. That is exactly the player the siege design exists to
        /// protect: someone who logged off without stationing anyone. Scaled on tier so a deep-map
        /// region defends itself harder than a border one.</para>
        ///
        /// <para>Sized against <see cref="PartyPowerCalculator.POWER_PER_LEVEL"/> (100/level): a
        /// tier-1 gate of 300 is a starting party's worth, a tier-4 gate of 1,200 wants a developed
        /// one. Against the design's worked example — a 5,640 garrison holding at 9,024 — undefended
        /// land stays several times softer than defended land, which is the intent. Soft, not free.</para>
        /// </summary>
        public const double HoldFloorPerTier = 500;

        /// <summary>
        /// The least a region's resolve can count for, however thoroughly it has been ground down.
        ///
        /// <para>Resolve multiplies the whole of hold, so without a floor a region raided to zero
        /// resolve has zero hold and a zero gate — the same "walks in with nothing" hole the tier
        /// floor closes, reached by a different door. Broken morale should make a region cheap to
        /// besiege, which is what the design wants raiding to buy; it should not make the walls
        /// vanish.</para>
        /// </summary>
        public const int MinimumEffectiveResolve = 25;

        /// <summary>Entrenchment 0 to 5, as the design's "ENTRENCH IV (x1.60)".</summary>
        public static double EntrenchmentMultiplier(int entrenchment)
        {
            switch (entrenchment)
            {
                case 1: return 1.15;
                case 2: return 1.30;
                case 3: return 1.45;
                case 4: return 1.60;
                case 5: return 1.75;
                default: return 1.00;
            }
        }

        public static string EntrenchmentLabel(int entrenchment)
        {
            switch (entrenchment)
            {
                case 1: return "I";
                case 2: return "II";
                case 3: return "III";
                case 4: return "IV";
                case 5: return "V";
                default: return "—";
            }
        }

        /// <summary>What the walls and locals of a tier-<paramref name="tier"/> region are worth.</summary>
        public static double HoldFloor(int tier)
        {
            return Clamp(tier, 1, 4) * HoldFloorPerTier;
        }

        /// <summary>
        /// A region's hold, rounded the way the dossier shows it.
        ///
        /// <para>The design's worked example is the garrison term: 5,640 garrison x 1.60 entrench x
        /// 1.00 supply x 100% resolve = 9,024. A region's floor is added to the garrison before the
        /// multipliers, so entrenchment strengthens the walls as well as the troops — which is what
        /// entrenching an empty region should buy.</para>
        ///
        /// <para>Note the floor is a function of tier alone, not of tier and entrenchment as the
        /// design sketch wrote it: entrenchment already multiplies the bracket, so taking it inside
        /// as well would square it.</para>
        /// </summary>
        public static long Hold(double garrisonPower, int tier, int entrenchment, double supply, int resolve)
        {
            double garrison = garrisonPower > 0 ? garrisonPower : 0;

            double clampedResolve = Clamp(resolve, MinimumEffectiveResolve, 100) / 100.0;
            double value = (garrison + HoldFloor(tier))
                           * EntrenchmentMultiplier(entrenchment)
                           * supply
                           * clampedResolve;

            return (long)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        /// <summary>The power an attacker must field before they may declare a siege.</summary>
        public static long SiegeGate(long hold)
        {
            return (long)Math.Round(hold * SiegeGateFraction, MidpointRounding.AwayFromZero);
        }

        /// <summary>True when the attacking party is strong enough to declare.</summary>
        public static bool ClearsGate(double marchingPower, long hold)
        {
            return marchingPower >= SiegeGate(hold);
        }

        /// <summary>
        /// Everything the dossier's siege block needs, computed once.
        /// <paramref name="supply"/> comes from <see cref="SupplyCalculator"/>.
        /// </summary>
        public static SiegeAssessment Assess(double garrisonPower, int tier, int entrenchment, double supply, int resolve, double marchingPower)
        {
            long hold = Hold(garrisonPower, tier, entrenchment, supply, resolve);
            long gate = SiegeGate(hold);
            long raidBar = RaidResolver.RaidBar(hold);

            return new SiegeAssessment
            {
                Hold = hold,
                Gate = gate,
                RaidBar = raidBar,
                GarrisonPower = garrisonPower,
                Supply = supply,
                MarchingPower = marchingPower,
                ClearsGate = marchingPower >= gate,
                ClearsRaidBar = marchingPower >= raidBar,
                ShortBy = marchingPower >= gate ? 0 : (long)Math.Round(gate - marchingPower, MidpointRounding.AwayFromZero),
                Surplus = marchingPower >= gate ? (long)Math.Round(marchingPower - gate, MidpointRounding.AwayFromZero) : 0,
                ExpectedRaidDamage = marchingPower >= raidBar
                    ? RaidResolver.ResolveDamage(marchingPower, hold)
                    : 0
            };
        }

        /// <summary>
        /// Assesses a region straight from the world, which is what the server does to judge a raid
        /// and what the client's dossier does to show one.
        ///
        /// <para>This exists so there is one answer to "what is this region's garrison worth, is it
        /// supplied, and how hard is it to break". That sum used to live in the client's
        /// <c>RegionDossier</c> alone, where the server could not reach it — and the server has to
        /// apply exactly the numbers the player was shown when they decided to march.</para>
        /// </summary>
        public static SiegeAssessment AssessRegion(
            WorldRegionData region,
            IReadOnlyCollection<WorldRegionData> allRegions,
            double marchingPower)
        {
            if (region == null) return new SiegeAssessment();

            double supply = SupplyCalculator.SupplyFor(region, allRegions);

            var assessment = Assess(
                GarrisonPowerOf(region), region.Tier, region.Entrenchment, supply, region.Resolve, marchingPower);

            assessment.GarrisonCount = GarrisonCountOf(region);
            return assessment;
        }

        /// <summary>
        /// Everything stationed in a region, added up.
        ///
        /// <para>A region's defenders are spread across its sites — each site's override carries its
        /// own garrison — so the region's defence is the sum, not any one site's.</para>
        /// </summary>
        public static double GarrisonPowerOf(WorldRegionData region)
        {
            if (region?.SiteOverrides == null) return 0;

            double total = 0;
            foreach (var over in region.SiteOverrides.Values)
            {
                if (over != null) total += over.GarrisonPower;
            }

            return total;
        }

        /// <summary>How many characters are stationed in a region, across all its sites.</summary>
        public static int GarrisonCountOf(WorldRegionData region)
        {
            if (region?.SiteOverrides == null) return 0;

            int count = 0;
            foreach (var over in region.SiteOverrides.Values)
            {
                if (over?.GarrisonCharacterIds != null) count += over.GarrisonCharacterIds.Count;
            }

            return count;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }
    }

    /// <summary>The siege block of a region's dossier: what it would take, and what you have.</summary>
    public class SiegeAssessment
    {
        public long Hold;
        public long Gate;

        /// <summary>The lower bar a raid is measured against — half the siege gate.</summary>
        public long RaidBar;

        /// <summary>Combined power of everything stationed in the region.</summary>
        public double GarrisonPower;

        /// <summary>How many characters are stationed there.</summary>
        public int GarrisonCount;

        /// <summary>1.00 when supplied, lower when the owner is cut off from their capital.</summary>
        public double Supply;

        public double MarchingPower;
        public bool ClearsGate;

        /// <summary>True when the marching party is strong enough to raid, whatever a siege would need.</summary>
        public bool ClearsRaidBar;

        /// <summary>Resolve a successful raid would cost the region. Zero when the bar is not cleared.</summary>
        public int ExpectedRaidDamage;

        /// <summary>Power still needed to reach the gate. Zero when it is already cleared.</summary>
        public long ShortBy;

        /// <summary>Power beyond the gate. Zero when it is not cleared.</summary>
        public long Surplus;

        /// <summary>True when the region's owner cannot trace a path of their own land back home.</summary>
        public bool CutOff => Supply < SupplyCalculator.Supplied;
    }
}
