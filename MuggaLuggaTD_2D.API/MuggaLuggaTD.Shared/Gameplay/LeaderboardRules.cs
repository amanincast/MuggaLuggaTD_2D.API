using System;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>The boards (Leaderboards spec, as revised 2026-10-10).</summary>
    public enum LeaderboardBoard
    {
        /// <summary>A season's average points per hour: NOW, THIS MONTH and ALL-TIME.</summary>
        Rate,
        /// <summary>One hero's power, by season-length bracket, rebuilt hourly.</summary>
        Heroes,
        /// <summary>A company's power, by bracket, hourly.</summary>
        Companies,
        /// <summary>Enemies slain: the last 7 days (any realm) and all-time, hourly.</summary>
        Slayers,
        /// <summary>Regions held right now, by bracket, hourly.</summary>
        Conquerors,
    }

    /// <summary>How long a realm's seasons run, so a 7-day realm's heroes are not ranked against a 90-day one's.</summary>
    public enum SeasonBracket
    {
        /// <summary>7 days or fewer.</summary>
        Short,
        /// <summary>8 to 30 days.</summary>
        Standard,
        /// <summary>31 days or more.</summary>
        Long,
    }

    /// <summary>The Slayers board's two windows. Seasons don't line up across realms, so "this season" is a rolling week.</summary>
    public enum SlayerWindow
    {
        Week,
        AllTime,
    }

    /// <summary>
    /// The rules every board shares (Leaderboards spec §3): who qualifies for the rate boards, which bracket
    /// a realm is in, and how a month is named. Read-only maths, shared so the client greys a row with the
    /// same rule the server ranked it by.
    /// </summary>
    public static class LeaderboardRules
    {
        /// <summary>A season shorter than this is not ranked.</summary>
        public const int MinimumSeasonDays = 7;

        /// <summary>A player must have been in the season this long (tune).</summary>
        public const double MinimumHoursInSeason = 48;

        /// <summary>The NOW board shows a season once it is this old (tune).</summary>
        public const double NowAfterDays = 3;

        /// <summary>How many rows a board shows; the player's own row is pinned below if it is not among them.</summary>
        public const int Top = 50;

        /// <summary>A living board is rebuilt when it is older than this.</summary>
        public static readonly TimeSpan LivingMaxAge = TimeSpan.FromHours(1);

        /// <summary>The Slayers board's rolling window.</summary>
        public const int SlayerWindowDays = 7;

        /// <summary>Whether a finished season counts on the rate boards (spec row 4).</summary>
        public static bool Qualifies(int seasonLengthDays, double hoursInSeason, int regionsHeld) =>
            WhyNot(seasonLengthDays, hoursInSeason, regionsHeld) == null;

        /// <summary>Why a finished season does not count, in the player's words, or null if it does.</summary>
        public static string WhyNot(int seasonLengthDays, double hoursInSeason, int regionsHeld)
        {
            if (seasonLengthDays < MinimumSeasonDays) return $"Seasons under {MinimumSeasonDays} days are not ranked.";
            if (hoursInSeason < MinimumHoursInSeason) return $"Ranked seasons need {MinimumHoursInSeason:0} hours played in them.";
            if (regionsHeld < 1) return "Ranked seasons need a region held at the end.";
            return null;
        }

        /// <summary>
        /// How long the player was in the season: from when they joined it (their first score), or its start if
        /// they were there before it.
        /// </summary>
        public static double HoursIn(DateTime? joinedAt, DateTime seasonStartedAt, DateTime seasonEndedAt)
        {
            var from = joinedAt.HasValue && joinedAt.Value > seasonStartedAt ? joinedAt.Value : seasonStartedAt;
            return Math.Max(0, (seasonEndedAt - from).TotalHours);
        }

        /// <summary>The bracket of a realm whose seasons last <paramref name="seasonLengthDays"/>.</summary>
        public static SeasonBracket BracketOf(int seasonLengthDays) =>
            seasonLengthDays <= 7 ? SeasonBracket.Short
            : seasonLengthDays <= 30 ? SeasonBracket.Standard
            : SeasonBracket.Long;

        /// <summary>The bracket's name, as the board's switch shows it.</summary>
        public static string BracketName(SeasonBracket bracket) => bracket switch
        {
            SeasonBracket.Short => "SHORT (7 DAYS OR FEWER)",
            SeasonBracket.Long => "LONG (31+ DAYS)",
            _ => "STANDARD (8–30 DAYS)",
        };

        /// <summary>The month a season counts in: the UTC calendar month it ended in, as yyyymm.</summary>
        public static int MonthOf(DateTime endedAtUtc) => endedAtUtc.Year * 100 + endedAtUtc.Month;

        /// <summary>The month before <paramref name="yyyymm"/>.</summary>
        public static int MonthBefore(int yyyymm) =>
            yyyymm % 100 == 1 ? (yyyymm / 100 - 1) * 100 + 12 : yyyymm - 1;

        /// <summary>A month as words: "October 2026".</summary>
        public static string MonthName(int yyyymm)
        {
            int month = Math.Max(1, Math.Min(12, yyyymm % 100));
            return new DateTime(yyyymm / 100, month, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The NOW board's rate: points so far over the hours since the player joined the season (or since it
        /// began). Null until the season is <see cref="NowAfterDays"/> old.
        /// </summary>
        public static double? NowRate(double pointsSoFar, DateTime seasonStartedAt, DateTime? joinedAt, DateTime now)
        {
            if ((now - seasonStartedAt).TotalDays < NowAfterDays) return null;
            double hours = HoursIn(joinedAt, seasonStartedAt, now);
            if (hours <= 0) return null;
            return Math.Max(0, pointsSoFar) / hours;
        }
    }
}
