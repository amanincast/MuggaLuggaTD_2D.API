using System;
using System.Collections.Generic;
using System.Linq;
using Enums;
using Items.Models;
using MuggaLuggaTD.Shared.World;
using StateManagement.Models;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>What a quest asks for (<c>docs/design/quests.md</c> §4). Stored as a number: append, never renumber.</summary>
    public enum QuestKind
    {
        /// <summary>Slay a number of one people (<see cref="QuestOffer.Target"/>) in the giver's region.</summary>
        Slay = 0,

        /// <summary>Clear one site (<see cref="QuestOffer.Target"/>), or a number of sites in the region (no target).</summary>
        Clear = 1,

        /// <summary>Fight off ambushes. Given by the Hall, so anywhere.</summary>
        Ambush = 2,

        /// <summary>Bring a number of one material (<see cref="QuestOffer.Target"/>) to the giver.</summary>
        Gather = 3
    }

    /// <summary>
    /// One quest as it is offered, and as it is frozen when taken. Everything the board, the quest log
    /// and the hand-in need, so a taken quest does not change when the realm does. Names are not stored:
    /// the client words a quest from its region, site and people, as it names everything else.
    /// </summary>
    public class QuestOffer
    {
        /// <summary>"{hour}.{set}.{giver}", unique per player: the hour and set it was offered in, and who offers it.</summary>
        public string Id { get; set; }

        /// <summary>
        /// The village's site id, a wandering quest-giver's id ("r12:npc1", <see cref="QuestRules.NpcGiverId"/>), or
        /// <see cref="QuestRules.HallGiver"/>.
        /// </summary>
        public string GiverId { get; set; }

        /// <summary>A wandering giver's calling (<see cref="QuestRules.Callings"/>): what they look like and ask. Null for a village or the Hall.</summary>
        public string Calling { get; set; }

        /// <summary>The region the deed must be done in; null for a Hall quest, which counts anywhere.</summary>
        public string RegionId { get; set; }

        public QuestKind Kind { get; set; }

        /// <summary>The people (Slay), the site id (Clear one site) or the material (Gather). Null when any will do.</summary>
        public string Target { get; set; }

        public int Count { get; set; }

        /// <summary>The level the chest's pieces and the gold are rolled at.</summary>
        public int Level { get; set; }

        /// <summary>The tier of the chest's best piece; the rest are lower.</summary>
        public ItemRarityTypes ChestTier { get; set; }

        public long Gold { get; set; }

        /// <summary>Materials paid beside the chest.</summary>
        public List<MaterialGrant> Materials { get; set; } = new List<MaterialGrant>();
    }

    /// <summary>
    /// Something a player did that a quest may count: a site cleared, enemies slain, an ambush fought
    /// off. Recorded by the server where it happens (a PvE claim, an ambush claim, auto mode).
    /// </summary>
    public class QuestDeed
    {
        public string RegionId { get; set; }

        /// <summary>The site cleared, when the deed is a clear of a fightable site.</summary>
        public string ClearedSiteId { get; set; }

        public bool AmbushWon { get; set; }

        /// <summary>Enemies slain by people, already clamped (<see cref="QuestRules.ClampKills"/>).</summary>
        public Dictionary<string, int> Kills { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Quests (<c>docs/design/quests.md</c>, Mike 2026-10-07): village elders and the Hall's board offer a
    /// few quests at a time, which turn over each hour or at once when all of them are done; finishing one
    /// pays a chest of a weighted tier.
    ///
    /// <para><b>The board is a pure function</b> of the realm, the player, the hour, the set number and the
    /// land the player can see. Nothing is stored for an offer until it is taken, so the server can work a
    /// board out whenever it is asked and a web companion would show the same one. Seeds go through
    /// <see cref="DeterministicRandom"/> and <see cref="Naming.Hash"/>.</para>
    ///
    /// <para><b>Each village is rolled on its own.</b> A village's offer depends only on the hour, the set and
    /// that village, and the villages that give one are those that score highest. Taking a region lights new
    /// villages; it can displace an offer but never rewrite one.</para>
    /// </summary>
    public static class QuestRules
    {
        /// <summary>The Guild Hall's board, as a giver id.</summary>
        public const string HallGiver = "hall";

        /// <summary>How many villages offer a quest at once. <i>(tune)</i></summary>
        public const int VillageOffers = 4;

        /// <summary>How many quests the Hall's board offers at once. <i>(tune)</i></summary>
        public const int HallOffers = 2;

        /// <summary>
        /// How many wandering quest-givers offer a quest at once (Mike, 2026-10-08: NPCs in the regions as well as
        /// villages, on top of them). A giver stands in the region only while it has an offer or a quest of it is
        /// in hand. <i>(tune)</i>
        /// </summary>
        public const int NpcOffers = 3;

        /// <summary>How many wandering givers a region can have at once.</summary>
        public const int NpcSlotsPerRegion = 2;

        public const string Hunter = "Hunter";
        public const string Pilgrim = "Pilgrim";
        public const string Pedlar = "Pedlar";
        public const string Scout = "Scout";

        /// <summary>A wandering giver's calling shapes the ask: a hunter wants a people slain, a pilgrim sites cleared, a pedlar goods, a scout an ambush fought off.</summary>
        public static readonly IReadOnlyList<string> Callings = new[] { Hunter, Pilgrim, Pedlar, Scout };

        /// <summary>A wandering giver's id: its region and slot ("r12:npc1"), so <see cref="SiteSpec.RegionIdOf"/> reads its region.</summary>
        public static string NpcGiverId(string regionId, int slot) => $"{regionId}:npc{slot}";

        /// <summary>Whether a giver id is a wandering giver's.</summary>
        public static bool IsNpcGiver(string giverId) =>
            giverId != null && giverId.IndexOf(":npc", StringComparison.Ordinal) > 0;

        /// <summary>How many quests a player may have taken and not yet handed in. <i>(tune)</i></summary>
        public const int MaxActive = 3;

        /// <summary>How many pieces a quest chest holds: one of its tier, the rest lower. <i>(tune)</i></summary>
        public const int ChestPieces = 3;

        /// <summary>How often the untaken offers turn over.</summary>
        public static readonly TimeSpan Turnover = TimeSpan.FromHours(1);

        /// <summary>
        /// The chest tiers a quest may pay, and their weights by the region's tier (1, 2, 3 and up)
        /// <i>(tune)</i>. Deeper land tilts toward the better chests.
        /// </summary>
        public static readonly ItemRarityTypes[] ChestTiers =
        {
            ItemRarityTypes.Uncommon, ItemRarityTypes.Magic, ItemRarityTypes.Rare, ItemRarityTypes.Legendary
        };

        private static readonly int[][] ChestWeightsByTier =
        {
            new[] { 55, 30, 12, 3 },
            new[] { 45, 32, 18, 5 },
            new[] { 35, 33, 24, 8 }
        };

        /// <summary>A kill tally may run past the plan by this much: a dungeon's lord and its pre-placed guards.</summary>
        public const double KillSlack = 1.5;

        /// <summary>The hour an instant falls in, counted from the epoch: the board's clock.</summary>
        public static long HourOf(DateTime utc) => (long)Math.Floor((utc - DateTime.UnixEpoch).TotalHours);

        /// <summary>When the hour <paramref name="hour"/> ends and the untaken offers turn over.</summary>
        public static DateTime TurnsOverAt(long hour) => DateTime.UnixEpoch.AddHours(hour + 1);

        /// <summary>
        /// The board: up to <see cref="VillageOffers"/> village quests and <see cref="HallOffers"/> Hall quests.
        /// </summary>
        /// <param name="regions">The whole world; only the villages the player can see give quests.</param>
        /// <param name="peoples">The peoples that live in each biome (CharacterData's enemies).</param>
        public static List<QuestOffer> Board(string realmId, string userId, long hour, int set,
            IEnumerable<WorldRegionData> regions, IReadOnlyDictionary<BiomeType, IReadOnlyList<string>> peoples,
            RunTuning tuning)
        {
            var all = (regions ?? Enumerable.Empty<WorldRegionData>()).Where(r => r != null).ToList();
            var lit = RegionSight.Lit(all, userId);
            ulong seed = Naming.Hash($"quests|{realmId}|{userId}|{hour}|{set}");

            var givers = new List<(ulong Score, WorldRegionData Region, RegionLayout Layout, SiteSpec Village)>();
            var wanderers = new List<(ulong Score, WorldRegionData Region, RegionLayout Layout, string Id)>();
            var levels = new List<int>();
            foreach (var region in all.Where(r => lit.Contains(r.RegionId)).OrderBy(r => r.RegionId, StringComparer.Ordinal))
            {
                if (IsRivals(region, userId)) continue;
                var layout = RegionGenerator.Generate(region);
                levels.Add(AutoFightRules.MobLevel(layout.Sites));
                foreach (var site in layout.Sites.Where(s => s.Type == LocationType.NeutralHome))
                {
                    var scorer = DeterministicRandom.ForSubject(seed, Naming.Hash(site.SiteId));
                    givers.Add((scorer.NextUInt64(), region, layout, site));
                }
                for (int slot = 0; slot < NpcSlotsPerRegion; slot++)
                {
                    string npc = NpcGiverId(region.RegionId, slot);
                    var scorer = DeterministicRandom.ForSubject(seed, Naming.Hash(npc));
                    wanderers.Add((scorer.NextUInt64(), region, layout, npc));
                }
            }

            // The highest scores give, one village to a region before any region gives twice.
            var chosen = new List<(ulong Score, WorldRegionData Region, RegionLayout Layout, SiteSpec Village)>();
            var ranked = givers.OrderByDescending(g => g.Score).ToList();
            foreach (var g in ranked)
                if (chosen.Count < VillageOffers && chosen.All(c => c.Region.RegionId != g.Region.RegionId)) chosen.Add(g);
            foreach (var g in ranked)
                if (chosen.Count < VillageOffers && !chosen.Contains(g)) chosen.Add(g);

            var offers = new List<QuestOffer>();
            foreach (var g in chosen)
            {
                var random = DeterministicRandom.ForSubject(seed, Naming.Hash(g.Village.SiteId) + 1);
                offers.Add(VillageOffer(ref random, $"{hour}.{set}.{g.Village.SiteId}", g.Region, g.Layout, g.Village, peoples, tuning));
            }

            // Wanderers the same way: the highest scores, one to a region first.
            var walking = new List<(ulong Score, WorldRegionData Region, RegionLayout Layout, string Id)>();
            var byScore = wanderers.OrderByDescending(w => w.Score).ToList();
            foreach (var w in byScore)
                if (walking.Count < NpcOffers && walking.All(c => c.Region.RegionId != w.Region.RegionId)) walking.Add(w);
            foreach (var w in byScore)
                if (walking.Count < NpcOffers && !walking.Contains(w)) walking.Add(w);
            foreach (var w in walking)
            {
                var random = DeterministicRandom.ForSubject(seed, Naming.Hash(w.Id) + 1);
                offers.Add(NpcOffer(ref random, $"{hour}.{set}.{w.Id}", w.Region, w.Layout, w.Id, peoples, tuning));
            }

            int hallLevel = levels.Count == 0 ? 1 : Math.Max(1, (int)Math.Round(levels.Average()));
            var hall = DeterministicRandom.ForSubject(seed, Naming.Hash(HallGiver));
            var kinds = new List<QuestKind> { QuestKind.Ambush, QuestKind.Clear, QuestKind.Gather };
            hall.Shuffle(kinds);
            for (int i = 0; i < HallOffers && i < kinds.Count; i++)
                offers.Add(HallOffer(ref hall, $"{hour}.{set}.{HallGiver}.{i}", kinds[i], hallLevel, tuning));

            return offers;
        }

        /// <summary>A region another player holds: its villages answer to them, and its sites are a siege's business.</summary>
        private static bool IsRivals(WorldRegionData region, string userId) =>
            region.Ownership == LocationOwnership.Player && !string.IsNullOrEmpty(region.OwnerUserId)
            && !string.Equals(region.OwnerUserId, userId, StringComparison.Ordinal);

        private static QuestOffer VillageOffer(ref DeterministicRandom random, string id, WorldRegionData region,
            RegionLayout layout, SiteSpec village, IReadOnlyDictionary<BiomeType, IReadOnlyList<string>> peoples, RunTuning tuning)
        {
            int level = AutoFightRules.MobLevel(layout.Sites);
            int tier = Math.Max(1, region.Tier);
            var fightable = layout.Sites.Where(s => s.IsFightable).ToList();
            var folk = PeoplesOf(region.Biome, peoples);
            var trades = layout.Sites.Where(s => s.Type == LocationType.ResourceNode)
                .Select(s => ResourceNodeRules.GoodOf(ResourceNodeRules.TradeOf(s.SiteId, region.Biome)))
                .Where(g => !string.IsNullOrEmpty(g)).Distinct().ToList();

            var offer = new QuestOffer { Id = id, GiverId = village.SiteId, RegionId = region.RegionId, Level = level };

            // Slay 35, clear 30, gather 35, less whatever this region cannot offer.
            int[] weights =
            {
                folk.Count > 0 && fightable.Count > 0 ? 35 : 0,
                fightable.Count > 0 ? 30 : 0,
                35
            };
            var kind = new[] { QuestKind.Slay, QuestKind.Clear, QuestKind.Gather }[random.NextWeighted(weights)];
            Ask(ref random, offer, kind, region, tier, folk, fightable, trades, tuning);
            Reward(ref random, offer, tier, tuning);
            return offer;
        }

        /// <summary>
        /// A wandering giver's offer: its calling first (hunter 30, pilgrim 25, pedlar 25, scout 20, less what the
        /// region cannot offer), and the calling decides the ask.
        /// </summary>
        private static QuestOffer NpcOffer(ref DeterministicRandom random, string id, WorldRegionData region,
            RegionLayout layout, string giverId, IReadOnlyDictionary<BiomeType, IReadOnlyList<string>> peoples, RunTuning tuning)
        {
            int level = AutoFightRules.MobLevel(layout.Sites);
            int tier = Math.Max(1, region.Tier);
            var fightable = layout.Sites.Where(s => s.IsFightable).ToList();
            var folk = PeoplesOf(region.Biome, peoples);
            var trades = layout.Sites.Where(s => s.Type == LocationType.ResourceNode)
                .Select(s => ResourceNodeRules.GoodOf(ResourceNodeRules.TradeOf(s.SiteId, region.Biome)))
                .Where(g => !string.IsNullOrEmpty(g)).Distinct().ToList();

            int[] weights =
            {
                folk.Count > 0 && fightable.Count > 0 ? 30 : 0,
                fightable.Count > 0 ? 25 : 0,
                25,
                20
            };
            int calling = random.NextWeighted(weights);
            var offer = new QuestOffer { Id = id, GiverId = giverId, RegionId = region.RegionId, Level = level, Calling = Callings[calling] };
            var kind = new[] { QuestKind.Slay, QuestKind.Clear, QuestKind.Gather, QuestKind.Ambush }[calling];
            Ask(ref random, offer, kind, region, tier, folk, fightable, trades, tuning);
            Reward(ref random, offer, tier, tuning);
            return offer;
        }

        /// <summary>What a regional giver asks, by kind: sized to the region.</summary>
        private static void Ask(ref DeterministicRandom random, QuestOffer offer, QuestKind kind, WorldRegionData region, int tier,
            IReadOnlyList<string> folk, List<SiteSpec> fightable, List<string> trades, RunTuning tuning)
        {
            switch (kind)
            {
                case QuestKind.Slay:
                    offer.Kind = QuestKind.Slay;
                    offer.Target = folk[random.Next(folk.Count)];
                    // About one and a half runs' worth of that people, shared among the region's peoples.
                    int planned = RunRewardCalculator.PlannedEnemies(tier, tuning, LocationType.Dungeon);
                    offer.Count = Math.Max(5, RoundTo5(planned * 1.5 / folk.Count));
                    break;
                case QuestKind.Clear:
                    offer.Kind = QuestKind.Clear;
                    if (fightable.Count == 1 || random.Next(2) == 0)
                    {
                        offer.Target = fightable[random.Next(fightable.Count)].SiteId;
                        offer.Count = 1;
                    }
                    else
                    {
                        offer.Count = 2;
                    }
                    break;
                case QuestKind.Ambush:
                    // A scout's: the roads through this region.
                    offer.Kind = QuestKind.Ambush;
                    offer.Count = 1;
                    break;
                default:
                    offer.Kind = QuestKind.Gather;
                    GatherTarget(ref random, offer, region.Biome, tier, trades);
                    break;
            }
        }

        private static QuestOffer HallOffer(ref DeterministicRandom random, string id, QuestKind kind, int level, RunTuning tuning)
        {
            var offer = new QuestOffer { Id = id, GiverId = HallGiver, Kind = kind, Level = level };
            switch (kind)
            {
                case QuestKind.Ambush:
                    offer.Count = 1;
                    break;
                case QuestKind.Clear:
                    offer.Count = 3;
                    break;
                default:
                    offer.Kind = QuestKind.Gather;
                    offer.Target = Essence;
                    offer.Count = 4 + random.Next(5);
                    break;
            }
            Reward(ref random, offer, 1, tuning);
            return offer;
        }

        public const string Essence = "Lesser Essence";

        /// <summary>The crystal of an affinity, as MaterialData names it: "Minor Fire Crystal".</summary>
        public static string MinorCrystal(AffinityTypes affinity) => $"Minor {affinity} Crystal";

        /// <summary>What a village asks to be brought: a good its own trades produce, its biome's crystal, or essence.</summary>
        private static void GatherTarget(ref DeterministicRandom random, QuestOffer offer, BiomeType biome, int tier, List<string> trades)
        {
            var crystal = TavernRules.FavouredAffinity(biome);
            int[] weights = { trades.Count > 0 ? 50 : 0, crystal.HasValue ? 25 : 0, 25 };
            switch (random.NextWeighted(weights))
            {
                case 0:
                    offer.Target = trades[random.Next(trades.Count)];
                    offer.Count = 10 + 5 * Math.Min(tier, 3) + random.Next(6);
                    break;
                case 1:
                    offer.Target = MinorCrystal(crystal.Value);
                    offer.Count = 2 + random.Next(3);
                    break;
                default:
                    offer.Target = Essence;
                    offer.Count = 4 + 2 * Math.Min(tier, 3) + random.Next(3);
                    break;
            }
        }

        /// <summary>The chest's tier, the gold (one clear's worth at the quest's level) and the materials.</summary>
        private static void Reward(ref DeterministicRandom random, QuestOffer offer, int tier, RunTuning tuning)
        {
            offer.ChestTier = ChestTiers[random.NextWeighted(ChestWeightsFor(tier))];
            offer.Gold = Math.Max(10, RunRewardCalculator.Calculate(offer.Level, 1, tuning, null, null).Gold);
            offer.Materials.Add(new MaterialGrant { MaterialName = Essence, Quantity = 2 + Math.Min(tier, 3) });
            if (offer.ChestTier >= ItemRarityTypes.Rare)
                offer.Materials.Add(new MaterialGrant
                {
                    MaterialName = offer.ChestTier >= ItemRarityTypes.Legendary ? "Rare Shard" : "Common Shard",
                    Quantity = 1
                });
        }

        public static int[] ChestWeightsFor(int tier) => ChestWeightsByTier[Math.Max(1, Math.Min(tier, ChestWeightsByTier.Length)) - 1];

        private static int RoundTo5(double value) => (int)Math.Round(value / 5.0) * 5;

        /// <summary>
        /// The peoples a biome's fights draw from. A biome with nobody of its own borrows Grassland's, as the
        /// combat roster does (<c>BiomeRoster</c>), so a quest never asks for someone who cannot spawn there.
        /// </summary>
        public static IReadOnlyList<string> PeoplesOf(BiomeType biome, IReadOnlyDictionary<BiomeType, IReadOnlyList<string>> peoples)
        {
            if (peoples == null) return Array.Empty<string>();
            if (peoples.TryGetValue(biome, out var own) && own.Count > 0) return own;
            return peoples.TryGetValue(BiomeType.Grassland, out var fallback) ? fallback : Array.Empty<string>();
        }

        /// <summary>
        /// A client's kill tally, held to what the fight could have held: only the peoples of the biome it was
        /// fought in, and no more in all than the run's plan (with <see cref="KillSlack"/>). A cheating client
        /// can at most finish a Slay quest in one run instead of two; it can never invent one.
        /// </summary>
        public static Dictionary<string, int> ClampKills(IDictionary<string, int> reported, IReadOnlyList<string> peoplesOfBiome, int plannedEnemies)
        {
            var clamped = new Dictionary<string, int>(StringComparer.Ordinal);
            if (reported == null || peoplesOfBiome == null) return clamped;

            int budget = (int)Math.Ceiling(Math.Max(0, plannedEnemies) * KillSlack);
            foreach (var people in peoplesOfBiome)
            {
                if (budget <= 0) break;
                if (!reported.TryGetValue(people, out int count) || count <= 0) continue;
                int take = Math.Min(count, budget);
                clamped[people] = take;
                budget -= take;
            }
            return clamped;
        }

        /// <summary>
        /// A fight the server fought itself (auto mode): its kills spread evenly across the biome's peoples, at
        /// <paramref name="share"/> of the plan (auto mode is paid at a third, and counts at a third).
        /// </summary>
        public static Dictionary<string, int> EstimatedKills(IReadOnlyList<string> peoplesOfBiome, int plannedEnemies, double share)
        {
            var kills = new Dictionary<string, int>(StringComparer.Ordinal);
            if (peoplesOfBiome == null || peoplesOfBiome.Count == 0) return kills;
            int each = (int)Math.Floor(plannedEnemies * share / peoplesOfBiome.Count);
            if (each <= 0) return kills;
            foreach (var people in peoplesOfBiome) kills[people] = each;
            return kills;
        }

        /// <summary>
        /// How far a deed takes a quest: its progress after the deed, never past the count. Gather quests are
        /// not advanced by deeds; they are handed in from the wallet.
        /// </summary>
        public static int Advance(QuestOffer quest, int progress, QuestDeed deed)
        {
            if (quest == null || deed == null || progress >= quest.Count) return progress;
            bool here = quest.RegionId == null || string.Equals(quest.RegionId, deed.RegionId, StringComparison.Ordinal);
            if (!here) return progress;

            int gained = 0;
            switch (quest.Kind)
            {
                case QuestKind.Slay:
                    if (deed.Kills != null && quest.Target != null && deed.Kills.TryGetValue(quest.Target, out int slain))
                        gained = Math.Max(0, slain);
                    break;
                case QuestKind.Clear:
                    if (!string.IsNullOrEmpty(deed.ClearedSiteId)
                        && (quest.Target == null || string.Equals(quest.Target, deed.ClearedSiteId, StringComparison.Ordinal)))
                        gained = 1;
                    break;
                case QuestKind.Ambush:
                    if (deed.AmbushWon) gained = 1;
                    break;
            }
            return Math.Min(quest.Count, progress + gained);
        }

        /// <summary>Whether a quest is done: its count reached. A Gather quest is done when it is handed in.</summary>
        public static bool IsDone(QuestOffer quest, int progress) =>
            quest != null && quest.Kind != QuestKind.Gather && progress >= quest.Count;

        /// <summary>
        /// The chest's pieces: one of <paramref name="tier"/>, and <see cref="ChestPieces"/> - 1 more, each one
        /// or more tiers lower and never below Common.
        /// </summary>
        public static List<ItemSaveData> RollChest(IList<ItemTemplate> templates, Random random, int level, ItemRarityTypes tier)
        {
            var pieces = new List<ItemSaveData>();
            var best = FirstStepsRules.RollPiece(templates, random, level, tier);
            if (best == null) return pieces;
            pieces.Add(best);
            for (int i = 1; i < ChestPieces; i++)
            {
                int below = (int)tier - (1 + random.Next(Math.Max(1, (int)tier)));
                var rarity = (ItemRarityTypes)Math.Max((int)ItemRarityTypes.Common, below);
                var piece = FirstStepsRules.RollPiece(templates, random, level, rarity);
                if (piece != null) pieces.Add(piece);
            }
            return pieces;
        }
    }
}
