using System;
using System.Collections.Generic;
using System.Linq;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>A perk a worker rolls as they level (spec "Workers: veterans who grow with you" §3.3).</summary>
    public enum WorkerPerk : short
    {
        // Minor
        SteadyHands = 0,
        Stalwart = 1,
        QuickStudy = 2,
        Homebody = 3,
        // Major
        Packhorse = 4,
        Mentor = 5,
        OldHand = 6,
        JackOfTrades = 7,
        // Great
        KeenEye = 8,
        Overseer = 9,
        Tireless = 10,
        LuckyStrike = 11
    }

    public enum PerkGrade : short
    {
        Minor = 0,
        Major = 1,
        Great = 2
    }

    /// <summary>What a level's roll came to, for the reveal.</summary>
    public enum WorkerRollKind : short
    {
        Perk = 0,
        Promotion = 1,
        NoPromotion = 2,
        /// <summary>A Master at a promotion level: a bonus perk instead.</summary>
        BonusPerk = 3,
        NoBonusPerk = 4
    }

    /// <summary>One level's roll, as stored and as revealed.</summary>
    public sealed class WorkerRollResult
    {
        public int Level;
        public WorkerRollKind Kind;
        public WorkerPerk? Perk;
        /// <summary>The tier a promotion reached.</summary>
        public WorkerTier? NewTier;
        /// <summary>A trait a promotion added to reach the new tier's count.</summary>
        public WorkerTrait? NewTrait;
        /// <summary>A second trade gained with the roll (Jack of Trades, or a Versatile promotion).</summary>
        public ResourceTrade? NewSecondTrade;
    }

    /// <summary>
    /// Everything about a worker that their output depends on. The server builds it from its row; the
    /// client from the Hiring Hall's answer, so both work out the same rate.
    /// </summary>
    public sealed class WorkerSheet
    {
        public WorkerTier Tier;
        public ResourceTrade Trade;
        public ResourceTrade? SecondTrade;
        public IReadOnlyCollection<WorkerTrait> Traits = Array.Empty<WorkerTrait>();
        public IReadOnlyCollection<WorkerPerk> Perks = Array.Empty<WorkerPerk>();
        public BiomeType HomeBiome;
        public int Level = 1;

        public bool Has(WorkerTrait trait) => Traits != null && Traits.Contains(trait);
        public bool Has(WorkerPerk perk) => Perks != null && Perks.Contains(perk);
    }

    /// <summary>
    /// Workers who grow (spec "Workers: veterans who grow with you", approved 2026-10-08 with Mike's
    /// changes: perks are <b>rolled</b>, weighted by tier, and a worker may be <b>promoted</b> a tier at
    /// levels 5 and 10).
    ///
    /// <para><b>Experience is time at work.</b> Each hour assigned to a site is an hour of experience
    /// (more with Quick Study or a Mentor beside them). The level is derived from the hours, never
    /// stored. Every level adds 4% output.</para>
    ///
    /// <para><b>The rolls are the server's.</b> They are made from (worker, level, season) with
    /// <see cref="DeterministicRandom"/> and stored, so a level's roll is the same however the settle
    /// that crossed it was timed, and a veteran re-reaching a level in a new season rolls afresh.</para>
    /// </summary>
    public static class WorkerLevelRules
    {
        public const int MaxLevel = 10;

        /// <summary>Hours of experience to reach each level, by level (index 0 unused). Tune.</summary>
        private static readonly double[] HoursFor = { 0, 0, 6, 18, 36, 60, 96, 144, 216, 312, 432 };

        public const double OutputPerLevel = 0.04;
        public const double PromotionChance = 0.5;
        public const double BonusPerkChance = 0.5;

        public static readonly int[] PerkLevels = { 3, 6, 9 };
        public static readonly int[] PromotionLevels = { 5, 10 };

        // The perks' numbers (tune).
        public const double SteadyHandsFactor = 1.08;
        public const double StalwartHarriedFactor = 0.75;
        public const double QuickStudyFactor = 1.25;
        public const double HomebodyFactor = 1.12;
        public const double PackhorseLuckyFactor = 2.5;
        public const int PackhorseLuckyOneIn = 15;
        public const double MentorFactor = 1.25;
        public const double OldHandFactor = 1.10;
        public static readonly TimeSpan OldHandAfter = TimeSpan.FromHours(24);
        public const double JackOfTradesFactor = 0.75;
        public const double JackAndVersatileFactor = 1.0;
        public const int KeenEyeOneIn = 12;
        public const double OverseerBonus = 0.10;
        public const double OverseerForemanBonus = 0.15;
        public const double TirelessFactor = 1.20;
        public const int LuckyStrikeOneIn = 48;

        /// <summary>What Lucky Strike turns up (tune).</summary>
        public const string LuckyStrikeFind = "Common Shard";

        /// <summary>The level reached with this many hours of experience.</summary>
        public static int LevelFor(double hours)
        {
            int level = 1;
            for (int l = 2; l <= MaxLevel; l++)
                if (hours >= HoursFor[l]) level = l;
            return level;
        }

        /// <summary>The hours of experience a level starts at.</summary>
        public static double HoursAt(int level) => HoursFor[Math.Max(1, Math.Min(MaxLevel, level))];

        /// <summary>The hours of experience the next level needs, or null at the top.</summary>
        public static double? NextLevelAt(int level) => level >= MaxLevel ? (double?)null : HoursFor[level + 1];

        public static double LevelFactor(int level) => 1.0 + OutputPerLevel * (Math.Max(1, level) - 1);

        public static bool IsPerkLevel(int level) => Array.IndexOf(PerkLevels, level) >= 0;
        public static bool IsPromotionLevel(int level) => Array.IndexOf(PromotionLevels, level) >= 0;
        public static bool RollsAt(int level) => IsPerkLevel(level) || IsPromotionLevel(level);

        /// <summary>Each perk's grade.</summary>
        public static PerkGrade GradeOf(WorkerPerk perk) =>
            (short)perk >= (short)WorkerPerk.KeenEye ? PerkGrade.Great
            : (short)perk >= (short)WorkerPerk.Packhorse ? PerkGrade.Major
            : PerkGrade.Minor;

        public static IEnumerable<WorkerPerk> PerksOf(PerkGrade grade) =>
            Enum.GetValues(typeof(WorkerPerk)).Cast<WorkerPerk>().Where(p => GradeOf(p) == grade);

        /// <summary>Grade weights (Minor, Major, Great) by the worker's tier, mirroring the board's 60/30/10.</summary>
        public static int[] GradeWeights(WorkerTier tier) =>
            tier == WorkerTier.Master ? new[] { 20, 45, 35 }
            : tier == WorkerTier.Skilled ? new[] { 40, 40, 20 }
            : new[] { 60, 30, 10 };

        /// <summary>How many traits a tier carries: a promotion rolls new ones up to this.</summary>
        public static int TraitsFor(WorkerTier tier) => tier == WorkerTier.Master ? 2 : tier == WorkerTier.Skilled ? 1 : 0;

        // -----------------------------------------------------------------
        // Output
        // -----------------------------------------------------------------

        /// <summary>The bonus this worker gives everyone else at their site: a Foreman's, or an Overseer's.</summary>
        public static double ForemanBonusOf(WorkerSheet worker)
        {
            bool foreman = worker.Has(WorkerTrait.Foreman), overseer = worker.Has(WorkerPerk.Overseer);
            if (foreman && overseer) return OverseerForemanBonus;
            return foreman || overseer ? OverseerBonus : 0;
        }

        /// <summary>How much of their rate a worker gathers at their second trade.</summary>
        public static double SecondTradeFactor(WorkerSheet worker)
        {
            bool jack = worker.Has(WorkerPerk.JackOfTrades);
            if (jack && worker.Has(WorkerTrait.Versatile)) return JackAndVersatileFactor;
            return jack ? JackOfTradesFactor : HiringRules.SecondTradeFactor;
        }

        /// <summary>
        /// A worker's goods an hour at a site, with their traits, perks and level. <paramref name="foremanBonus"/>
        /// is the sum of <see cref="ForemanBonusOf"/> over the <i>other</i> workers there. Old Hand is
        /// not in it: it depends on time, so <see cref="Gathered"/> applies it by the hour.
        /// </summary>
        public static double RateAt(WorkerSheet worker, ResourceTrade siteTrade, BiomeType siteBiome, int siteTier, double foremanBonus)
        {
            if (!HiringRules.CanWork(worker.Trade, worker.SecondTrade, siteTrade)) return 0;

            double rate = HiringRules.BaseRate(worker.Tier);
            if (worker.Trade != siteTrade) rate *= SecondTradeFactor(worker);
            if (worker.Has(WorkerTrait.Steady)) rate *= HiringRules.SteadyFactor;
            if (worker.Has(WorkerTrait.Hometown) && worker.HomeBiome == siteBiome) rate *= HiringRules.HometownFactor;
            if (worker.Has(WorkerTrait.Prospector) && siteTier >= HiringRules.ProspectorMinimumTier) rate *= HiringRules.ProspectorFactor;
            if (worker.Has(WorkerPerk.SteadyHands)) rate *= SteadyHandsFactor;
            if (worker.Has(WorkerPerk.Homebody) && worker.HomeBiome == siteBiome) rate *= HomebodyFactor;
            if (worker.Has(WorkerPerk.Tireless)) rate *= TirelessFactor;
            if (foremanBonus > 0) rate *= 1.0 + foremanBonus;
            return rate * LevelFactor(worker.Level);
        }

        /// <summary>Whether an hour is lucky for this worker: Lucky's 1 in 10, or Packhorse's 1 in 15 without it.</summary>
        public static bool IsLuckyHour(WorkerSheet worker, string workerId, long hour)
        {
            if (worker.Has(WorkerTrait.Lucky)) return HiringRules.IsLuckyHour(workerId, hour);
            return worker.Has(WorkerPerk.Packhorse) && Naming.Hash($"{workerId}:pack:{hour}") % PackhorseLuckyOneIn == 0;
        }

        /// <summary>
        /// Goods gathered at <paramref name="ratePerHour"/> between two instants, hour by hour: lucky
        /// hours (×2, Packhorse ×2.5), hours raiders harried (×0.5, Stalwart ×0.75), and Old Hand's
        /// +10% once the worker has been at this site <see cref="OldHandAfter"/> (from <paramref name="assignedAt"/>).
        /// </summary>
        public static double Gathered(double ratePerHour, DateTime from, DateTime to, WorkerSheet worker, string workerId,
            Func<long, bool> harried, DateTime? assignedAt)
            => Gathered(ratePerHour, from, to, worker, workerId, harried, assignedAt, null);

        /// <summary>
        /// As above, with <paramref name="goodsFactor"/>: what the hour itself does to the region's goods
        /// (a Harvest Fair's +50%, <see cref="RegionConditionRules.GoodsFactor"/>). Null for ordinary hours.
        /// </summary>
        public static double Gathered(double ratePerHour, DateTime from, DateTime to, WorkerSheet worker, string workerId,
            Func<long, bool> harried, DateTime? assignedAt, Func<long, double> goodsFactor)
        {
            if (ratePerHour <= 0 || to <= from) return 0;
            DateTime? oldHandFrom = worker.Has(WorkerPerk.OldHand) && assignedAt.HasValue ? assignedAt + OldHandAfter : null;
            bool luckable = worker.Has(WorkerTrait.Lucky) || worker.Has(WorkerPerk.Packhorse);
            if (!luckable && harried == null && oldHandFrom == null && goodsFactor == null) return ratePerHour * (to - from).TotalHours;

            double luckyFactor = worker.Has(WorkerPerk.Packhorse) ? PackhorseLuckyFactor : 2;
            double harriedFactor = worker.Has(WorkerPerk.Stalwart) ? StalwartHarriedFactor : HarassmentRules.HarriedFactor;
            double total = 0;
            long first = from.Ticks / TimeSpan.TicksPerHour, last = (to.Ticks - 1) / TimeSpan.TicksPerHour;
            for (long h = first; h <= last; h++)
            {
                var start = new DateTime(Math.Max(from.Ticks, h * TimeSpan.TicksPerHour), DateTimeKind.Utc);
                var end = new DateTime(Math.Min(to.Ticks, (h + 1) * TimeSpan.TicksPerHour), DateTimeKind.Utc);
                double factor = luckable && IsLuckyHour(worker, workerId, h) ? luckyFactor : 1;
                if (harried != null && harried(h)) factor *= harriedFactor;
                if (goodsFactor != null) factor *= goodsFactor(h);

                if (oldHandFrom.HasValue && end > oldHandFrom.Value)
                {
                    // The part of the hour before the 24 h mark is plain, the rest is Old Hand's.
                    var mark = new DateTime(Math.Max(start.Ticks, oldHandFrom.Value.Ticks), DateTimeKind.Utc);
                    total += ratePerHour * factor * ((mark - start).TotalHours + (end - mark).TotalHours * OldHandFactor);
                }
                else
                    total += ratePerHour * factor * (end - start).TotalHours;
            }
            return total;
        }

        /// <summary>
        /// What Keen Eye and Lucky Strike turned up over a stretch: one find per qualifying hour that
        /// <i>ended</i> within it, so consecutive settles never count an hour twice. Material name to count.
        /// </summary>
        public static Dictionary<string, int> Finds(WorkerSheet worker, string workerId, DateTime from, DateTime to, BiomeType siteBiome)
        {
            var finds = new Dictionary<string, int>();
            bool keen = worker.Has(WorkerPerk.KeenEye), strike = worker.Has(WorkerPerk.LuckyStrike);
            if ((!keen && !strike) || to <= from) return finds;

            string keenFind = worker.Tier >= WorkerTier.Skilled
                ? QuestRules.MinorCrystal(TavernRules.FavouredAffinity(siteBiome) ?? global::Enums.AffinityTypes.Physical)
                : QuestRules.Essence;
            // Hours whose end lies in (from, to].
            long first = from.Ticks / TimeSpan.TicksPerHour, last = to.Ticks / TimeSpan.TicksPerHour - 1;
            for (long h = first; h <= last; h++)
            {
                if (keen && Naming.Hash($"{workerId}:keen:{h}") % KeenEyeOneIn == 0)
                    finds[keenFind] = finds.TryGetValue(keenFind, out var n) ? n + 1 : 1;
                if (strike && Naming.Hash($"{workerId}:strike:{h}") % LuckyStrikeOneIn == 0)
                    finds[LuckyStrikeFind] = finds.TryGetValue(LuckyStrikeFind, out var m) ? m + 1 : 1;
            }
            return finds;
        }

        /// <summary>How fast a worker gains experience: Quick Study, and a Mentor among the others at the site.</summary>
        public static double ExperienceFactor(WorkerSheet worker, bool mentorBeside)
            => (worker.Has(WorkerPerk.QuickStudy) ? QuickStudyFactor : 1) * (mentorBeside ? MentorFactor : 1);

        // -----------------------------------------------------------------
        // The rolls
        // -----------------------------------------------------------------

        /// <summary>The stream a (worker, level, season) roll draws from.</summary>
        public static DeterministicRandom StreamFor(string workerId, int level, int season)
            => DeterministicRandom.ForSubject(Naming.Hash(workerId ?? ""), ((ulong)(uint)season << 32) | (uint)level);

        /// <summary>
        /// The roll a worker makes on reaching <paramref name="level"/>, or null if the level brings none.
        /// It only <i>decides</i>; the caller applies it (<see cref="Apply"/>). <paramref name="forcePromotion"/>
        /// is the debug window's "force the next promotion".
        /// </summary>
        public static WorkerRollResult Roll(string workerId, int level, int season, WorkerSheet worker, bool? forcePromotion = null)
        {
            if (!RollsAt(level)) return null;
            var random = StreamFor(workerId, level, season);

            if (IsPerkLevel(level))
                return PerkRoll(ref random, worker, level, WorkerRollKind.Perk);

            // A promotion level. A Master has nowhere to go: a chance of a bonus perk instead.
            bool lucky = random.Next(1000) < (int)Math.Round((worker.Tier == WorkerTier.Master ? BonusPerkChance : PromotionChance) * 1000);
            if (forcePromotion.HasValue) lucky = forcePromotion.Value;
            if (worker.Tier == WorkerTier.Master)
                return lucky
                    ? PerkRoll(ref random, worker, level, WorkerRollKind.BonusPerk)
                    : new WorkerRollResult { Level = level, Kind = WorkerRollKind.NoBonusPerk };
            if (!lucky)
                return new WorkerRollResult { Level = level, Kind = WorkerRollKind.NoPromotion };

            var tier = (WorkerTier)((short)worker.Tier + 1);
            var result = new WorkerRollResult { Level = level, Kind = WorkerRollKind.Promotion, NewTier = tier };
            // One new trait reaches the new tier's count from the one below (a Local with none gains one).
            if (worker.Traits.Count < TraitsFor(tier))
            {
                var pool = Enum.GetValues(typeof(WorkerTrait)).Cast<WorkerTrait>().Where(t => !worker.Has(t)).ToList();
                if (pool.Count > 0)
                {
                    result.NewTrait = pool[random.Next(pool.Count)];
                    if (result.NewTrait == WorkerTrait.Versatile && worker.SecondTrade == null)
                        result.NewSecondTrade = OtherTrade(ref random, worker.Trade);
                }
            }
            return result;
        }

        private static WorkerRollResult PerkRoll(ref DeterministicRandom random, WorkerSheet worker, int level, WorkerRollKind kind)
        {
            var weights = GradeWeights(worker.Tier);
            int pick = random.Next(weights.Sum()), grade = 0;
            while (grade < 2 && pick >= weights[grade]) pick -= weights[grade++];

            // No duplicates: a grade used up falls to the next down, then up.
            var order = new List<int> { grade };
            for (int g = grade - 1; g >= 0; g--) order.Add(g);
            for (int g = grade + 1; g <= 2; g++) order.Add(g);
            foreach (int g in order)
            {
                var pool = PerksOf((PerkGrade)g).Where(p => !worker.Has(p)).ToList();
                if (pool.Count == 0) continue;
                var perk = pool[random.Next(pool.Count)];
                var result = new WorkerRollResult { Level = level, Kind = kind, Perk = perk };
                if (perk == WorkerPerk.JackOfTrades && worker.SecondTrade == null)
                    result.NewSecondTrade = OtherTrade(ref random, worker.Trade);
                return result;
            }
            // Every perk taken (cannot happen with 12 perks and at most 5 rolls): nothing to give.
            return new WorkerRollResult { Level = level, Kind = kind == WorkerRollKind.BonusPerk ? WorkerRollKind.NoBonusPerk : kind };
        }

        private static ResourceTrade OtherTrade(ref DeterministicRandom random, ResourceTrade trade)
        {
            var others = ResourceNodeRules.Trades.Where(t => t != trade).ToList();
            return others[random.Next(others.Count)];
        }

        /// <summary>Applies a roll to a sheet: the perk, the promotion's tier and trait, a second trade.</summary>
        public static void Apply(WorkerSheet worker, WorkerRollResult roll)
        {
            if (roll == null) return;
            if (roll.Perk.HasValue && !worker.Has(roll.Perk.Value))
                worker.Perks = worker.Perks.Concat(new[] { roll.Perk.Value }).ToList();
            if (roll.NewTier.HasValue) worker.Tier = roll.NewTier.Value;
            if (roll.NewTrait.HasValue && !worker.Has(roll.NewTrait.Value))
                worker.Traits = worker.Traits.Concat(new[] { roll.NewTrait.Value }).ToList();
            if (roll.NewSecondTrade.HasValue && worker.SecondTrade == null) worker.SecondTrade = roll.NewSecondTrade;
        }

        // -----------------------------------------------------------------
        // The season's end
        // -----------------------------------------------------------------

        public const int VeteransKept = 2;

        /// <summary>A worker's standing for the carry-over choice.</summary>
        public sealed class Candidate
        {
            public string Id;
            public bool Keep;
            public double Hours;
        }

        /// <summary>
        /// Who goes with a player into the new season: the ★ KEEP workers (at most two), then the highest
        /// levels, then the most hours. Ids in that order.
        /// </summary>
        public static List<string> Veterans(IEnumerable<Candidate> workers) =>
            workers.OrderByDescending(w => w.Keep)
                .ThenByDescending(w => LevelFor(w.Hours))
                .ThenByDescending(w => w.Hours)
                .ThenBy(w => w.Id, StringComparer.Ordinal)
                .Take(VeteransKept)
                .Select(w => w.Id)
                .ToList();

        /// <summary>The level a veteran returns at: half, rounded down, at least 1.</summary>
        public static int CarriedLevel(int level) => Math.Max(1, level / 2);

        // -----------------------------------------------------------------
        // Words
        // -----------------------------------------------------------------

        public static string NameOf(WorkerPerk perk)
        {
            switch (perk)
            {
                case WorkerPerk.SteadyHands: return "Steady Hands";
                case WorkerPerk.QuickStudy: return "Quick Study";
                case WorkerPerk.OldHand: return "Old Hand";
                case WorkerPerk.JackOfTrades: return "Jack of Trades";
                case WorkerPerk.KeenEye: return "Keen Eye";
                case WorkerPerk.LuckyStrike: return "Lucky Strike";
                default: return perk.ToString();
            }
        }

        /// <summary>A one-line description of a perk, for both sides' screens.</summary>
        public static string Describe(WorkerPerk perk, WorkerTier tier = WorkerTier.Local)
        {
            switch (perk)
            {
                case WorkerPerk.SteadyHands: return "Steady Hands: +8% goods.";
                case WorkerPerk.Stalwart: return "Stalwart: raiders slow them by only a quarter, not a half.";
                case WorkerPerk.QuickStudy: return "Quick Study: +25% experience.";
                case WorkerPerk.Homebody: return "Homebody: +12% goods in their home land.";
                case WorkerPerk.Packhorse: return "Packhorse: a lucky hour pays ×2.5, and one hour in 15 is lucky even without Lucky.";
                case WorkerPerk.Mentor: return "Mentor: the others at their site gain experience 25% faster.";
                case WorkerPerk.OldHand: return "Old Hand: +10% goods once a day at the same site.";
                case WorkerPerk.JackOfTrades: return "Jack of Trades: works a second trade at three-quarters rate (full rate if Versatile).";
                case WorkerPerk.KeenEye:
                    return tier >= WorkerTier.Skilled
                        ? "Keen Eye: about one hour in 12 also turns up a Minor Crystal of the land's affinity."
                        : "Keen Eye: about one hour in 12 also turns up a Lesser Essence.";
                case WorkerPerk.Overseer: return "Overseer: +10% to everyone else at their site (+15% if a Foreman too).";
                case WorkerPerk.Tireless: return "Tireless: +20% goods.";
                default: return "Lucky Strike: about one hour in 48 turns up a Common Shard.";
            }
        }
    }
}
