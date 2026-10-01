using System;
using System.Collections.Generic;
using System.Linq;
using Enums;
using Items.Models;
using StateManagement.Models;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// First Steps (design 12c): six things a player does on a world, and the Rare chest for doing
    /// all of them. Held per player per world, so a veteran starting a new world earns the same chest
    /// a newcomer does (Mike, 2026-10-01).
    ///
    /// The chest grants an item, so the steps that matter are recorded by the server where they
    /// happen, never taken from the client's word. Only the two that cannot be abused are reported:
    /// opening the Hall and putting on a piece of gear.
    /// </summary>
    public static class FirstStepsRules
    {
        public const string Company = "company";
        public const string Equip = "equip";
        public const string Hire = "hire";
        public const string March = "march";
        public const string Clear = "clear";
        public const string Garrison = "garrison";

        /// <summary>In the ledger's order.</summary>
        public static readonly IReadOnlyList<string> Steps = new[] { Company, Equip, Hire, March, Clear, Garrison };

        /// <summary>What a client may tick itself. Everything else the server records.</summary>
        public static readonly IReadOnlyList<string> ClientReported = new[] { Company, Equip };

        /// <summary>The chest's piece is Rare, rolled as if dropped at this level: a world's start.</summary>
        public const ItemRarityTypes ChestRarity = ItemRarityTypes.Rare;
        public const int ChestLevel = 5;

        public static bool IsStep(string key) => key != null && Steps.Contains(key);

        public static bool MayClientReport(string key) => key != null && ClientReported.Contains(key);

        public static bool AllDone(IEnumerable<string> done)
        {
            var set = new HashSet<string>(done ?? Enumerable.Empty<string>());
            return Steps.All(set.Contains);
        }

        /// <summary>The chest's item: one piece of equipment, rolled like a drop but always Rare.</summary>
        public static ItemSaveData RollChest(IList<ItemTemplate> templates, Random random)
        {
            if (templates == null || templates.Count == 0) return null;
            var template = templates[random.Next(templates.Count)];
            var item = new ItemSaveData
            {
                Id = Guid.NewGuid().ToString(),
                ItemName = template.ItemName,
                ItemType = template.ItemType,
                ItemCount = 1
            };
            Items.Utilities.ItemDropCalculator.ApplyDropProperties(
                item, ChestLevel, template.ImplicitPool, template.ExplicitPool, ChestRarity);
            return item;
        }
    }
}
