using System;
using System.Collections.Generic;
using System.Linq;
using Enums;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// A character sheet the Tavern may put a recruit on: what it looks like, and what it can be.
    ///
    /// <para>Today these are the ally templates in CharacterData. Design doc 05 §6 wants them to
    /// <i>become</i> sheets tagged by class and race rather than finished characters; this is the
    /// shape that reads them either way, so the content change costs nothing here.</para>
    /// </summary>
    public class RecruitSheet
    {
        /// <summary>The template's LinkName. Decides the art, and nothing else.</summary>
        public string Sheet { get; set; }

        public string Class { get; set; }

        /// <summary>The signature this sheet ships with, for the starting roster. Not used by a roll.</summary>
        public string SignatureId { get; set; }

        /// <summary>The affinity it ships with, likewise.</summary>
        public AffinityTypes? SignatureAffinity { get; set; }
    }

    /// <summary>One recruit as the Tavern rolled it: everything that makes it the character it is.</summary>
    public class RecruitRoll
    {
        public string Name { get; set; }
        public string Sheet { get; set; }
        public string Class { get; set; }
        public string SignatureId { get; set; }
        public AffinityTypes Affinity { get; set; }
        public CharacterRarity Rarity { get; set; }
    }

    /// <summary>
    /// Rolls the Tavern's board.
    ///
    /// <para><b>The server rolls, always.</b> A character is the most valuable thing in the game, so
    /// a client that could roll its own board could roll until it liked one. This lives in the shared
    /// assembly so the client can show the same odds it will be judged by, not so it can roll.</para>
    ///
    /// <para>A roll is class, then sheet, then signature, then affinity, then rarity - in that order,
    /// because each one narrows what the next may be. <b>Rarity is rolled last and independently</b>:
    /// it changes the ceiling, not the character, so a Legendary is a lucky copy of a roll a Common
    /// could equally have been. That is what makes ascension (§2) a road rather than a consolation.</para>
    /// </summary>
    public static class RecruitRoller
    {
        /// <summary>
        /// Recruit names. One flat table rather than doc 05's per-race ones, because races are not
        /// modelled yet - the sheet is the only thing that says what a character looks like.
        /// </summary>
        private static readonly string[] Names =
        {
            "Aldric", "Brenna", "Corvin", "Dessa", "Eamon", "Fenna", "Garrick", "Hesper",
            "Ivo", "Jorunn", "Kestrel", "Lisbet", "Maerwen", "Nolan", "Orla", "Perrin",
            "Quill", "Rook", "Silvi", "Torvald", "Ulla", "Varek", "Wynn", "Yorath",
            "Anselm", "Bryn", "Cael", "Delwyn", "Edda", "Finlay", "Grimm", "Halla",
            "Isen", "Juno", "Kiran", "Lark", "Merrow", "Nessa", "Osric", "Prue"
        };

        /// <summary>
        /// A whole board. Slots are rolled independently, so duplicates are possible and are not a
        /// bug: six slots is a small enough sample that forcing them apart would misrepresent the
        /// odds the player is told.
        /// </summary>
        public static List<RecruitRoll> RollBoard(
            IReadOnlyList<RecruitSheet> sheets,
            IReadOnlyList<SignatureDefinition> signatures,
            int locationTier,
            BiomeType? biome,
            Random random,
            int count = TavernRules.BoardSize)
        {
            var board = new List<RecruitRoll>();
            if (random == null)
                return board;

            for (int i = 0; i < count; i++)
            {
                var roll = Roll(sheets, signatures, locationTier, biome, random);
                if (roll != null)
                    board.Add(roll);
            }

            return board;
        }

        /// <summary>
        /// One recruit, or null when content cannot make one - no sheet of a class that has a
        /// signature. A null is a content problem, not a run-time one, and is better than a recruit
        /// with no ability.
        /// </summary>
        public static RecruitRoll Roll(
            IReadOnlyList<RecruitSheet> sheets,
            IReadOnlyList<SignatureDefinition> signatures,
            int locationTier,
            BiomeType? biome,
            Random random)
        {
            if (sheets == null || signatures == null || random == null)
                return null;

            // A class is only rollable when it has both a face and something to do with it.
            var classes = sheets
                .Where(s => s != null && !string.IsNullOrEmpty(s.Class) && !string.IsNullOrEmpty(s.Sheet))
                .Select(s => s.Class)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(c => SignatureRules.ForClass(signatures, c).Count > 0)
                .ToList();

            if (classes.Count == 0)
                return null;

            string className = classes[random.Next(classes.Count)];

            var classSheets = sheets
                .Where(s => s != null && string.Equals(s.Class, className, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var classSignatures = SignatureRules.ForClass(signatures, className);

            var signature = classSignatures[random.Next(classSignatures.Count)];

            return new RecruitRoll
            {
                Name = Names[random.Next(Names.Length)],
                Sheet = classSheets[random.Next(classSheets.Count)].Sheet,
                Class = className,
                SignatureId = signature.SignatureId,
                Affinity = RollAffinity(signature, biome, random),
                Rarity = RollRarity(locationTier, random)
            };
        }

        /// <summary>
        /// An affinity the signature may have, with the biome's own weighted double. The weighting is
        /// over the <i>allowed</i> affinities, so favouring Fire on a signature that cannot be Fire
        /// simply does nothing rather than skewing the rest.
        /// </summary>
        public static AffinityTypes RollAffinity(SignatureDefinition signature, BiomeType? biome, Random random)
        {
            var allowed = SignatureRules.AffinitiesFor(signature);
            if (allowed.Count == 0)
                return AffinityTypes.Physical;

            var favoured = biome.HasValue ? TavernRules.FavouredAffinity(biome.Value) : null;

            int total = 0;
            var weights = new int[allowed.Count];
            for (int i = 0; i < allowed.Count; i++)
            {
                weights[i] = favoured.HasValue && allowed[i] == favoured.Value ? TavernRules.BiomeFavourWeight : 1;
                total += weights[i];
            }

            int roll = random.Next(total);
            for (int i = 0; i < allowed.Count; i++)
            {
                roll -= weights[i];
                if (roll < 0)
                    return allowed[i];
            }

            return allowed[allowed.Count - 1];
        }

        /// <summary>The rarity, on the odds the dungeon's tier bought.</summary>
        public static CharacterRarity RollRarity(int locationTier, Random random)
        {
            var odds = TavernRules.RarityOdds(locationTier);

            int total = odds.Sum(o => o.Weight);
            if (total <= 0)
                return CharacterRarity.Common;

            int roll = random.Next(total);
            foreach (var option in odds)
            {
                roll -= option.Weight;
                if (roll < 0)
                    return option.Rarity;
            }

            return CharacterRarity.Common;
        }
    }
}
