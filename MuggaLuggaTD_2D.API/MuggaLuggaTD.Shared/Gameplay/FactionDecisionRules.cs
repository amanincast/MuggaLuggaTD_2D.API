using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>What a faction may do on its turn (<c>docs/design/npc-factions.md</c> §4).</summary>
    public enum FactionAction
    {
        /// <summary>It leans elsewhere this time, or nothing it could do was worth it.</summary>
        None = 0,
        Raid = 1,

        Siege = 2,

        /// <summary>Phase 4.</summary>
        Expand = 3,

        /// <summary>Phase 4.</summary>
        Fortify = 4
    }

    /// <summary>
    /// A faction's character, from the server's <c>FactionData.json</c>: how often it acts and what it
    /// leans toward. A lean, not a limit (Mike, 2026-10-06): every faction may do every action.
    /// </summary>
    public class FactionTemperament
    {
        public FactionId Id;
        public string Name = string.Empty;

        /// <summary>A few words for the dossier: "raids often".</summary>
        public string Lean = string.Empty;

        /// <summary>How often it acts, against an average of 1.</summary>
        public double Aggression = 1.0;

        public double Raid = 1.0;
        public double Siege;
        public double Expand;
        public double Fortify;

        public double WeightOf(FactionAction action)
        {
            switch (action)
            {
                case FactionAction.Raid: return Math.Max(0, Raid);
                case FactionAction.Siege: return Math.Max(0, Siege);
                case FactionAction.Expand: return Math.Max(0, Expand);
                case FactionAction.Fortify: return Math.Max(0, Fortify);
                default: return 0;
            }
        }
    }

    /// <summary>A region a faction could raid, and what it would face there.</summary>
    public struct FactionRaidTarget
    {
        public WorldRegionData Region;
        public long Hold;
        public double March;

        /// <summary>March over hold: the higher, the easier the raid.</summary>
        public double Ease => Hold <= 0 ? double.MaxValue : March / Hold;
    }

    /// <summary>
    /// How a faction decides to act (<c>docs/design/npc-factions.md</c> §4; Mike, 2026-10-06).
    ///
    /// <para>The server sweeps every <see cref="SweepInterval"/>. A faction that is not Bloodied and
    /// stands at <see cref="FactionStrengthRules.ActThreshold"/> or more of its cap may act, with a
    /// chance that rises with its readiness and its aggression. It picks an action by its temperament,
    /// then a target among the regions <b>bordering its own land</b>: whoever is close, player or
    /// faction, and nobody further away.</para>
    /// </summary>
    public static class FactionDecisionRules
    {
        public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

        /// <summary>
        /// The chance a full-strength faction of aggression 1 acts on one sweep: about once every
        /// six hours (the plan's "every 4-8h").
        /// </summary>
        public const double BaseChancePerSweep = 1.0 / 24.0;

        /// <summary>A won raid costs this share of the march.</summary>
        public const double WonRaidLoss = 0.10;

        /// <summary>A repelled raid costs this share of the march, and Bloodies the faction.</summary>
        public const double LostRaidLoss = 0.50;

        /// <summary>The actions built so far. A lean toward one not yet built is a turn spent on nothing.</summary>
        public static readonly IReadOnlyList<FactionAction> Built = new[] { FactionAction.Raid, FactionAction.Siege };

        /// <summary>
        /// The chance of acting on one sweep. Nothing below the threshold, while Bloodied, or while its
        /// own siege musters (one army); from there it rises from half to the full base chance as
        /// readiness reaches 100%, times aggression.
        /// </summary>
        public static double ChanceToAct(double readiness, double aggression, bool bloodied, bool mustering = false)
        {
            if (bloodied || mustering || readiness < FactionStrengthRules.ActThreshold) return 0;
            double span = 1.0 - FactionStrengthRules.ActThreshold;
            double over = span <= 0 ? 1 : Math.Min(1, (readiness - FactionStrengthRules.ActThreshold) / span);
            return Math.Min(1, BaseChancePerSweep * Math.Max(0, aggression) * (0.5 + 0.5 * over));
        }

        /// <summary>
        /// Picks an action by temperament, from a roll in [0, 1). Leans toward actions not built yet
        /// count in the roll and come back <see cref="FactionAction.None"/>, so an entrenching
        /// faction raids less now and does its own thing once that is built.
        /// </summary>
        public static FactionAction PickAction(FactionTemperament temperament, double roll)
        {
            var all = new[] { FactionAction.Raid, FactionAction.Siege, FactionAction.Expand, FactionAction.Fortify };
            double total = 0;
            foreach (var a in all) total += temperament.WeightOf(a);
            if (total <= 0) return FactionAction.None;

            double at = Math.Max(0, Math.Min(0.999999, roll)) * total;
            foreach (var a in all)
            {
                double w = temperament.WeightOf(a);
                if (at < w)
                {
                    foreach (var built in Built)
                        if (built == a) return a;
                    return FactionAction.None;
                }
                at -= w;
            }
            return FactionAction.None;
        }

        /// <summary>
        /// The regions next to <paramref name="faction"/>'s land that it does not hold itself. Neutral
        /// land is among them (expansion, phase 4); a raid narrows them further.
        /// </summary>
        public static List<WorldRegionData> Bordering(FactionId faction, IReadOnlyCollection<WorldRegionData> regions)
        {
            var byHex = new Dictionary<HexCoord, WorldRegionData>();
            foreach (var r in regions) byHex[r.Hex] = r;

            var seen = new HashSet<string>();
            var result = new List<WorldRegionData>();
            foreach (var own in regions)
            {
                if (!FactionStrengthRules.Holds(faction, own)) continue;
                foreach (var hex in own.Hex.Neighbours())
                {
                    if (!byHex.TryGetValue(hex, out var next)) continue;
                    if (FactionStrengthRules.Holds(faction, next)) continue;
                    if (seen.Add(next.RegionId)) result.Add(next);
                }
            }
            return result;
        }

        /// <summary>
        /// Whether a faction may raid this bordering region: a player's land that is not their seat
        /// and not under truce, or another faction's. Unclaimed land is taken, not raided.
        /// </summary>
        public static bool IsRaidable(FactionId faction, WorldRegionData region, DateTime utcNow)
        {
            if (region == null || FactionStrengthRules.Holds(faction, region)) return false;

            bool player = region.Ownership == LocationOwnership.Player && !string.IsNullOrEmpty(region.OwnerUserId);
            bool rival = FactionStrengthRules.IsFaction(region.Faction) && string.IsNullOrEmpty(region.OwnerUserId);
            if (!player && !rival) return false;
            if (region.IsCapital) return false;
            if (SiegeRules.IsUnderTruce(region, utcNow)) return false;
            return true;
        }

        /// <summary>What a raid marches with: a share of the faction's strength.</summary>
        public static double RaidMarch(double strength) => Math.Max(0, strength) * FactionStrengthRules.RaidShare;

        /// <summary>A Bloodied faction's land holds at a quarter less (matching <see cref="BloodiedRules.Penalty"/>).</summary>
        public static long DefendingHold(long hold, bool defenderIsBloodiedFaction) =>
            defenderIsBloodiedFaction ? (long)Math.Round(hold * (1.0 - BloodiedRules.Penalty), MidpointRounding.AwayFromZero) : hold;

        /// <summary>
        /// Picks among targets the march clears the raid bar of, weighted toward the easiest, from a
        /// roll in [0, 1). Null when it can beat none of them.
        /// </summary>
        public static FactionRaidTarget? PickRaidTarget(IReadOnlyList<FactionRaidTarget> candidates, double roll)
        {
            if (candidates == null) return null;
            double total = 0;
            var clear = new List<FactionRaidTarget>();
            foreach (var c in candidates)
            {
                if (!RaidResolver.ClearsBar(c.March, c.Hold)) continue;
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

        /// <summary>The strength a raid costs: a tenth of the march if it lands, half if it is repelled.</summary>
        public static double RaidCost(double march, bool won) => march * (won ? WonRaidLoss : LostRaidLoss);
    }
}
