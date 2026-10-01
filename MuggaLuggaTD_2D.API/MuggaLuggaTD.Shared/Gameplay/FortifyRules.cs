using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>Why a region cannot be fortified right now.</summary>
    public enum FortifyRefusal
    {
        None = 0,
        NotYours,
        AtMaximum,
        AlreadyUnderWay,
        UnderSiege
    }

    /// <summary>
    /// Fortifying a region (Hiring Hall phase 3; plan <c>docs/design/hiring-hall.md</c> in the Unity
    /// repo): goods the workers gathered raise its entrenchment by one, after the works take their
    /// time. Shared so the dossier quotes the bill and the wait the server will charge.
    ///
    /// <para>Entrenchment was already in the game - it multiplies hold, points and the gold rate, and a
    /// siege that takes a region knocks it to 0 - but nothing could raise it. This is what does.</para>
    /// </summary>
    public static class FortifyRules
    {
        public const int MaxLevel = 5;

        /// <summary>The works for one level take this long (Mike, 2026-10-01: "fortifying takes time").</summary>
        public static readonly TimeSpan Duration = TimeSpan.FromHours(2);

        /// <summary>Stone and timber for level I at tier 1 (tune).</summary>
        public const int BaseStoneAndTimber = 150;

        /// <summary>Ore joins the bill from this level up: the upper walls want iron.</summary>
        public const int OreFromLevel = 4;

        /// <summary>Ore per level past <see cref="OreFromLevel"/> - 1, at tier 1 (tune).</summary>
        public const int BaseOre = 100;

        /// <summary>A richer region needs bigger walls: ×1, ×1.5, ×2, ×2.5 for tiers 1 to 4.</summary>
        public static double TierFactor(int tier) => 1.0 + 0.5 * (Math.Max(1, tier) - 1);

        /// <summary>The goods it takes to raise a region of <paramref name="tier"/> to <paramref name="toLevel"/>.</summary>
        public static IReadOnlyList<(string Good, int Quantity)> CostFor(int toLevel, int tier)
        {
            var bill = new List<(string, int)>();
            if (toLevel < 1 || toLevel > MaxLevel) return bill;

            double factor = TierFactor(tier);
            int walls = (int)Math.Round(BaseStoneAndTimber * toLevel * factor);
            bill.Add((ResourceNodeRules.GoodOf(ResourceTrade.Quarrier), walls));
            bill.Add((ResourceNodeRules.GoodOf(ResourceTrade.Forester), walls));
            if (toLevel >= OreFromLevel)
                bill.Add((ResourceNodeRules.GoodOf(ResourceTrade.Miner),
                    (int)Math.Round(BaseOre * (toLevel - OreFromLevel + 1) * factor)));
            return bill;
        }

        /// <summary>Whether works are under way on the region, as the world says at <paramref name="now"/>.</summary>
        public static bool IsUnderWay(WorldRegionData region)
            => region != null && region.FortifyingTo > 0;

        /// <summary>When the works finish, or null when none are under way.</summary>
        public static DateTime? EndsAt(WorldRegionData region)
            => IsUnderWay(region) ? new DateTime(region.FortifyEndsAtUtcTicks, DateTimeKind.Utc) : (DateTime?)null;

        /// <summary>
        /// May <paramref name="userId"/> start raising this region now? <paramref name="besieged"/> is
        /// whether a siege is mustering or assaulting it, which only the server's table knows. A siege
        /// declared blocks fortifying, or a defender could pay to dodge it mid-muster.
        /// </summary>
        public static FortifyRefusal Check(WorldRegionData region, string userId, bool besieged)
        {
            if (region == null || !region.IsOwnedByPlayer(userId)) return FortifyRefusal.NotYours;
            if (IsUnderWay(region)) return FortifyRefusal.AlreadyUnderWay;
            if (region.Entrenchment >= MaxLevel) return FortifyRefusal.AtMaximum;
            if (besieged) return FortifyRefusal.UnderSiege;
            return FortifyRefusal.None;
        }

        public static string Explain(FortifyRefusal refusal)
        {
            switch (refusal)
            {
                case FortifyRefusal.NotYours: return "Only land you hold can be fortified.";
                case FortifyRefusal.AtMaximum: return "Its walls are as strong as walls get.";
                case FortifyRefusal.AlreadyUnderWay: return "Works are already under way there.";
                case FortifyRefusal.UnderSiege: return "Nobody builds with an army at the gate. Fortify once the siege ends.";
                default: return "";
            }
        }
    }
}
