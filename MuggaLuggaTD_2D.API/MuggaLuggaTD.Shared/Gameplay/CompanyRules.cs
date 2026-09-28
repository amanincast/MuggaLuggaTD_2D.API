using System;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// What a player's companies may be: how many they may form, how many a company holds, and what a
    /// company is doing. <c>docs/design/parties-and-travel.md</c> §2 (Unity repo).
    ///
    /// <para><b>A company is four.</b> The fight is built for a leader and three allies, and a company
    /// is the party that fights it.</para>
    ///
    /// <para><b>A player may form their roster cap divided by four</b> (Mike, 2026-09-27): two at the
    /// start, seven at the ceiling. The cap already grows with land and with bought slots
    /// (<see cref="RosterCapRules"/>), so the number of companies grows with it and there is no second
    /// dial to tune. Like the cap, <b>a falling limit takes nothing away</b>: a player over it keeps
    /// every company and cannot form another until under it.</para>
    ///
    /// <para>Shared because the Guild Hall shows the limit and the server enforces it.</para>
    /// </summary>
    public static class CompanyRules
    {
        /// <summary>How many characters one company holds.</summary>
        public const int MaxSize = 4;

        /// <summary>The longest name a company may carry.</summary>
        public const int MaxNameLength = 32;

        /// <summary>How many companies a roster cap of <paramref name="rosterCap"/> allows. Never fewer than one.</summary>
        public static int MaxCompanies(int rosterCap) => Math.Max(1, rosterCap / MaxSize);

        /// <summary>Whether a player holding <paramref name="companies"/> may form another.</summary>
        public static bool CanForm(int companies, int rosterCap) => companies < MaxCompanies(rosterCap);

        /// <summary>The name a company is given until its player names it.</summary>
        public static string DefaultName(int ordinal) => ordinal <= 1 ? "The Vanguard" : $"Company {ordinal}";

        /// <summary>
        /// A company's banner, until its player picks one: the palette in order, so the first few
        /// companies are always told apart. Hex, as the client's style sheets take it.
        /// </summary>
        public static readonly string[] Banners =
        {
            "#b8452c",   // rust
            "#2f6fa3",   // river blue
            "#3f7a3a",   // forest
            "#8a4fa3",   // heather
            "#c08a2a",   // ochre
            "#2f8a86",   // teal
            "#6b5636",   // umber
        };

        public static string DefaultBanner(int ordinal) => Banners[Math.Max(0, ordinal - 1) % Banners.Length];

        /// <summary>Whether <paramref name="colour"/> is a banner colour the client can draw: "#rrggbb".</summary>
        public static bool IsBanner(string? colour)
        {
            if (colour == null || colour.Length != 7 || colour[0] != '#') return false;
            for (int i = 1; i < 7; i++)
                if (!Uri.IsHexDigit(colour[i])) return false;
            return true;
        }
    }

    /// <summary>
    /// What a company is doing. Numbered for storage; append, never renumber. The Guild Hall's
    /// status badges are these: IN DUNGEON, MARCHING, RETURNING, GARRISONED.
    /// </summary>
    public enum CompanyState
    {
        /// <summary>Standing at a site, free to act.</summary>
        Idle = 0,
        /// <summary>On the road to a site.</summary>
        Travelling = 1,
        /// <summary>On the road back to where it set out: it fled an ambush, or was recalled.</summary>
        Returning = 2,
        /// <summary>Stopped on the road by an ambush, waiting for its player to fight or flee.</summary>
        Ambushed = 3,
        /// <summary>In a fight.</summary>
        InRun = 4,
        /// <summary>Assigned as a site's garrison; its members are stationed there.</summary>
        Stationed = 5,
    }
}
