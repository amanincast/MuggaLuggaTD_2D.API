using System;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// How hard a region is to take (design 7d).
    ///
    /// <code>
    /// Hold = garrison x entrench x supply x (resolve / 100)
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

        /// <summary>
        /// A region's hold, rounded the way the dossier shows it.
        /// The design's worked example: 5,640 garrison x 1.60 entrench x 1.00 supply x 100% resolve
        /// = 9,024.
        /// </summary>
        public static long Hold(double garrisonPower, int entrenchment, double supply, int resolve)
        {
            if (garrisonPower <= 0) return 0;

            double clampedResolve = Clamp(resolve, 0, 100) / 100.0;
            double value = garrisonPower
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
        public static SiegeAssessment Assess(double garrisonPower, int entrenchment, double supply, int resolve, double marchingPower)
        {
            long hold = Hold(garrisonPower, entrenchment, supply, resolve);
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
