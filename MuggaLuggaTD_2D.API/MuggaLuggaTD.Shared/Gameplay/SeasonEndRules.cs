using System;
using System.Collections.Generic;
using Enums;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// The end of a season (<c>docs/design/season-end.md</c>): the chest a finish earns, and the factions'
    /// place on the scoreboard.
    ///
    /// <para><b>The chest follows the average rate, not the total.</b> A season lasts 1 to 180 days, so a
    /// total would make a long season richer by its length alone. A player who held well for a weekend
    /// earns the same chest as one who held as well for a month.</para>
    ///
    /// <para><b>Factions score their land only</b>, at the players' rates (Mike, 2026-10-08). They earn
    /// nothing for raids or sieges: their land is their score. In a solo realm they are what you race.</para>
    /// </summary>
    public static class SeasonEndRules
    {
        /// <summary>A faction's id on the scoreboard, as in the war log: "faction:Grimjaw".</summary>
        public const string FactionPrefix = "faction:";

        /// <summary>The level a season chest's piece is rolled at. <i>(tune)</i></summary>
        public const int ChestLevel = 10;

        /// <summary>
        /// Average points per hour from which each chest is earned, lowest first <i>(tune)</i>. For scale: a
        /// seat earns about 11 an hour, a tier-3 region about 22, a heartland region 40 or more; a player
        /// holding their seat and two rings around it (about 12 regions) earns 140 to 360, and a faction 85
        /// to 420 (FactionBalanceSimulation, 2026-10-08). So: a few regions, Magic; a growing realm, Rare;
        /// a large one held all season, Legendary.
        /// </summary>
        public static readonly IReadOnlyList<(double FromRate, ItemRarityTypes Rarity)> ChestBands = new[]
        {
            (0.0, ItemRarityTypes.Uncommon),
            (25.0, ItemRarityTypes.Magic),
            (75.0, ItemRarityTypes.Rare),
            (175.0, ItemRarityTypes.Legendary)
        };

        /// <summary>The average points an hour a season's total works out at.</summary>
        public static double AverageRate(double totalPoints, DateTime seasonStartedAt, DateTime seasonEndedAt)
        {
            double hours = (seasonEndedAt - seasonStartedAt).TotalHours;
            if (hours <= 0 || totalPoints <= 0) return 0;
            return totalPoints / hours;
        }

        /// <summary>The chest a finish earns, or null for a player who scored nothing.</summary>
        public static ItemRarityTypes? ChestFor(double totalPoints, DateTime seasonStartedAt, DateTime seasonEndedAt)
        {
            if (totalPoints <= 0) return null;

            double rate = AverageRate(totalPoints, seasonStartedAt, seasonEndedAt);
            var chest = ChestBands[0].Rarity;
            foreach (var band in ChestBands)
            {
                if (rate >= band.FromRate) chest = band.Rarity;
            }
            return chest;
        }

        /// <summary>What a faction's land earns it an hour: the players' rates over the regions it holds.</summary>
        public static double RateForFaction(FactionId faction, IEnumerable<WorldRegionData> regions)
        {
            if (faction == FactionId.None || regions == null) return 0;

            double total = 0;
            foreach (var region in regions)
            {
                if (FactionStrengthRules.Holds(faction, region))
                    total += SeasonScoreRules.RateFor(region);
            }
            return total;
        }

        /// <summary>A faction's id on the scoreboard.</summary>
        public static string FactionScoreId(FactionId faction) => FactionPrefix + faction;

        /// <summary>The faction a scoreboard id names, or <see cref="FactionId.None"/> for a player.</summary>
        public static FactionId FactionOf(string scoreId)
        {
            if (string.IsNullOrEmpty(scoreId) || !scoreId.StartsWith(FactionPrefix, StringComparison.Ordinal))
                return FactionId.None;
            return Enum.TryParse<FactionId>(scoreId.Substring(FactionPrefix.Length), out var faction)
                ? faction
                : FactionId.None;
        }
    }
}
