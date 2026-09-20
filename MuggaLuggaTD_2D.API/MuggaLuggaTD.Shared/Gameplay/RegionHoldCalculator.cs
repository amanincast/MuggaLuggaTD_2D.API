using System;
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
    /// <para><b>Nothing acts on these numbers yet.</b> The dossier displays them. How a siege
    /// actually resolves — the design's declare, eight-hour muster, scheduled resolve — is still
    /// open, and the resolution rule will live beside this once it is settled.</para>
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

            return new SiegeAssessment
            {
                Hold = hold,
                Gate = gate,
                MarchingPower = marchingPower,
                ClearsGate = marchingPower >= gate,
                ShortBy = marchingPower >= gate ? 0 : (long)Math.Round(gate - marchingPower, MidpointRounding.AwayFromZero),
                Surplus = marchingPower >= gate ? (long)Math.Round(marchingPower - gate, MidpointRounding.AwayFromZero) : 0
            };
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
        public double MarchingPower;
        public bool ClearsGate;

        /// <summary>Power still needed to reach the gate. Zero when it is already cleared.</summary>
        public long ShortBy;

        /// <summary>Power beyond the gate. Zero when it is not cleared.</summary>
        public long Surplus;
    }
}
