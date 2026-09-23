using System;
using System.Collections.Generic;
using Enums;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// What the Tavern offers, what it costs, and how a dungeon shapes the board.
    ///
    /// <para>Shared because the client shows a player the cost and the odds before they spend, and
    /// the server charges them. Two implementations would mean a card that says one thing and a
    /// wallet that does another. Design doc 05 §1 and §4.</para>
    /// </summary>
    public static class TavernRules
    {
        /// <summary>
        /// Seats in the Tavern. Six is design 6d's screen, and now also a <b>cap</b> rather than a
        /// batch: a clear fills an empty seat, a hire empties one, and a paid refresh clears the lot.
        ///
        /// <para><b>A clear no longer wipes the board.</b> It used to roll six new faces and throw the
        /// old six away, which meant that farming the materials to afford a recruit was the very thing
        /// that took that recruit away. Saving up was self-defeating. Now a clear <i>adds</i> one to a
        /// free seat and never removes one, so nothing a player is saving for can disappear without
        /// their say-so — and a full board of people they do not want is what the paid refresh is
        /// for.</para>
        /// </summary>
        public const int BoardSize = 6;

        // How many characters a player may hold is no longer one number - it is built from the base,
        // the land they hold and the slots they have bought. See RosterCapRules.

        /// <summary>
        /// Base rarity odds in tenths of a percent, so the tier bonuses below are exact integers
        /// rather than float drift. 60% / 28% / 10% / 2%.
        /// </summary>
        private const int CommonWeight = 600;
        private const int RareWeight = 280;
        private const int EpicWeight = 100;
        private const int LegendaryWeight = 20;

        /// <summary>
        /// The odds a board is rolled on, shaped by the tier of the dungeon whose clearing restocked
        /// it. A deeper dungeon is a better night at the inn - which is what stops the Tavern being
        /// a slot machine you pull for free, since the pull costs a dungeon.
        ///
        /// <para>The bonus is taken out of Common, never out of Rare: the point is to raise the
        /// ceiling, not to hollow out the middle.</para>
        /// </summary>
        public static IReadOnlyList<(CharacterRarity Rarity, int Weight)> RarityOdds(int locationTier)
        {
            int tier = locationTier < 1 ? 1 : locationTier;

            // T1 +0, T2 +2% Epic, T3 +4% Epic and +1% Legendary, T4+ +6% and +2%.
            int epicBonus = tier <= 1 ? 0 : (tier - 1) * 20;
            if (epicBonus > 60) epicBonus = 60;

            int legendaryBonus = tier <= 2 ? 0 : (tier - 2) * 10;
            if (legendaryBonus > 20) legendaryBonus = 20;

            int common = CommonWeight - epicBonus - legendaryBonus;
            if (common < 0) common = 0;

            return new[]
            {
                (CharacterRarity.Common, common),
                (CharacterRarity.Rare, RareWeight),
                (CharacterRarity.Epic, EpicWeight + epicBonus),
                (CharacterRarity.Legendary, LegendaryWeight + legendaryBonus)
            };
        }

        /// <summary>
        /// The affinity a biome favours, or null where it favours none.
        ///
        /// <para>Physical and Arcane are nobody's home ground on purpose: one is the absence of an
        /// element and the other is not of anywhere. A favoured affinity is weighted
        /// <see cref="BiomeFavourWeight"/> against every other one the signature may roll - a nudge,
        /// not a guarantee, because the guarantee is what the crystal lures are for (§4).</para>
        /// </summary>
        public static AffinityTypes? FavouredAffinity(BiomeType biome)
        {
            switch (biome)
            {
                case BiomeType.Volcanic: return AffinityTypes.Fire;
                case BiomeType.Fenland: return AffinityTypes.Water;
                case BiomeType.Highland: return AffinityTypes.Air;
                case BiomeType.Thornwood: return AffinityTypes.Earth;
                case BiomeType.Marsh: return AffinityTypes.Dark;
                case BiomeType.RiverVale: return AffinityTypes.Light;
                default: return null;
            }
        }

        /// <summary>How much more likely the biome's affinity is than any other. Doubling, no more.</summary>
        public const int BiomeFavourWeight = 2;

        /// <summary>
        /// What a recruit of this rarity costs to hire, from design doc 05 §1.
        ///
        /// <para>Paid in materials rather than gold because gold does not exist yet and materials do -
        /// and because it gives the crystals and essences a purpose beyond item merging, which is the
        /// whole reason they were already dropping.</para>
        /// </summary>
        public static IReadOnlyList<MaterialGrant> HireCost(CharacterRarity rarity)
        {
            switch (rarity)
            {
                case CharacterRarity.Legendary:
                    return new[]
                    {
                        new MaterialGrant { MaterialName = "Supreme Essence", Quantity = 3 },
                        new MaterialGrant { MaterialName = "Legendary Shard", Quantity = 1 }
                    };
                case CharacterRarity.Epic:
                    return new[]
                    {
                        new MaterialGrant { MaterialName = "Supreme Essence", Quantity = 2 },
                        new MaterialGrant { MaterialName = "Rare Shard", Quantity = 1 }
                    };
                case CharacterRarity.Rare:
                    return new[]
                    {
                        new MaterialGrant { MaterialName = "Greater Essence", Quantity = 3 }
                    };
                default:
                    return new[]
                    {
                        new MaterialGrant { MaterialName = "Lesser Essence", Quantity = 5 }
                    };
            }
        }

        // -----------------------------------------------------------------
        // Lures and pity (design doc 05 section 4)
        // -----------------------------------------------------------------

        /// <summary>How strongly a crystal pulls the board toward its affinity.</summary>
        public enum LureStrength
        {
            None = 0,
            Minor = 1,
            Major = 2,
            Perfect = 3
        }

        /// <summary>
        /// The share of slots a lure aims to give its affinity.
        ///
        /// <para>A <b>target share</b>, not a multiplier, because that is what the design states and
        /// because a multiplier would mean something different for every signature: the affinity roll
        /// is weighted over the affinities a signature is <i>allowed</i>, which is rarely all eight.
        /// Expressed as a share, "a Perfect crystal makes it 60%" is true of every signature that can
        /// roll that affinity at all.</para>
        /// </summary>
        public static double LureTarget(LureStrength strength)
        {
            switch (strength)
            {
                case LureStrength.Perfect: return 0.60;
                case LureStrength.Major: return 0.40;
                case LureStrength.Minor: return 0.25;
                default: return 0;
            }
        }

        /// <summary>
        /// What each lured restock that showed none of the lured affinity adds to the next one.
        ///
        /// <para><b>Pity is not for rarity.</b> Six slots already give an 11% chance of a Legendary
        /// and 54% of Epic or better, so rarity needs no floor. The chase is the <i>combination</i>,
        /// so this is where the floor goes.</para>
        /// </summary>
        public const double PityStep = 0.10;

        /// <summary>
        /// However long the drought, a lure never quite guarantees the board. Leaving a little room
        /// keeps a lured board a board rather than an order form.
        /// </summary>
        public const double LureCeiling = 0.95;

        /// <summary>The share a lure actually aims for, once a run of misses is counted in.</summary>
        public static double EffectiveLureTarget(LureStrength strength, int missedRestocks)
        {
            double target = LureTarget(strength);
            if (target <= 0) return 0;

            if (missedRestocks > 0)
                target += missedRestocks * PityStep;

            return target > LureCeiling ? LureCeiling : target;
        }

        /// <summary>
        /// The crystal a lure of this strength is paid with, for this affinity. One crystal buys one
        /// restock: the lure is placed before going out and spent by the clear that comes back.
        /// </summary>
        public static IReadOnlyList<MaterialGrant> LureCost(AffinityTypes affinity, LureStrength strength)
        {
            if (strength == LureStrength.None)
                return new MaterialGrant[0];

            return new[]
            {
                new MaterialGrant { MaterialName = CrystalName(affinity, strength), Quantity = 1 }
            };
        }

        /// <summary>The content name of the crystal, e.g. "Perfect Fire Crystal".</summary>
        public static string CrystalName(AffinityTypes affinity, LureStrength strength)
            => strength == LureStrength.None ? null : $"{strength} {affinity} Crystal";

        /// <summary>
        /// Whether clearing this kind of site restocks the board. Only the fightable, spendable ones:
        /// the restock is paid for with a dungeon, so taking a keep must not also buy a board.
        /// </summary>
        public static bool BringsARecruit(LocationType siteType)
            => siteType == LocationType.Dungeon || siteType == LocationType.Portal;

        /// <summary>
        /// What a paid refresh costs, in gold.
        ///
        /// <para><b>The price is the gate, not a daily cap.</b> Design doc 05 §4 proposed one refresh
        /// a day once gold existed, but a cap and a price together gate the same thing twice, and the
        /// cap would need a table to remember. The price alone is enough because the alternative is
        /// strictly better: clearing a dungeon pays gold <i>and</i> brings somebody in for free, so
        /// paying to wipe the board is only ever worth it when the board is full of people you do not
        /// want. That is exactly the situation a refresh is for.</para>
        ///
        /// <para>Priced at roughly two and a half tier-1 clears, so it is a decision rather than a
        /// reflex.</para>
        /// </summary>
        public const long RefreshCostGold = 600;

        /// <summary>
        /// How much dearer each refresh is than the last, until a dungeon resets the count.
        ///
        /// <para><b>A flat price is not a gate for a rich player.</b> At 600 gold a player sitting on
        /// ten thousand can chain sixteen refreshes back to back - ninety-six rolls in about a minute,
        /// which is enough to fish for one exact class, signature and affinity. That quietly makes
        /// lures pointless: there is no reason to spend a crystal shifting the odds when you can buy
        /// more dice instead.</para>
        ///
        /// <para>Doubling gets out of reach fast - 600, 1,200, 2,400, 4,800, 9,600 - so the fifth
        /// refresh in a row costs sixteen times the first. Spending a little is cheap and spamming is
        /// not, which is the whole shape being aimed for.</para>
        /// </summary>
        public const int RefreshCostDoublesEach = 2;

        /// <summary>
        /// Where the doubling stops, so a long run cannot overflow the arithmetic. By this point the
        /// price is prohibitive many times over; the clamp is for safety, not for balance.
        /// </summary>
        public const int MaximumRefreshEscalations = 10;

        /// <summary>
        /// What the next refresh costs, given how many have already been bought since the last
        /// dungeon.
        ///
        /// <para><b>A dungeon resets it</b>, which is the point: the escalation is not a punishment
        /// for refreshing, it is a pull back toward playing. A player who wants another cheap board
        /// can always have one - by going and clearing something, which is what the Tavern has always
        /// been attached to.</para>
        /// </summary>
        public static long RefreshCostFor(int refreshesSinceClear)
        {
            if (refreshesSinceClear <= 0)
                return RefreshCostGold;

            int steps = refreshesSinceClear > MaximumRefreshEscalations
                ? MaximumRefreshEscalations
                : refreshesSinceClear;

            long cost = RefreshCostGold;
            for (int i = 0; i < steps; i++)
                cost *= RefreshCostDoublesEach;

            return cost;
        }
    }
}
