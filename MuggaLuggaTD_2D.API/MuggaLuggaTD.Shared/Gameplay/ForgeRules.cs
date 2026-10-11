using System;
using System.Collections.Generic;
using System.Linq;
using Enums;
using Items.Models;
using Items.Utilities;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>What a Forge action costs: materials by name, and gold.</summary>
    public class ForgeCost
    {
        public List<MaterialGrant> Materials { get; set; } = new List<MaterialGrant>();
        public long Gold { get; set; }
    }

    /// <summary>
    /// The Forge (Forge spec, Mike 2026-10-10): improving a piece of gear on the server. Phase 1 is Temper (power
    /// tier +1), Ascend (rarity +1, eating a second piece of the same slot) and Salvage (break a piece into
    /// materials); phase 2 adds Imbue (an affinity star, from a crystal) and Reforge (re-roll the explicit
    /// attributes, the deliberate gamble). Every number here is a first pass (tune).
    ///
    /// <para><b>An upgrade keeps a piece's rolls, nearly.</b> Each explicit value is raised to the new tier and
    /// rarity at its roll quality (its share of the most it could roll), and that quality drifts by up to
    /// <see cref="Drift"/> either way, so a perfect roll can't be locked in (Mike: "avoid the perfect craft").
    /// A slot the upgrade opens is rolled fresh, as a drop would.</para>
    /// </summary>
    public static class ForgeRules
    {
        /// <summary>How far a value's roll quality may drift on an upgrade, as a share of its range's top (±5%).</summary>
        public const double Drift = 0.05;

        /// <summary>The lowest and highest roll quality: a value rolls between half its maximum and all of it.</summary>
        public const double LeastQuality = 0.5;
        public const double MostQuality = 1.0;

        /// <summary>Ascend goes no higher; Celestial and GodLike stay drop-only.</summary>
        public const ItemRarityTypes AscendCap = ItemRarityTypes.Legendary;

        public const string LesserEssence = "Lesser Essence";
        public const string GreaterEssence = "Greater Essence";
        public const string SupremeEssence = "Supreme Essence";
        public const string CommonShard = "Common Shard";
        public const string RareShard = "Rare Shard";
        public const string LegendaryShard = "Legendary Shard";

        /// <summary>Equipment the Forge works on: anything worn, not a material, potion or scroll.</summary>
        public static bool IsForgeable(IItemData item) =>
            item != null && item.ItemType != ItemTypes.Material && item.ItemType != ItemTypes.Potion &&
            item.ItemType != ItemTypes.Scroll && item.ItemType != ItemTypes.General;

        // -----------------------------------------------------------------
        // Temper
        // -----------------------------------------------------------------

        /// <summary>The highest tier a piece can be tempered to: what a drop could reach at the best hero's level.</summary>
        public static int TemperCap(int highestHeroLevel) => Math.Min(10, 1 + Math.Max(1, highestHeroLevel) / 3);

        /// <summary>The hero level that opens tempering to <paramref name="tier"/>.</summary>
        public static int LevelForTier(int tier) => Math.Max(1, (tier - 1) * 3);

        /// <summary>
        /// What tempering <paramref name="item"/> one tier costs: the table (spec §3.2), plus the Bazaar worth the
        /// step adds to the piece. A piece's price grows with the square of its power, so a flat table would let a
        /// player temper to sell; with the added worth on top, forging to sell never pays.
        /// </summary>
        public static ForgeCost TemperCost(IItemData item)
        {
            int to = Math.Min(10, (int)item.PowerTier + 1);
            var cost = TemperTable(to);
            cost.Gold += WorthAdded(item, (ItemPowerTier)to, item.Rarity);
            return cost;
        }

        /// <summary>The table part of tempering to <paramref name="toTier"/> (spec §3.2).</summary>
        public static ForgeCost TemperTable(int toTier)
        {
            int step = toTier <= 4 ? toTier : toTier <= 7 ? toTier - 3 : toTier - 6;
            string essence = toTier <= 4 ? LesserEssence : toTier <= 7 ? GreaterEssence : SupremeEssence;
            long[] gold = { 0, 0, 100, 150, 200, 300, 400, 500, 750, 1000, 1500 };
            return new ForgeCost
            {
                Materials = new List<MaterialGrant> { new MaterialGrant { MaterialName = essence, Quantity = step } },
                Gold = gold[Math.Max(0, Math.Min(10, toTier))],
            };
        }

        /// <summary>Why a piece can't be tempered, or null if it can.</summary>
        public static string CantTemper(IItemData item, int highestHeroLevel)
        {
            if (!IsForgeable(item)) return "Only gear can be forged.";
            int tier = (int)item.PowerTier;
            if (tier >= 10) return "It is already at the highest tier.";
            if (tier >= TemperCap(highestHeroLevel))
                return $"Your heroes can't use tier {tier + 1} yet: reach level {LevelForTier(tier + 1)}.";
            return null;
        }

        /// <summary>Raises the piece one tier.</summary>
        public static void Temper(IItemData item, Random random, List<ItemExplicitTypes> explicitPool = null) =>
            Raise(item, (ItemPowerTier)Math.Min(10, (int)item.PowerTier + 1), item.Rarity, random, explicitPool);

        // -----------------------------------------------------------------
        // Ascend
        // -----------------------------------------------------------------

        /// <summary>What ascending <paramref name="item"/> one rarity costs: the table, plus the Bazaar worth it adds.</summary>
        public static ForgeCost AscendCost(IItemData item)
        {
            var to = (ItemRarityTypes)Math.Min((int)AscendCap, (int)item.Rarity + 1);
            var cost = AscendTable(to);
            cost.Gold += WorthAdded(item, item.PowerTier, to);
            return cost;
        }

        /// <summary>The table part of ascending to <paramref name="to"/>, besides the second piece (spec §3.2).</summary>
        public static ForgeCost AscendTable(ItemRarityTypes to)
        {
            switch (to)
            {
                case ItemRarityTypes.Uncommon: return Cost(CommonShard, 1, 100);
                case ItemRarityTypes.Magic: return Cost(CommonShard, 2, 250);
                case ItemRarityTypes.Rare: return Cost(RareShard, 1, 500);
                default: return Cost(LegendaryShard, 1, 1000);
            }
        }

        /// <summary>Why a piece can't be ascended, or null if it can. <paramref name="second"/> is the piece it eats (null to ask of the piece alone).</summary>
        public static string CantAscend(IItemData item, IItemData second)
        {
            if (!IsForgeable(item)) return "Only gear can be forged.";
            if (item.Rarity >= AscendCap) return item.Rarity == AscendCap
                ? "Legendary is as far as the Forge goes; Celestial and GodLike are found, not made."
                : "It is already beyond what the Forge can make.";
            if (second == null) return null;
            if (second.Id == item.Id) return "Choose a second piece.";
            if (second.ItemType != item.ItemType) return $"The second piece must also be {SlotName(item.ItemType)}.";
            if ((int)second.Rarity < (int)item.Rarity - 1)
                return $"The second piece must be at least {(ItemRarityTypes)Math.Max(0, (int)item.Rarity - 1)}.";
            return null;
        }

        /// <summary>Whether <paramref name="second"/> could be eaten to ascend <paramref name="item"/>.</summary>
        public static bool CanFeed(IItemData item, IItemData second) =>
            item != null && second != null && CantAscend(item, second) == null;

        /// <summary>Raises the piece one rarity.</summary>
        public static void Ascend(IItemData item, Random random, List<ItemExplicitTypes> explicitPool = null) =>
            Raise(item, item.PowerTier, (ItemRarityTypes)Math.Min((int)AscendCap, (int)item.Rarity + 1), random, explicitPool);

        // -----------------------------------------------------------------
        // Imbue (phase 2)
        // -----------------------------------------------------------------

        /// <summary>The most stars a piece carries in all, as drops have.</summary>
        public const int MostStars = 5;

        /// <summary>Stars on the piece, every affinity together.</summary>
        public static int StarsOn(IItemData item) => item?.Influences?.Sum(i => Math.Max(0, i.Stars)) ?? 0;

        /// <summary>Stars of one affinity on the piece.</summary>
        public static int StarsOf(IItemData item, AffinityTypes affinity) =>
            item?.Influences?.Where(i => i.AffinityType == affinity).Sum(i => Math.Max(0, i.Stars)) ?? 0;

        /// <summary>
        /// The crystal grades that can give an affinity its star number <paramref name="newStars"/>, lowest first:
        /// a Minor crystal (Tier1) up to 2 stars, a Major (Tier2) up to 4, a Perfect (Tier3) up to 5 (spec row 6).
        /// The server spends the lowest one the player holds.
        /// </summary>
        public static List<MaterialTier> ImbueGrades(int newStars)
        {
            var grades = new List<MaterialTier>();
            if (newStars <= 2) grades.Add(MaterialTier.Tier1);
            if (newStars <= 4) grades.Add(MaterialTier.Tier2);
            if (newStars <= MostStars) grades.Add(MaterialTier.Tier3);
            return grades;
        }

        /// <summary>"Minor", "Major", "Perfect": a crystal grade in words.</summary>
        public static string GradeName(MaterialTier tier) =>
            tier == MaterialTier.Tier1 ? "Minor" : tier == MaterialTier.Tier2 ? "Major" : "Perfect";

        /// <summary>Why a piece can't take another star, or null if it can.</summary>
        public static string CantImbue(IItemData item)
        {
            if (!IsForgeable(item)) return "Only gear can be forged.";
            if (StarsOn(item) >= MostStars) return "It already carries five stars, as many as gear can hold.";
            return null;
        }

        /// <summary>
        /// What imbuing <paramref name="item"/> with <paramref name="affinity"/> costs, spending
        /// <paramref name="crystal"/>: the crystal, 100 gold for each star the affinity will have, and the Bazaar
        /// worth the star adds (so imbuing to sell never pays).
        /// </summary>
        public static ForgeCost ImbueCost(IItemData item, AffinityTypes affinity, string crystal)
        {
            int stars = StarsOf(item, affinity) + 1;
            var cost = Cost(crystal, 1, 100L * stars);
            cost.Gold += Math.Max(0, BazaarAssay.PriceOf(PreviewImbue(item, affinity)) - BazaarAssay.PriceOf(item));
            return cost;
        }

        /// <summary>Adds a star of <paramref name="affinity"/> (a new influence at one star if it had none) and the
        /// influence bonus attribute that star brings, rolled fresh.</summary>
        public static void Imbue(IItemData item, AffinityTypes affinity)
        {
            AddStar(item, affinity);
            if (item.ExplicitAttributes == null) item.ExplicitAttributes = new List<ItemExplicitAttribute>();
            var bonus = ItemAttributeRoller.RollInfluenceBonus(item.PowerTier, item.Rarity, affinity);
            if (bonus != null) item.ExplicitAttributes.Add(bonus);
        }

        /// <summary>The piece after a star of <paramref name="affinity"/>, its new bonus shown as an empty placeholder.</summary>
        public static StateManagement.Models.ItemSaveData PreviewImbue(IItemData item, AffinityTypes affinity)
        {
            var copy = Preview(item, item.PowerTier, item.Rarity);
            AddStar(copy, affinity);
            copy.ExplicitAttributes.Add(new ItemExplicitAttribute { AffinityType = affinity, Value = 0, IsInfluenceBonus = true });
            return copy;
        }

        private static void AddStar(IItemData item, AffinityTypes affinity)
        {
            if (item.Influences == null) item.Influences = new List<ItemInfluence>();
            var influence = item.Influences.FirstOrDefault(i => i.AffinityType == affinity);
            if (influence != null) influence.Stars++;
            else item.Influences.Add(new ItemInfluence { AffinityType = affinity, Stars = 1 });
        }

        // -----------------------------------------------------------------
        // Reforge (phase 2)
        // -----------------------------------------------------------------

        /// <summary>The essence of a piece's tier band: Lesser for 1–4, Greater for 5–7, Supreme for 8–10.</summary>
        public static string EssenceFor(ItemPowerTier tier)
        {
            int t = Math.Max(1, (int)tier);
            return t <= 4 ? LesserEssence : t <= 7 ? GreaterEssence : SupremeEssence;
        }

        /// <summary>What reforging costs: one essence of the piece's band and 100 gold a tier.</summary>
        public static ForgeCost ReforgeCost(IItemData item) =>
            Cost(EssenceFor(item.PowerTier), 1, 100L * Math.Max(1, (int)item.PowerTier));

        /// <summary>Why a piece can't be reforged, or null if it can.</summary>
        public static string CantReforge(IItemData item)
        {
            if (!IsForgeable(item)) return "Only gear can be forged.";
            if ((item.ExplicitAttributes?.Count(a => !a.IsInfluenceBonus) ?? 0) == 0) return "It has no attributes to re-roll.";
            return null;
        }

        /// <summary>
        /// Re-rolls every ordinary explicit attribute, as a drop of this tier and rarity would roll them: the same
        /// number of them, fresh types and values. The implicit, the influences and their bonuses are kept. It can
        /// come out worse; that is the gamble the player asked for.
        /// </summary>
        public static void Reforge(IItemData item, List<ItemExplicitTypes> explicitPool = null)
        {
            var kept = item.ExplicitAttributes?.Where(a => a.IsInfluenceBonus).ToList() ?? new List<ItemExplicitAttribute>();
            int count = item.ExplicitAttributes?.Count(a => !a.IsInfluenceBonus) ?? 0;
            var fresh = new List<ItemExplicitAttribute>();
            for (int i = 0; i < count; i++)
            {
                var attribute = ItemAttributeRoller.RollAdditionalExplicit(item.PowerTier, item.Rarity, fresh.Select(a => a.Type), explicitPool);
                if (attribute == null) break;
                fresh.Add(attribute);
            }
            item.ExplicitAttributes = fresh.Concat(kept).ToList();
        }

        // -----------------------------------------------------------------
        // What a material is for (the Vault's Forge row)
        // -----------------------------------------------------------------

        /// <summary>A material's job at the Forge, in a line, or null if the Forge doesn't use it.</summary>
        public static string ForgeUse(MaterialCategory category, MaterialTier tier, AffinityTypes? affinity)
        {
            switch (category)
            {
                case MaterialCategory.Essence:
                    return tier == MaterialTier.Tier1 ? "Tempers gear to tiers 2–4; reforges tiers 1–4"
                        : tier == MaterialTier.Tier2 ? "Tempers gear to tiers 5–7; reforges tiers 5–7"
                        : "Tempers gear to tiers 8–10; reforges tiers 8–10";
                case MaterialCategory.RarityShard:
                    return tier == MaterialTier.Tier1 ? "Ascends gear to Uncommon or Magic"
                        : tier == MaterialTier.Tier2 ? "Ascends gear to Rare"
                        : "Ascends gear to Legendary";
                case MaterialCategory.AffinityCrystal:
                    int most = tier == MaterialTier.Tier1 ? 2 : tier == MaterialTier.Tier2 ? 4 : MostStars;
                    return $"Imbues gear with {(affinity.HasValue ? affinity.Value.ToString() : "its affinity")}, up to ★{most}";
                default:
                    return null;
            }
        }

        // -----------------------------------------------------------------
        // Salvage (spec §3.4)
        // -----------------------------------------------------------------

        /// <summary>One line of what a salvage gives: a material, how many, and the chance (1 = always).</summary>
        public class SalvageLine
        {
            public string MaterialName { get; set; }
            public int Quantity { get; set; }
            public double Chance { get; set; } = 1.0;
        }

        /// <summary>
        /// What breaking a piece down can give: essence by its tier, a shard by its rarity, and a Minor crystal
        /// for each of its influences. <paramref name="crystalName"/> names the Minor crystal of an affinity
        /// (from content); an influence it can't name gives nothing.
        /// </summary>
        public static List<SalvageLine> SalvageTable(IItemData item, Func<AffinityTypes, string> crystalName)
        {
            var lines = new List<SalvageLine>();
            if (!IsForgeable(item)) return lines;

            int tier = Math.Max(1, (int)item.PowerTier);
            string essence = tier <= 4 ? LesserEssence : tier <= 7 ? GreaterEssence : SupremeEssence;
            lines.Add(new SalvageLine { MaterialName = essence, Quantity = 1 + (tier - 1) / 3 });

            switch (item.Rarity)
            {
                case ItemRarityTypes.Common: break;
                case ItemRarityTypes.Uncommon: lines.Add(new SalvageLine { MaterialName = CommonShard, Quantity = 1, Chance = 0.25 }); break;
                case ItemRarityTypes.Magic: lines.Add(new SalvageLine { MaterialName = CommonShard, Quantity = 1 }); break;
                case ItemRarityTypes.Rare:
                    lines.Add(new SalvageLine { MaterialName = CommonShard, Quantity = 1 });
                    lines.Add(new SalvageLine { MaterialName = RareShard, Quantity = 1, Chance = 0.25 });
                    break;
                case ItemRarityTypes.Legendary: lines.Add(new SalvageLine { MaterialName = RareShard, Quantity = 1 }); break;
                default: lines.Add(new SalvageLine { MaterialName = LegendaryShard, Quantity = 1 }); break;
            }

            if (item.Influences != null && crystalName != null)
                foreach (var influence in item.Influences)
                {
                    string name = crystalName(influence.AffinityType);
                    if (!string.IsNullOrEmpty(name)) lines.Add(new SalvageLine { MaterialName = name, Quantity = 1 });
                }
            return lines;
        }

        /// <summary>Rolls a salvage table: the sure lines, and each chance line on its chance.</summary>
        public static List<MaterialGrant> RollSalvage(IEnumerable<SalvageLine> table, Random random)
        {
            var got = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in table)
            {
                if (line.Chance < 1.0 && random.NextDouble() >= line.Chance) continue;
                got.TryGetValue(line.MaterialName, out int n);
                got[line.MaterialName] = n + line.Quantity;
            }
            return got.Select(p => new MaterialGrant { MaterialName = p.Key, Quantity = p.Value }).ToList();
        }

        // -----------------------------------------------------------------
        // Raising a piece
        // -----------------------------------------------------------------

        /// <summary>
        /// Moves a piece to a new tier and rarity: every explicit value keeps its roll quality (± the drift),
        /// a slot the new tier or rarity opens is rolled fresh, and the implicit is set for the new tier.
        /// <paramref name="random"/> null means no drift (a preview).
        /// </summary>
        public static void Raise(IItemData item, ItemPowerTier tier, ItemRarityTypes rarity, Random random,
            List<ItemExplicitTypes> explicitPool = null)
        {
            var oldTier = item.PowerTier;
            var oldRarity = item.Rarity;

            foreach (var attribute in item.ExplicitAttributes ?? new List<ItemExplicitAttribute>())
            {
                float oldMax = ItemAttributeRoller.MaxExplicitValue(attribute.Type, oldTier, oldRarity, attribute.IsInfluenceBonus);
                double quality = oldMax > 0 ? attribute.Value / oldMax : LeastQuality;
                attribute.Value = (float)(Drifted(quality, random) *
                    ItemAttributeRoller.MaxExplicitValue(attribute.Type, tier, rarity, attribute.IsInfluenceBonus));
            }

            item.PowerTier = tier;
            item.Rarity = rarity;
            if (item.ExplicitAttributes == null) item.ExplicitAttributes = new List<ItemExplicitAttribute>();

            int wanted = ItemAttributeRoller.GetTotalExplicitCount(tier, rarity);
            int have = item.ExplicitAttributes.Count(a => !a.IsInfluenceBonus);
            for (int i = have; i < wanted && random != null; i++)
            {
                var used = item.ExplicitAttributes.Where(a => !a.IsInfluenceBonus).Select(a => a.Type);
                var fresh = ItemAttributeRoller.RollAdditionalExplicit(tier, rarity, used, explicitPool);
                if (fresh == null) break;
                item.ExplicitAttributes.Add(fresh);
            }

            if (item.ImplicitAttribute != null)
                item.ImplicitAttribute.Value = ItemAttributeRoller.ImplicitValue(item.ImplicitAttribute.Type, tier);
        }

        /// <summary>A roll quality after an upgrade's drift, kept within a roll's range.</summary>
        public static double Drifted(double quality, Random random)
        {
            double shifted = random == null ? quality : quality + (random.NextDouble() * 2 - 1) * Drift;
            return Math.Max(LeastQuality, Math.Min(MostQuality, shifted));
        }

        /// <summary>
        /// How much a piece's Bazaar price rises if it moves to this tier and rarity: worked out on a copy at
        /// the same roll qualities, with the slots the step opens counted.
        /// </summary>
        public static long WorthAdded(IItemData item, ItemPowerTier tier, ItemRarityTypes rarity)
        {
            var after = Preview(item, tier, rarity);
            return Math.Max(0, BazaarAssay.PriceOf(after) - BazaarAssay.PriceOf(item));
        }

        /// <summary>
        /// The piece as it would be after a step, with no drift. A slot the step opens is shown as an empty
        /// placeholder (its roll isn't known yet), so the power it adds is still counted.
        /// </summary>
        public static StateManagement.Models.ItemSaveData Preview(IItemData item, ItemPowerTier tier, ItemRarityTypes rarity)
        {
            var copy = new StateManagement.Models.ItemSaveData
            {
                Id = item.Id, ItemName = item.ItemName, ItemType = item.ItemType, ItemCount = item.ItemCount,
                Rarity = item.Rarity, PowerTier = item.PowerTier,
                Influences = item.Influences?.Select(i => new ItemInfluence { AffinityType = i.AffinityType, Stars = i.Stars }).ToList()
                             ?? new List<ItemInfluence>(),
                ImplicitAttribute = item.ImplicitAttribute == null ? null
                    : new ItemImplicitAttribute { Type = item.ImplicitAttribute.Type, Value = item.ImplicitAttribute.Value },
                ExplicitAttributes = item.ExplicitAttributes?.Select(a => new ItemExplicitAttribute
                    { Type = a.Type, AffinityType = a.AffinityType, Value = a.Value, IsInfluenceBonus = a.IsInfluenceBonus }).ToList()
                    ?? new List<ItemExplicitAttribute>(),
            };
            int opened = SlotsOpened(copy, tier, rarity);
            Raise(copy, tier, rarity, null);
            for (int i = 0; i < opened; i++) copy.ExplicitAttributes.Add(new ItemExplicitAttribute { Value = 0 });
            return copy;
        }

        /// <summary>How many explicit slots tempering or ascending would open.</summary>
        public static int SlotsOpened(IItemData item, ItemPowerTier tier, ItemRarityTypes rarity) =>
            Math.Max(0, ItemAttributeRoller.GetTotalExplicitCount(tier, rarity) -
                        (item.ExplicitAttributes?.Count(a => !a.IsInfluenceBonus) ?? 0));

        // -----------------------------------------------------------------

        private static ForgeCost Cost(string material, int quantity, long gold) => new ForgeCost
        {
            Materials = new List<MaterialGrant> { new MaterialGrant { MaterialName = material, Quantity = quantity } },
            Gold = gold,
        };

        /// <summary>"Gloves", "a Weapon": a slot's name in a sentence.</summary>
        public static string SlotName(ItemTypes type)
        {
            switch (type)
            {
                case ItemTypes.BodyArmor: return "body armour";
                case ItemTypes.LegArmor: return "leg armour";
                case ItemTypes.Gloves: return "gloves";
                case ItemTypes.Boots: return "boots";
                default: return "a " + type.ToString().ToLowerInvariant();
            }
        }
    }
}
