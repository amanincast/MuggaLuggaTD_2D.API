using System;
using System.Collections.Generic;
using System.Linq;
using Enums;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// The Weekly Trial (Active Content E; Mike 2026-10-08: "cross-realm competition at a balanced arena is perfect"). One
    /// challenge fight a week, the same for everyone in every realm: an open field of endless waves in the week's land,
    /// with two twists from the rift list, fought by one of four ready-made heroes so that gear and roster size do not
    /// decide the board. The score is enemies slain times seconds survived; three tries a day; a chest by finish when
    /// the week is over.
    ///
    /// <para><b>Everything about a week comes from its number</b>, through <see cref="DeterministicRandom"/>: the land,
    /// the arena's seed, the twists and the four heroes. Nothing is stored but the scores.</para>
    /// </summary>
    public static class TrialRules
    {
        /// <summary>A trial's run is a pseudo-site, <c>trial:{week}</c>.</summary>
        public const string SitePrefix = "trial:";

        /// <summary>Fought in the open field (Enclosed), as a portal is: the planned waves, never ending.</summary>
        public const LocationType SiteType = LocationType.Portal;

        /// <summary>The waves never run out: the fight ends when the hero falls (or walks away).</summary>
        public const int EndlessWaves = 999;

        /// <summary>The waves are sized as a tier this high (its factor on each wave's roster).</summary>
        public const int Tier = 2;

        /// <summary>Every trial hero stands at this level (spec: 15).</summary>
        public const int HeroLevel = 15;

        /// <summary>A trial hero's rarity: Rare reaches awakening stage II at level 15, as a hired Rare would.</summary>
        public const CharacterRarity HeroRarity = CharacterRarity.Rare;

        /// <summary>The enemies start at this level and climb with the waves, as a portal's do (tune).</summary>
        public const int EnemyLevel = 12;

        /// <summary>Twists from the rift list on the week's fight.</summary>
        public const int MutatorCount = 2;

        /// <summary>Tries a player has each UTC day.</summary>
        public const int AttemptsPerDay = 3;

        /// <summary>The board shows this many rows (the leaderboards' own top).</summary>
        public const int Top = LeaderboardRules.Top;

        /// <summary>The four heroes are one of each class, in this order.</summary>
        public static readonly string[] Classes = { "Warrior", "Archer", "Mage", "Cleric" };

        /// <summary>The weeks are counted from this Monday (UTC), so a week runs Monday 00:00 to Monday 00:00.</summary>
        public static readonly DateTime Epoch = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // -----------------------------------------------------------------
        // The week
        // -----------------------------------------------------------------

        /// <summary>The week <paramref name="at"/> falls in.</summary>
        public static int WeekOf(DateTime at) => (int)Math.Floor((at - Epoch).TotalDays / 7.0);

        /// <summary>When week <paramref name="week"/> begins (a Monday, 00:00 UTC).</summary>
        public static DateTime StartOf(int week) => Epoch.AddDays(week * 7.0);

        /// <summary>When week <paramref name="week"/> ends, and the next begins.</summary>
        public static DateTime EndOf(int week) => StartOf(week + 1);

        /// <summary>The UTC day number, for the tries: a try belongs to the day it was begun on.</summary>
        public static int DayOf(DateTime at) => (int)Math.Floor((at - Epoch).TotalDays);

        public static string SiteId(int week) => SitePrefix + week;

        public static bool TryParseSiteId(string siteId, out int week)
        {
            week = 0;
            return !string.IsNullOrEmpty(siteId) && siteId.StartsWith(SitePrefix, StringComparison.Ordinal)
                   && int.TryParse(siteId.Substring(SitePrefix.Length), out week);
        }

        /// <summary>The week's land.</summary>
        public static BiomeType Biome(int week)
        {
            var all = (BiomeType[])Enum.GetValues(typeof(BiomeType));
            var random = RandomFor(week, "biome");
            return all[random.Next(all.Length)];
        }

        /// <summary>The week's arena seed: the same field for everyone.</summary>
        public static int ArenaSeed(int week) => (int)(RandomFor(week, "arena").NextUInt64() & 0x7FFFFFFF);

        /// <summary>The week's twists, from the rift list.</summary>
        public static List<RiftMutator> Mutators(int week)
        {
            var pool = RiftRules.RolledMutators.ToList();
            var random = RandomFor(week, "mutators");
            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                var swap = pool[i];
                pool[i] = pool[j];
                pool[j] = swap;
            }
            return pool.Take(MutatorCount).ToList();
        }

        /// <summary>One of the week's four heroes: its class, signature and affinity.</summary>
        public sealed class TrialHero
        {
            public int Index;
            public string Class;
            public string SignatureId;
            public AffinityTypes Affinity;
        }

        /// <summary>
        /// The week's four heroes, one per class (<see cref="Classes"/>), each with a signature and affinity drawn for
        /// the week. A class with no signature in the content is left out.
        /// </summary>
        public static List<TrialHero> Heroes(int week, IEnumerable<SignatureDefinition> signatures)
        {
            var all = signatures?.ToList() ?? new List<SignatureDefinition>();
            var heroes = new List<TrialHero>();
            for (int i = 0; i < Classes.Length; i++)
            {
                var mine = SignatureRules.ForClass(all, Classes[i])
                    .OrderBy(s => s.SignatureId, StringComparer.Ordinal).ToList();
                if (mine.Count == 0) continue;
                var random = RandomFor(week, "hero:" + Classes[i]);
                var signature = mine[random.Next(mine.Count)];
                var affinities = SignatureRules.AffinitiesFor(signature).OrderBy(a => (int)a).ToList();
                heroes.Add(new TrialHero
                {
                    Index = i,
                    Class = Classes[i],
                    SignatureId = signature.SignatureId,
                    Affinity = affinities.Count == 0 ? AffinityTypes.Physical : affinities[random.Next(affinities.Count)],
                });
            }
            return heroes;
        }

        // -----------------------------------------------------------------
        // The score
        // -----------------------------------------------------------------

        /// <summary>The score: enemies slain times whole seconds survived.</summary>
        public static long Score(int kills, int seconds) => (long)Math.Max(0, kills) * Math.Max(0, seconds);

        /// <summary>
        /// The most enemies a fight could have dealt by wave <paramref name="waves"/>: the plan's rosters, plus a
        /// split for every one of them (Splitting halves one in five, so this is generous) and an echo per wave.
        /// </summary>
        public static int KillBudget(RunTuning tuning, int waves)
        {
            long total = 0;
            for (int wave = 1; wave <= Math.Max(1, waves); wave++)
                total += WavePlan.EnemiesInWave(tuning ?? new RunTuning(), Tier, wave) + 1;
            return (int)Math.Min(int.MaxValue, total * 2);
        }

        /// <summary>
        /// The most waves that can have begun after <paramref name="seconds"/>: the next wave comes the breather after
        /// the field is clear at the soonest, and a wave cannot be cleared before it has been dealt.
        /// </summary>
        public static int MostWaves(RunTuning tuning, int seconds)
        {
            var t = tuning ?? new RunTuning();
            double step = Math.Max(1.0, Math.Min(t.WaveArenaWaveSeconds, t.WaveArenaDealSeconds + t.WaveArenaBreatherSeconds));
            return 1 + (int)Math.Floor(Math.Max(0, seconds) / step);
        }

        /// <summary>Slack on the client's clock against the server's, in seconds (loading, the last frame, the post).</summary>
        public const int ClockSlackSeconds = 15;

        /// <summary>What the server accepts of a reported try, and whether it had to trim it.</summary>
        public sealed class Checked
        {
            public int Kills;
            public int Waves;
            public int Seconds;
            public long Score;
            public bool Trimmed;
        }

        /// <summary>
        /// Holds a reported try to what could have happened: no more seconds than passed on the server's clock (plus
        /// <see cref="ClockSlackSeconds"/>), no more waves than that time allows, no more kills than those waves held.
        /// Spec: "accept for testers, and log outliers": a trimmed try still scores what is believable.
        /// </summary>
        public static Checked Check(RunTuning tuning, int kills, int waves, int seconds, double serverSeconds)
        {
            int mostSeconds = (int)Math.Floor(Math.Max(0, serverSeconds)) + ClockSlackSeconds;
            int s = Math.Max(0, Math.Min(seconds, mostSeconds));
            int w = Math.Max(1, Math.Min(waves, MostWaves(tuning, s)));
            int k = Math.Max(0, Math.Min(kills, KillBudget(tuning, w)));
            return new Checked
            {
                Kills = k,
                Waves = w,
                Seconds = s,
                Score = Score(k, s),
                Trimmed = s != seconds || w != Math.Max(1, waves) || k != Math.Max(0, kills),
            };
        }

        // -----------------------------------------------------------------
        // The chest
        // -----------------------------------------------------------------

        /// <summary>
        /// The week's chest for finishing <paramref name="rank"/> (1-based) of <paramref name="total"/>: top 1%
        /// Legendary, top 10% Rare, top 50% Magic, anyone who scored Uncommon. The share ahead of a player is what is
        /// measured, so the week's best is always in the top 1%, however few played.
        /// </summary>
        public static ItemRarityTypes ChestFor(int rank, int total)
        {
            if (total <= 0 || rank <= 0) return ItemRarityTypes.Uncommon;
            double ahead = (rank - 1) / (double)total;
            if (ahead < 0.01) return ItemRarityTypes.Legendary;
            if (ahead < 0.10) return ItemRarityTypes.Rare;
            if (ahead < 0.50) return ItemRarityTypes.Magic;
            return ItemRarityTypes.Uncommon;
        }

        private static DeterministicRandom RandomFor(int week, string purpose) =>
            DeterministicRandom.ForSubject(Naming.Hash("trial-week:" + week), Naming.Hash("trial:" + purpose));
    }
}
