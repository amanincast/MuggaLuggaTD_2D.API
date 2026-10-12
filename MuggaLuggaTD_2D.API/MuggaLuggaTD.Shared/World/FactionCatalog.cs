using System;
using System.Collections.Generic;
using System.Linq;

namespace MuggaLuggaTD.Shared.World
{
    /// <summary>One NPC faction as every screen and rule knows it (Factions 2 spec §4).</summary>
    public sealed class FactionInfo
    {
        public FactionId Id { get; }

        /// <summary>The bare name: "Grimjaw". Warbands and garrisons are named from it ("Grimjaw Raiders").</summary>
        public string Name { get; }

        /// <summary>The name with its article: "The Grimjaw". What holds a region, in the blob and the war log.</summary>
        public string Title { get; }

        /// <summary>One line for the dossier and the Powers panel.</summary>
        public string Lore { get; }

        /// <summary>The biomes it is seated in at generation.</summary>
        public IReadOnlyList<BiomeType> HomeBiomes { get; }

        /// <summary>Its colour on the map, as linear 0–1 red, green and blue.</summary>
        public float Red { get; }
        public float Green { get; }
        public float Blue { get; }

        /// <summary>Its banner's cloth shape (<c>HolderFlag</c>).</summary>
        public int Flag { get; }

        /// <summary>Whether a world's generation may seat it. Factions that arrive by an event (the Riftborn) are not.</summary>
        public bool Seatable { get; }

        /// <summary>Whether it stands in the season's standings. The Riftborn do not (Invasions R8).</summary>
        public bool Scored { get; }

        /// <summary>
        /// The least tier of wild land it expands into (<see cref="Gameplay.FactionGrowthRules"/>): hard country for the
        /// seated factions, any land at all for the Riftborn, who spread like a plague (R8).
        /// </summary>
        public int ExpandMinimumTier { get; }

        public FactionInfo(FactionId id, string name, string title, string lore, IReadOnlyList<BiomeType> homeBiomes,
            float red, float green, float blue, int flag, bool seatable, bool scored = true, int expandMinimumTier = 3)
        {
            Scored = scored;
            ExpandMinimumTier = expandMinimumTier;
            Id = id;
            Name = name;
            Title = title;
            Lore = lore;
            HomeBiomes = homeBiomes;
            Red = red;
            Green = green;
            Blue = blue;
            Flag = flag;
            Seatable = seatable;
        }

        /// <summary>"the Grimjaw", mid-sentence.</summary>
        public string TitleInSentence => Title.StartsWith("The ", StringComparison.Ordinal) ? "the " + Title.Substring(4) : Title;
    }

    /// <summary>
    /// The NPC factions there are (Factions 2 spec, step 1). Everything that once named the Grimjaw and the Ashkin
    /// asks here instead, so a new faction is a row, not a hunt through the code.
    ///
    /// <para><b>The cast</b> is the factions a world was generated with, kept in its blob (<c>"Factions"</c>). A world
    /// written before the cast existed has none recorded and reads as <see cref="DefaultCast"/>. A faction that
    /// arrives by an event rather than at generation (the Riftborn) is in play once it holds land
    /// (<see cref="InPlay"/>).</para>
    /// </summary>
    public static class FactionCatalog
    {
        public static readonly FactionInfo Grimjaw = new FactionInfo(FactionId.Grimjaw, "Grimjaw", "The Grimjaw",
            "Raiders who stopped asking whose land it was.",
            new[] { BiomeType.Grassland, BiomeType.Forest }, 0.35f, 0.29f, 0.48f, 3, seatable: true);

        public static readonly FactionInfo Ashkin = new FactionInfo(FactionId.Ashkin, "Ashkin", "The Ashkin",
            "They dug into the mountain and the mountain let them stay.",
            new[] { BiomeType.Volcanic, BiomeType.Highland }, 0.62f, 0.26f, 0.24f, 3, seatable: true);

        /// <summary>
        /// Strangers from another realm (Invasions phase 6). A Great Rift's eruption seats them, so they are in play only
        /// once they hold land. Violet, flying the tear (flag 4); not scored; they spread into wild land of any tier.
        /// </summary>
        public static readonly FactionInfo Riftborn = new FactionInfo(FactionId.Riftborn, "Riftborn", "The Riftborn",
            "They came through the tear, and more keep coming.",
            new BiomeType[0], 0.66f, 0.36f, 0.96f, 4, seatable: false, scored: false, expandMinimumTier: 1);

        /// <summary>Every faction, in the order the Powers panel lists them.</summary>
        public static readonly IReadOnlyList<FactionInfo> All = new[] { Grimjaw, Ashkin, Riftborn };

        /// <summary>The cast of a world that recorded none: the two factions every world had before the cast existed.</summary>
        public static readonly IReadOnlyList<FactionId> DefaultCast = new[] { FactionId.Grimjaw, FactionId.Ashkin };

        /// <summary>Whether <paramref name="faction"/> is an NPC faction (not None, not a player).</summary>
        public static bool Has(FactionId faction) => Of(faction) != null;

        /// <summary>The faction's row, or null for None, Player or an unknown id.</summary>
        public static FactionInfo Of(FactionId faction)
        {
            foreach (var info in All)
                if (info.Id == faction) return info;
            return null;
        }

        /// <summary>The faction's title ("The Grimjaw"), or its enum name for an unknown one.</summary>
        public static string TitleOf(FactionId faction) => Of(faction)?.Title ?? faction.ToString();

        /// <summary>The faction's bare name ("Grimjaw"), or its enum name for an unknown one.</summary>
        public static string NameOf(FactionId faction) => Of(faction)?.Name ?? faction.ToString();

        /// <summary>Whether the faction stands in the season's standings (<see cref="FactionInfo.Scored"/>).</summary>
        public static bool IsScored(FactionId faction) => Of(faction)?.Scored ?? false;

        /// <summary>A recorded cast, cleaned (known factions, once each); <see cref="DefaultCast"/> when none was recorded.</summary>
        public static IReadOnlyList<FactionId> CastOrDefault(IEnumerable<FactionId> recorded)
        {
            var cast = recorded?.Where(Has).Distinct().ToList();
            return cast == null || cast.Count == 0 ? DefaultCast : cast;
        }

        /// <summary>
        /// The factions in play in a world: its cast, then any other faction that holds land in it (one that arrived by an
        /// event), in catalogue order.
        /// </summary>
        public static IReadOnlyList<FactionId> InPlay(IEnumerable<FactionId> cast, IEnumerable<WorldRegionData> regions)
        {
            var list = CastOrDefault(cast).ToList();
            if (regions == null) return list;
            var holding = new HashSet<FactionId>(regions
                .Where(r => r != null && string.IsNullOrEmpty(r.OwnerUserId) && Has(r.Faction))
                .Select(r => r.Faction));
            foreach (var info in All)
                if (holding.Contains(info.Id) && !list.Contains(info.Id)) list.Add(info.Id);
            return list;
        }

        /// <summary>
        /// Which of the cast a region of this biome goes to at generation: the first whose home biomes hold it, else the
        /// cast's first (as the Grimjaw took every biome that was not the Ashkin's).
        /// </summary>
        public static FactionId SeatFor(BiomeType biome, IReadOnlyList<FactionId> cast)
        {
            cast = CastOrDefault(cast);
            foreach (var id in cast)
            {
                var info = Of(id);
                if (info != null && info.HomeBiomes.Contains(biome)) return id;
            }
            return cast[0];
        }
    }
}
