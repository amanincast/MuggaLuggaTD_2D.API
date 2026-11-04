using System;
using Enums;
using Items.Models;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// What the Crossroads Bazaar charges for a thing (design 12d, "the Bazaar Assay").
    ///
    /// <para><b>Nobody sets a price.</b> Every item carries one, worked out from what it is, so the
    /// same item costs the same from every seller in every world. That is what makes a cross-world
    /// market safe to open: there is no price to manipulate, only goods to move. It is shared so the
    /// Sell tab can show the seller exactly what the server will pay them.</para>
    ///
    /// <para><b>Equipment</b> is priced from its power (<see cref="PartyPowerCalculator.CalculateItemPower"/>),
    /// the same weight garrisons and raids already count it at, so an item worth more on the field is
    /// worth more in the market. The price grows with the square of power: a Legendary is not three
    /// Commons, it is the thing a player saves for. <b>Materials</b> are priced from their tier and what
    /// they are for (Mike, 2026-10-01).</para>
    ///
    /// <para>The design also lets supply move the price ("how much of it is listed"). That is left
    /// out: a price that moves is a price a player can push, and Mike asked for one we calculate.</para>
    /// </summary>
    public static class BazaarAssay
    {
        /// <summary>Power squared over this is an item's price before rounding.</summary>
        public const double PowerSquaredPerGold = 50.0;

        /// <summary>Nothing sells for less.</summary>
        public const long MinimumPrice = 10;

        /// <summary>The Bazaar's tenth: what the house keeps of every sale.</summary>
        public const double Fee = 0.10;

        /// <summary>A material's price by tier (1, 2, 3), before its category.</summary>
        private static readonly long[] MaterialTierPrice = { 20, 80, 300 };

        /// <summary>
        /// What each kind of material is worth against an essence of its tier. Crystals steer the
        /// Tavern (a lure), shards lift a rarity and are the rarest drop, so each is worth more.
        /// </summary>
        public static double CategoryFactor(MaterialCategory category)
        {
            switch (category)
            {
                case MaterialCategory.AffinityCrystal: return 1.5;
                case MaterialCategory.RarityShard: return 3.0;
                default: return 1.0;
            }
        }

        /// <summary>The price of one piece of equipment.</summary>
        public static long PriceOf(IItemData item)
        {
            if (item == null) return MinimumPrice;
            double power = PartyPowerCalculator.CalculateItemPower(item);
            return Tidy(power * power / PowerSquaredPerGold);
        }

        /// <summary>

        /// One unit of a worker's goods (tune). Goods move in bulk, hundreds at a time, so they sit under

        /// the floor every other tag starts at: at 10 a unit a fortification would cost as much as a Rare sword.

        /// </summary>

        public const long GoodsUnitPrice = 2;


        /// <summary>The price of one unit of a material.</summary>
        public static long PriceOf(MaterialCategory category, MaterialTier tier)
        {
            if (category == MaterialCategory.Goods) return GoodsUnitPrice;
            int index = Math.Max(1, Math.Min(MaterialTierPrice.Length, (int)tier)) - 1;
            return Tidy(MaterialTierPrice[index] * CategoryFactor(category));
        }

        /// <summary>The price of one unit of a material, or the minimum for one content does not know.</summary>
        public static long PriceOf(MaterialTemplate material)
            => material == null ? MinimumPrice : PriceOf(material.Category, material.Tier);

        // -----------------------------------------------------------------
        // The market's terms (Marketplace spec, Mike 2026-10-10 / 2026-11-04)
        // -----------------------------------------------------------------

        /// <summary>A listing's deposit, as a share of what it would sell for (row 8). Back on a sale, kept on a pull-back or an expiry.</summary>
        public const double DepositRate = 0.02;

        /// <summary>How long a listing or a buy order stands before it goes back (row 5).</summary>
        public const int ListingHours = 72;

        /// <summary>Active listings one player may have from one realm (M2: high, for forge-feed churn).</summary>
        public const int MaxListings = 100;

        /// <summary>Open buy orders one player may have in one realm (M2).</summary>
        public const int MaxOrders = 20;

        /// <summary>The most one buy order may ask for.</summary>
        public const int MaxOrderQuantity = 999;

        /// <summary>How far back the demand line counts ("38 sold this week", M1).</summary>
        public const int DemandDays = 7;

        /// <summary>The deposit on a listing worth <paramref name="gross"/>: a fiftieth, rounded up, never less than 1.</summary>
        public static long DepositOn(long gross) => gross <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(gross * DepositRate - 1e-9));

        /// <summary>
        /// The part of a <paramref name="deposit"/> a sale gives back when a listing of
        /// <paramref name="quantity"/> goes from <paramref name="soldBefore"/> to <paramref name="soldAfter"/> sold.
        /// Counted on the running total, so every refund of a listing that sells out adds up to the whole deposit.
        /// </summary>
        public static long DepositBack(long deposit, int quantity, int soldBefore, int soldAfter)
        {
            if (deposit <= 0 || quantity <= 0) return 0;
            int before = Math.Max(0, Math.Min(quantity, soldBefore));
            int after = Math.Max(before, Math.Min(quantity, soldAfter));
            return deposit * after / quantity - deposit * before / quantity;
        }

        /// <summary>The house's cut of a sale worth <paramref name="gross"/>.</summary>
        public static long FeeOn(long gross) => gross <= 0 ? 0 : (long)Math.Round(gross * Fee, MidpointRounding.AwayFromZero);

        /// <summary>What a seller receives for a sale worth <paramref name="gross"/>.</summary>
        public static long NetOf(long gross) => gross <= 0 ? 0 : gross - FeeOn(gross);

        /// <summary>
        /// Rounds a raw figure to a price a person would write on a tag: 5s under 100, 10s under
        /// 1,000, 50s under 10,000, 100s above. Rounding is up to the floor, never below it.
        /// </summary>
        public static long Tidy(double raw)
        {
            if (double.IsNaN(raw) || raw <= MinimumPrice) return MinimumPrice;
            long step = raw < 100 ? 5 : raw < 1_000 ? 10 : raw < 10_000 ? 50 : 100;
            long rounded = (long)Math.Round(raw / step, MidpointRounding.AwayFromZero) * step;
            return Math.Max(MinimumPrice, rounded);
        }
    }
}
