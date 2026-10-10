using System;
using System.Collections.Generic;
using System.Linq;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>What a letter is about (the Inbox spec, §3). Stored by name.</summary>
    public enum LetterKind
    {
        RaidOnYou,
        RaidRepelled,
        SiegeDeclaredOnYou,
        SiegeResultOnYou,
        YourSiegeResult,
        HeroesCaptured,
        HeroesReturned,
        PrisonersRansomed,
        CompanyArrived,
        CompanyAmbushed,
        AutoReport,
        QuestReady,
        SeasonEnded,
        RealmGoalReached,
    }

    /// <summary>The Letters book's filter chips.</summary>
    public enum LetterCategory { War, Companies, Quests, Realm }

    /// <summary>
    /// The inbox (the Inbox spec, Mike 2026-10-08): personal letters about what happened to a player in
    /// one realm, each with an action. Not the war log, which is the realm's news. Here so the server, the
    /// client and a future web companion keep one set of numbers.
    /// </summary>
    public static class LetterRules
    {
        /// <summary>Raids on one region within this window fold into one letter ("raided twice").</summary>
        public static readonly TimeSpan GroupWindow = TimeSpan.FromHours(1);

        /// <summary>A letter older than this is dropped.</summary>
        public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

        /// <summary>Read letters older than this go first when the cap is reached.</summary>
        public static readonly TimeSpan ReadGoesFirstAfter = TimeSpan.FromDays(7);

        /// <summary>The most letters one player keeps in one realm.</summary>
        public const int Cap = 200;

        /// <summary>
        /// Kinds that can need the player (a ⚑): the flag is live only while its condition holds (the
        /// siege is still live, the heroes are still held), which the server works out on read.
        /// </summary>
        public static bool CanFlag(LetterKind kind) =>
            kind == LetterKind.SiegeDeclaredOnYou || kind == LetterKind.HeroesCaptured ||
            kind == LetterKind.CompanyAmbushed || kind == LetterKind.QuestReady || kind == LetterKind.SeasonEnded;

        public static LetterCategory CategoryOf(LetterKind kind)
        {
            switch (kind)
            {
                case LetterKind.CompanyArrived:
                case LetterKind.CompanyAmbushed:
                case LetterKind.AutoReport:
                    return LetterCategory.Companies;
                case LetterKind.QuestReady:
                    return LetterCategory.Quests;
                case LetterKind.SeasonEnded:
                case LetterKind.RealmGoalReached:
                    return LetterCategory.Realm;
                default:
                    return LetterCategory.War;
            }
        }

        /// <summary>Whether a raid at <paramref name="at"/> folds into a letter first written at <paramref name="first"/>.</summary>
        public static bool FoldsInto(DateTime first, DateTime at) => at >= first && at - first < GroupWindow;

        /// <summary>
        /// Which letters to delete so a player keeps at most <see cref="Cap"/> and none past
        /// <see cref="KeepFor"/>: first anything too old, then read letters past
        /// <see cref="ReadGoesFirstAfter"/>, then the oldest read, then the oldest unread.
        /// </summary>
        public static List<T> ToDrop<T>(IEnumerable<T> letters, Func<T, DateTime> occurredAt, Func<T, bool> read, DateTime now, int cap = Cap)
        {
            var all = letters.ToList();
            var drop = all.Where(l => now - occurredAt(l) > KeepFor).ToList();
            var kept = all.Except(drop).ToList();
            int over = kept.Count - cap;
            if (over <= 0) return drop;

            var order = kept
                .OrderBy(l => read(l) && now - occurredAt(l) > ReadGoesFirstAfter ? 0 : read(l) ? 1 : 2)
                .ThenBy(occurredAt)
                .Take(over);
            drop.AddRange(order);
            return drop;
        }
    }
}
