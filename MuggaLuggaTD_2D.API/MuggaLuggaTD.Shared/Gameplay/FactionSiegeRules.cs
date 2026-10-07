using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>How a faction's siege ended at muster close.</summary>
    public struct FactionSiegeResult
    {
        /// <summary>True when the region fell to the faction.</summary>
        public bool Fell;

        /// <summary>True when the defender had raised the hold past the faction's gate: no roll was made.</summary>
        public bool BelowGate;

        public int D20Roll;
        public int Total;
    }

    /// <summary>
    /// A faction's siege (<c>docs/design/npc-factions.md</c> §5, phase 3; Mike, 2026-10-06).
    ///
    /// <para>It mirrors a player's siege: only on land whose resolve is worn to
    /// <see cref="SiegeRules.DeclareResolveThreshold"/> or below, only with a march (<see
    /// cref="FactionStrengthRules.SiegeShare"/> of its strength) that clears the gate, and with an
    /// <see cref="SiegeRules.MusterHours"/> muster the defender sees coming. Nobody fights the assault,
    /// so the server <b>settles it at muster close</b>: the frozen march against the frozen hold.
    /// A hold the defender has raised past the gate turns it away outright; otherwise it goes to
    /// <see cref="PassivePvPResolver"/>, the dice every passive contest uses.</para>
    ///
    /// <para><b>Break the siege</b> is the defender's active answer: once during the muster they may
    /// sally out and fight the besieging army, sized from its march (<see cref="SortieEncounter"/>).
    /// Win and the siege is broken; lose and it goes ahead.</para>
    /// </summary>
    public static class FactionSiegeRules
    {
        /// <summary>A siege that takes the region brings most of its march home.</summary>
        public const double WonSiegeLoss = 0.10;

        /// <summary>A siege that fails, at the walls or to a sortie, loses its whole march.</summary>
        public const double LostSiegeLoss = 1.0;

        /// <summary>The warband's captains, who fight a sortie as elites: one per this much march.</summary>
        public const double MarchPerCaptain = 2000;

        public const int MinimumCaptains = 1;
        public const int MaximumCaptains = 4;

        /// <summary>What a siege marches with: a share of the faction's strength.</summary>
        public static double SiegeMarch(double strength) => Math.Max(0, strength) * FactionStrengthRules.SiegeShare;

        /// <summary>The strength a siege costs: a tenth of the march if the region falls, all of it if not.</summary>
        public static double SiegeCost(double march, bool fell) => Math.Max(0, march) * (fell ? WonSiegeLoss : LostSiegeLoss);

        /// <summary>
        /// Whether a faction may besiege this bordering region: a player's land that is not their seat,
        /// or another faction's (phase 4), not under truce, and worn to the resolve gate.
        /// </summary>
        public static bool IsBesiegeable(FactionId faction, WorldRegionData region, DateTime utcNow)
        {
            if (region == null || FactionStrengthRules.Holds(faction, region)) return false;
            bool player = region.Ownership == LocationOwnership.Player && !string.IsNullOrEmpty(region.OwnerUserId);
            bool rival = FactionStrengthRules.IsFaction(region.Faction) && string.IsNullOrEmpty(region.OwnerUserId);
            if (!player && !rival) return false;
            if (region.IsCapital) return false;
            if (SiegeRules.IsUnderTruce(region, utcNow)) return false;
            return region.Resolve <= SiegeRules.DeclareResolveThreshold;
        }

        /// <summary>
        /// Picks among targets whose gate the march clears, weighted toward the easiest, from a roll
        /// in [0, 1). Null when it clears none. <see cref="FactionRaidTarget.March"/> is the siege march.
        /// </summary>
        public static FactionRaidTarget? PickSiegeTarget(IReadOnlyList<FactionRaidTarget> candidates, double roll)
        {
            if (candidates == null) return null;
            double total = 0;
            var clear = new List<FactionRaidTarget>();
            foreach (var c in candidates)
            {
                if (!RegionHoldCalculator.ClearsGate(c.March, c.Hold)) continue;
                clear.Add(c);
                total += Math.Min(10, c.Ease);
            }
            if (clear.Count == 0) return null;

            double at = Math.Max(0, Math.Min(0.999999, roll)) * total;
            foreach (var c in clear)
            {
                double w = Math.Min(10, c.Ease);
                if (at < w) return c;
                at -= w;
            }
            return clear[clear.Count - 1];
        }

        /// <summary>
        /// The siege's assault at muster close, with the server's d20. A hold raised past the gate
        /// turns the army away without a roll: that is what mustering is for.
        /// </summary>
        public static FactionSiegeResult Settle(double march, long frozenHold, int d20Roll)
        {
            if (!RegionHoldCalculator.ClearsGate(march, frozenHold))
                return new FactionSiegeResult { Fell = false, BelowGate = true };

            var fight = PassivePvPResolver.Resolve((float)march, frozenHold, d20Roll);
            return new FactionSiegeResult { Fell = fight.AttackerWins, D20Roll = fight.D20Roll, Total = fight.Total };
        }

        /// <summary>The warband's captains in a sortie, by its march.</summary>
        public static int CaptainsOf(double march)
        {
            int captains = (int)Math.Round(Math.Max(0, march) / MarchPerCaptain, MidpointRounding.AwayFromZero);
            return Math.Max(MinimumCaptains, Math.Min(MaximumCaptains, captains));
        }

        /// <summary>
        /// The sortie: the defender's party marching out against the besiegers. It is a siege assault
        /// turned round (<see cref="SiegeAssaultRules.EncounterFor"/>): the faction's march stands
        /// where a hold would, so a party that is small beside the warband faces the full eight waves
        /// at its own level, and one that outclasses it walks over it.
        /// </summary>
        public static SiegeEncounter SortieEncounter(double sortiePower, double march, int armySize)
        {
            long asHold = (long)Math.Round(Math.Max(1, march), MidpointRounding.AwayFromZero);
            return SiegeAssaultRules.EncounterFor(sortiePower, asHold, armySize, CaptainsOf(march));
        }
    }
}
