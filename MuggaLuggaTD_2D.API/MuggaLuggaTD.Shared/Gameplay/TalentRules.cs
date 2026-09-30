using System;
using System.Collections.Generic;
using System.Linq;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>What a talent node raises.</summary>
    public enum TalentStat
    {
        MaxHealth,
        Damage,
        MoveSpeed,
        SignatureCooldown,
        DamageTaken,
        StatusDuration,
        GarrisonPower,
        Capstone,
    }

    /// <summary>One node of the Trainer's tree.</summary>
    public sealed class TalentNode
    {
        public string Id { get; }
        public string Name { get; }
        public TalentStat Stat { get; }
        public int MaxRank { get; }

        /// <summary>The change each rank makes, as a fraction (0.04 = 4%). Negative for a reduction.</summary>
        public double PerRank { get; }

        /// <summary>The row, from 0 at the top. A row opens once <see cref="TalentRules.RowGate"/> points are spent above it.</summary>
        public int Row { get; }

        /// <summary>What a rank does, in the Trainer's words.</summary>
        public string Effect { get; }

        public TalentNode(string id, string name, TalentStat stat, int maxRank, double perRank, int row, string effect)
        {
            Id = id; Name = name; Stat = stat; MaxRank = maxRank; PerRank = perRank; Row = row; Effect = effect;
        }
    }

    /// <summary>
    /// The Trainer (design 6c, Mike 2026-09-29: "a smaller version of it"): permanent talents, one
    /// point per character level and one more at 10, 20 and 30.
    ///
    /// <para><b>One tree for every class, of stats only.</b> The design's own tree is already small and
    /// class-free. Its one mechanic, Second Wind (heal on kill), is left out; class-specific picks are
    /// to come from feedback. The capstone, Unbreakable, is written as stats (health and damage taken)
    /// because the design gives it no effect.</para>
    ///
    /// <para><b>Shared because power is priced from it.</b> Each point spent is worth
    /// <see cref="PowerPerPoint"/> (the design's "25 power per point"), and Hold the Line raises a
    /// garrison's worth, so the server must read the same tree the Trainer shows. What a character has
    /// learnt is <b>written only by the server</b> (learn and respec), and every save is put back to
    /// that record, so a save cannot teach itself.</para>
    /// </summary>
    public static class TalentRules
    {
        public const float PowerPerPoint = 25f;

        /// <summary>Gold to unlearn everything, per character level.</summary>
        public const long RespecGoldPerLevel = 50;

        /// <summary>Levels that grant a bonus point on top of the level's own.</summary>
        public static readonly int[] BonusLevels = { 10, 20, 30 };

        public static readonly IReadOnlyList<TalentNode> Tree = new[]
        {
            new TalentNode("iron_hide", "Iron Hide", TalentStat.MaxHealth, 5, 0.04, 0, "+4% health"),
            new TalentNode("keen_edge", "Keen Edge", TalentStat.Damage, 5, 0.03, 0, "+3% damage"),
            new TalentNode("quick_feet", "Quick Feet", TalentStat.MoveSpeed, 3, 0.02, 0, "+2% speed"),
            new TalentNode("signature_focus", "Signature Focus", TalentStat.SignatureCooldown, 3, -0.04, 1, "−4% signature cooldown"),
            new TalentNode("shield_wall", "Shield Wall", TalentStat.DamageTaken, 5, -0.03, 1, "−3% damage taken"),
            new TalentNode("status_potency", "Status Potency", TalentStat.StatusDuration, 4, 0.05, 2, "+5% status duration"),
            new TalentNode("hold_the_line", "Hold the Line", TalentStat.GarrisonPower, 3, 0.05, 2, "+5% power on a garrison"),
            new TalentNode("unbreakable", "Unbreakable", TalentStat.Capstone, 1, 0, 3, "+10% health, −5% damage taken"),
        };

        /// <summary>Points that must be spent in the rows above before a row opens.</summary>
        public static int RowGate(int row)
        {
            switch (row)
            {
                case 0: return 0;
                case 1: return 10;
                default: return 20;
            }
        }

        public const double CapstoneHealth = 0.10;
        public const double CapstoneDamageTaken = -0.05;

        public static TalentNode Node(string id) => Tree.FirstOrDefault(n => n.Id == id);

        /// <summary>Points a character of <paramref name="level"/> has earned.</summary>
        public static int PointsFor(long level)
        {
            if (level < 1) return 0;
            int points = (int)Math.Min(level, CharacterProgression.MaxLevel);
            foreach (var bonus in BonusLevels)
                if (level >= bonus) points++;
            return points;
        }

        public static int RankOf(IReadOnlyDictionary<string, int> ranks, string id)
            => ranks != null && id != null && ranks.TryGetValue(id, out var r) ? Math.Max(0, r) : 0;

        /// <summary>Points spent on nodes of the tree (unknown ids count nothing).</summary>
        public static int Spent(IReadOnlyDictionary<string, int> ranks)
            => ranks == null ? 0 : Tree.Sum(n => Math.Min(n.MaxRank, RankOf(ranks, n.Id)));

        /// <summary>Points spent in the rows above <paramref name="row"/>.</summary>
        public static int SpentAbove(IReadOnlyDictionary<string, int> ranks, int row)
            => ranks == null ? 0 : Tree.Where(n => n.Row < row).Sum(n => Math.Min(n.MaxRank, RankOf(ranks, n.Id)));

        public static int Unspent(IReadOnlyDictionary<string, int> ranks, long level)
            => Math.Max(0, PointsFor(level) - Spent(ranks));

        /// <summary>
        /// Why one more rank of <paramref name="id"/> cannot be learnt, or null if it can. The words are
        /// the player's: the server sends them back as its refusal.
        /// </summary>
        public static string WhyNot(IReadOnlyDictionary<string, int> ranks, long level, string id)
        {
            var node = Node(id);
            if (node == null) return "There is no such talent.";
            if (RankOf(ranks, id) >= node.MaxRank) return $"{node.Name} is already at its highest rank.";
            if (Unspent(ranks, level) <= 0) return "No points left to spend. Each level brings one.";
            int gate = RowGate(node.Row);
            if (SpentAbove(ranks, node.Row) < gate) return $"{node.Name} opens once {gate} points are spent above it.";
            return null;
        }

        /// <summary>
        /// True if <paramref name="ranks"/> could have been learnt at <paramref name="level"/>: only
        /// known nodes, within their ranks and the points earned, every row's gate met.
        /// </summary>
        public static bool IsLegal(IReadOnlyDictionary<string, int> ranks, long level)
        {
            if (ranks == null) return true;
            foreach (var entry in ranks)
            {
                var node = Node(entry.Key);
                if (node == null || entry.Value < 0 || entry.Value > node.MaxRank) return false;
                if (entry.Value > 0 && SpentAbove(ranks, node.Row) < RowGate(node.Row)) return false;
            }
            return Spent(ranks) <= PointsFor(level);
        }

        /// <summary>Gold to unlearn every talent.</summary>
        public static long RespecCost(long level) => Math.Max(1, level) * RespecGoldPerLevel;

        public static float PowerFor(IReadOnlyDictionary<string, int> ranks) => Spent(ranks) * PowerPerPoint;

        // ---- What the ranks do. Each is a factor on the stat (1 = unchanged). ----

        private static double Sum(IReadOnlyDictionary<string, int> ranks, TalentStat stat)
            => ranks == null ? 0 : Tree.Where(n => n.Stat == stat).Sum(n => Math.Min(n.MaxRank, RankOf(ranks, n.Id)) * n.PerRank);

        private static bool HasCapstone(IReadOnlyDictionary<string, int> ranks) => RankOf(ranks, "unbreakable") > 0;

        public static float HealthFactor(IReadOnlyDictionary<string, int> ranks)
            => (float)(1 + Sum(ranks, TalentStat.MaxHealth) + (HasCapstone(ranks) ? CapstoneHealth : 0));

        public static float DamageFactor(IReadOnlyDictionary<string, int> ranks)
            => (float)(1 + Sum(ranks, TalentStat.Damage));

        public static float MoveSpeedFactor(IReadOnlyDictionary<string, int> ranks)
            => (float)(1 + Sum(ranks, TalentStat.MoveSpeed));

        /// <summary>Scales the signature's cooldown (below 1 is faster).</summary>
        public static float SignatureCooldownFactor(IReadOnlyDictionary<string, int> ranks)
            => (float)(1 + Sum(ranks, TalentStat.SignatureCooldown));

        public static float DamageTakenFactor(IReadOnlyDictionary<string, int> ranks)
            => (float)(1 + Sum(ranks, TalentStat.DamageTaken) + (HasCapstone(ranks) ? CapstoneDamageTaken : 0));

        public static float StatusDurationFactor(IReadOnlyDictionary<string, int> ranks)
            => (float)(1 + Sum(ranks, TalentStat.StatusDuration));

        /// <summary>Scales what this character is worth standing on a garrison's walls.</summary>
        public static float GarrisonPowerFactor(IReadOnlyDictionary<string, int> ranks)
            => (float)(1 + Sum(ranks, TalentStat.GarrisonPower));
    }
}
