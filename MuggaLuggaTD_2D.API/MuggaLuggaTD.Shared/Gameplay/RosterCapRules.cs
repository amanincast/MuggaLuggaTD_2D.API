using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// How many characters a player may hold, and where that number comes from.
    ///
    /// <para><b>Land grants the capacity to garrison it.</b> That is the whole idea. A garrisoned
    /// character is locked out of party selection, so holding more ground is already the thing that
    /// makes a player need more characters — the cap rising with territory is not a reward bolted onto
    /// conquest, it is the same number on both sides of the ledger. A player who has taken half a
    /// continent has somewhere to put an army; a player sitting on their capital does not.</para>
    ///
    /// <para><b>The cap gates hiring, never holding.</b> Territory can be lost, so the cap can fall
    /// below what a player already has. It never takes a character away — they simply cannot hire
    /// again until they are under it. Same principle as season points, which stop accruing when ground
    /// is lost but are never clawed back: losing a war should put a player behind, not unmake what
    /// they built.</para>
    ///
    /// <para><b>Gold buys a few permanent slots on top.</b> Two reasons. Land is the only other
    /// source and land can be taken, so without this a bad season caps a player's ambitions along with
    /// their borders — bought slots are the floor nobody can push them below. And gold badly needs a
    /// second sink: until now it bought nothing but Tavern refreshes, which are deliberately priced to
    /// discourage buying them.</para>
    ///
    /// <para>Shared because the Guild Hall shows a player the cap and the price, and the server
    /// enforces both. Design doc 05 §4.</para>
    /// </summary>
    public static class RosterCapRules
    {
        /// <summary>
        /// Characters a player may hold before owning anything or buying anything.
        ///
        /// <para>Deliberately tight. The starting roster is the seven ally templates, so this is a
        /// bench of three — enough to field a party of four and rotate, not enough to collect. A cap
        /// that is generous at the start is a cap nobody ever thinks about, and then a miss at the
        /// Tavern costs nothing.</para>
        /// </summary>
        public const int BaseSlots = 10;

        /// <summary>Slots one held region is worth. Roughly the garrison one region asks for.</summary>
        public const int SlotsPerRegion = 1;

        /// <summary>
        /// Where territory stops paying out, so a runaway leader's roster does not run away with them.
        /// A world is ~78 regions and a dominant player can hold far more than fourteen; past this
        /// point more ground is its own reward.
        /// </summary>
        public const int MaximumFromTerritory = 14;

        /// <summary>
        /// Slots that can be bought, ever, in one realm. <b>The hard count is the real bound</b> — the
        /// escalating price only shapes the order they are bought in. A price alone is no bound at all
        /// for a player with land, since land prints gold by the hour.
        /// </summary>
        public const int MaximumPurchasedSlots = 6;

        /// <summary>
        /// The most any player can reach by any route. The three sources sum to exactly this, which is
        /// the point: there is no fourth way in, and no arithmetic that gets past it.
        /// </summary>
        public const int AbsoluteCap = BaseSlots + MaximumFromTerritory + MaximumPurchasedSlots;

        /// <summary>What the first bought slot costs.</summary>
        public const long FirstSlotCostGold = 2_500;

        /// <summary>
        /// How much dearer each bought slot is than the one before — 2,500 / 5,000 / 7,500 up to
        /// 15,000, 52,500 gold for all six.
        ///
        /// <para>Linear rather than doubling, unlike <see cref="TavernRules.RefreshCostFor"/>, because
        /// these are six permanent purchases rather than an endlessly repeatable one. A refresh needs
        /// a price that escalates out of reach because nothing else stops it being bought again;
        /// a slot is stopped by <see cref="MaximumPurchasedSlots"/>, so its price only has to make the
        /// sixth feel like a commitment rather than an afterthought.</para>
        /// </summary>
        public const long SlotCostStepGold = 2_500;

        /// <summary>Slots earned by holding <paramref name="regionsHeld"/> regions.</summary>
        public static int FromTerritory(int regionsHeld)
        {
            if (regionsHeld <= 0) return 0;

            int earned = regionsHeld * SlotsPerRegion;
            return earned > MaximumFromTerritory ? MaximumFromTerritory : earned;
        }

        /// <summary>
        /// This player's cap right now: the base, what their land is worth, and what they have bought.
        ///
        /// <para>A live function of current holdings, never a stored number. A cap written down is a
        /// cap that can disagree with the map, and the map is the thing the player can see.</para>
        /// </summary>
        public static int CapFor(int regionsHeld, int purchasedSlots)
        {
            int bought = purchasedSlots < 0 ? 0 : purchasedSlots;
            if (bought > MaximumPurchasedSlots) bought = MaximumPurchasedSlots;

            int cap = BaseSlots + FromTerritory(regionsHeld) + bought;
            return cap > AbsoluteCap ? AbsoluteCap : cap;
        }

        /// <summary>Whether another slot can still be bought.</summary>
        public static bool CanBuyAnother(int purchasedSlots)
            => purchasedSlots < MaximumPurchasedSlots;

        /// <summary>
        /// What the next bought slot costs, or 0 when there are none left to buy. Zero rather than an
        /// exception so a caller that forgets to ask <see cref="CanBuyAnother"/> shows no price rather
        /// than a wrong one.
        /// </summary>
        public static long SlotCostFor(int purchasedSlots)
        {
            if (!CanBuyAnother(purchasedSlots)) return 0;

            int bought = purchasedSlots < 0 ? 0 : purchasedSlots;
            return FirstSlotCostGold + (bought * SlotCostStepGold);
        }

        /// <summary>How many regions a player holds, which is what their cap is built from.</summary>
        public static int RegionsHeldBy(string userId, IEnumerable<WorldRegionData> allRegions)
        {
            if (string.IsNullOrEmpty(userId) || allRegions == null) return 0;

            int held = 0;
            foreach (var region in allRegions)
            {
                if (region != null && region.IsOwnedByPlayer(userId)) held++;
            }

            return held;
        }
    }
}
