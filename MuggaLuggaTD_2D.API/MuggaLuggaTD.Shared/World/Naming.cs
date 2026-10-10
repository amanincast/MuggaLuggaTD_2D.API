using System;
using System.Collections.Generic;
using System.Linq;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD.Shared.World
{
    /// <summary>
    /// Names for the world and the people in it, rolled from seeds: regions, sites, bosses, warbands
    /// and heroes, in the design handoff's voice ("Valebrook Keep", "Ashen Deep", "Tidecaller Vosk",
    /// "Grimjaw Warband", "Pip Quickfletch").
    ///
    /// <para><b>Nothing is stored.</b> A name is a pure function of a seed that already exists — a
    /// region's seed, a site's id, the arena's seed, a character's id — so every player sees the
    /// same name for the same thing without a byte of it in the world blob, exactly as sites
    /// themselves are never stored. It is shared so the server can name things too (a refusal that
    /// says who, a recruit's roll).</para>
    ///
    /// <para><b>The word lists are the data.</b> Changing one renames things, which is harmless (a
    /// name is never a key) but visible: players will notice their capital was renamed. Add to the
    /// end of a list rather than reordering it when a rename is not wanted - and even that renames
    /// whatever rolled past the old end, so treat it as a content change.</para>
    ///
    /// <para>Every roll goes through <see cref="DeterministicRandom"/> and <see cref="Hash"/>, never
    /// <c>System.Random</c> or <c>string.GetHashCode</c>, for the reason the region generator gives:
    /// the client and the server must produce the same name.</para>
    /// </summary>
    public static class Naming
    {
        #region Seeds

        /// <summary>FNV-1a over the string's UTF-16 units: stable across runtimes, unlike <c>GetHashCode</c>.</summary>
        public static ulong Hash(string text)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                if (text != null)
                    foreach (char c in text)
                    {
                        hash ^= c;
                        hash *= 1099511628211UL;
                    }
                return hash;
            }
        }

        private static string Pick(string[] list, ref DeterministicRandom random) => list[random.Next(list.Length)];

        private static string Cap(string word) =>
            string.IsNullOrEmpty(word) ? word : char.ToUpperInvariant(word[0]) + word.Substring(1);

        /// <summary>Words a compound happens to make that mean something else entirely.</summary>
        private static readonly HashSet<string> Unfortunate = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Hollywood", "Redwood", "Bottom", "Dryfall",
        };

        /// <summary>Root + ending as one word, refusing joins that read badly ("Ashhold").</summary>
        private static string Compound(string root, string ending)
        {
            if (char.ToLowerInvariant(root[root.Length - 1]) == char.ToLowerInvariant(ending[0])) return null;
            if (string.Equals(root, ending, StringComparison.OrdinalIgnoreCase)) return null;
            var word = Cap(root) + ending.ToLowerInvariant();
            return Unfortunate.Contains(word) ? null : word;
        }

        /// <summary>"Adjective Noun", refusing "The Hollow Hollow".</summary>
        private static string Phrase(string[] adjectives, string[] nouns, ref DeterministicRandom random)
        {
            for (int i = 0; i < 8; i++)
            {
                string adjective = Pick(adjectives, ref random), noun = Pick(nouns, ref random);
                if (!string.Equals(adjective, noun, StringComparison.OrdinalIgnoreCase)) return $"{adjective} {noun}";
            }
            return $"{adjectives[0]} {nouns[nouns.Length - 1]}";
        }

        #endregion

        #region Land

        /// <summary>First halves of a place name, by what the land is like.</summary>
        private static string[] Roots(BiomeType biome)
        {
            switch (biome)
            {
                case BiomeType.Forest: return new[] { "Thorn", "Elder", "Moss", "Bramble", "Holly", "Yew", "Fern", "Briar", "Hollow", "Wild", "Rowan", "Hazel", "Owl", "Deer" };
                case BiomeType.Lakeland: return new[] { "Fen", "Mere", "Reed", "Heron", "Still", "Mist", "Willow", "Pike", "Weir", "Cress", "Rush", "Otter", "Swan", "Eel" };
                case BiomeType.Highland: return new[] { "Stone", "Pale", "Crag", "Frost", "High", "Grey", "Eagle", "Snow", "Iron", "Ridge", "White", "Cairn", "Wind", "Rime" };
                case BiomeType.Volcanic: return new[] { "Ember", "Ash", "Cinder", "Char", "Smoke", "Brand", "Flint", "Slag", "Scorch", "Soot", "Pyre", "Kiln", "Red", "Blaze" };
                case BiomeType.Swamp: return new[] { "Bog", "Murk", "Mire", "Sedge", "Gloam", "Toad", "Black", "Sour", "Silt", "Leech", "Rot", "Newt", "Dun", "Wither" };
                case BiomeType.Desert: return new[] { "Sun", "Sand", "Dune", "Salt", "Dust", "Bone", "Gold", "Scar", "Glass", "Dry", "Amber", "Sear", "Jackal", "Mirage" };
                default: return new[] { "Vale", "Mill", "Green", "Barley", "Oak", "Wheat", "Meadow", "Honey", "Fair", "Hay", "Clover", "Sheaf", "Bell", "Linden" };
            }
        }

        /// <summary>Second halves of a region's name.</summary>
        private static string[] RegionEndings(BiomeType biome)
        {
            switch (biome)
            {
                case BiomeType.Forest: return new[] { "wood", "glen", "shaw", "march", "hollow", "dale", "hurst", "watch" };
                case BiomeType.Lakeland: return new[] { "mere", "water", "fen", "holm", "wick", "brook", "ford", "marsh" };
                case BiomeType.Highland: return new[] { "fell", "watch", "tor", "rise", "crest", "reach", "gard", "march" };
                case BiomeType.Volcanic: return new[] { "hold", "fall", "forge", "mouth", "scar", "watch", "fell", "reach" };
                case BiomeType.Swamp: return new[] { "moor", "marsh", "hollow", "wallow", "fen", "mere", "reach", "bottom" };
                case BiomeType.Desert: return new[] { "reach", "well", "spire", "mark", "fall", "crest", "hold", "wastes" };
                default: return new[] { "brook", "field", "dale", "stead", "ford", "march", "vale", "mead" };
            }
        }

        /// <summary>One place word for this land ("Valebrook", "Thornmere"). Never null.</summary>
        private static string PlaceWord(BiomeType biome, ref DeterministicRandom random)
        {
            var roots = Roots(biome);
            var endings = RegionEndings(biome);
            for (int i = 0; i < 16; i++)
            {
                var word = Compound(Pick(roots, ref random), Pick(endings, ref random));
                if (word != null) return word;
            }
            return Cap(roots[0]) + endings[1];
        }

        /// <summary>
        /// A region's own name, before the world has made it unique. <paramref name="attempt"/> is
        /// how many times a clash has sent it back to roll again.
        /// </summary>
        public static string RegionName(WorldRegionData region, int attempt = 0)
        {
            if (region == null) return "Unknown";
            var random = DeterministicRandom.ForSubject((ulong)(uint)region.Seed, 0x7265676E + (ulong)attempt);
            return PlaceWord(region.Biome, ref random);
        }

        /// <summary>
        /// Every region's name, unique across the world. Regions are named in region-id order and a
        /// clash rolls the later one again, so the answer depends only on the world — two players
        /// holding the same world see the same names.
        /// </summary>
        public static Dictionary<string, string> RegionNames(IEnumerable<WorldRegionData> regions)
        {
            var names = new Dictionary<string, string>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (regions == null) return names;

            foreach (var region in regions.Where(r => r != null && !string.IsNullOrEmpty(r.RegionId))
                         .OrderBy(r => r.RegionId, StringComparer.Ordinal))
            {
                string name = null;
                for (int attempt = 0; attempt < 32; attempt++)
                {
                    name = RegionName(region, attempt);
                    if (taken.Add(name)) break;
                    name = null;
                }
                names[region.RegionId] = name ?? $"{RegionName(region)} {region.Hex.Q},{region.Hex.R}";
            }
            return names;
        }

        #endregion

        #region Sites

        private static readonly string[] KeepWords = { "Keep", "Hold", "Castle" };
        private static readonly string[] VillageEndings = { "ford", "wick", "by", "ton", "stead", "well", "bury", "cott", "ham" };
        private static readonly string[] DeepNouns = { "Deep", "Hollow", "Barrow", "Pit", "Warren", "Grotto", "Maw", "Den", "Delve", "Chasm" };
        private static readonly string[] RiftNouns = { "Rift", "Gate", "Tear", "Breach", "Veil", "Door" };
        private static readonly string[] RiftAdjectives = { "Shimmering", "Hungry", "Broken", "Violet", "Wailing", "Starless", "Unquiet", "Weeping" };
        private static readonly string[] RuinNouns = { "Shrine", "Span", "Chapel", "Watchtower", "Halls", "Cairn", "Circle", "Abbey", "Tower" };
        private static readonly string[] CampNouns = { "Camp", "Encampment", "Hideout", "Den", "Stockade", "Warren" };
        private static readonly string[] CampAdjectives = { "Bandit", "Raiders'", "Outlaw", "Smoking", "Muddy", "Thieves'", "Brigand" };
        private static readonly string[] RuinAdjectives = { "Broken", "Hollow", "Fallen", "Roofless", "Sunken", "Burnt", "Forgotten", "Old" };

        private static string[] DeepAdjectives(BiomeType biome)
        {
            switch (biome)
            {
                case BiomeType.Forest: return new[] { "Tangled", "Whispering", "Gloom", "Rootbound", "Hollow" };
                case BiomeType.Lakeland: return new[] { "Sunken", "Drowned", "Dripping", "Misty", "Still" };
                case BiomeType.Highland: return new[] { "Frozen", "Howling", "Echoing", "Pale", "Deep" };
                case BiomeType.Volcanic: return new[] { "Ashen", "Burning", "Smouldering", "Molten", "Black" };
                case BiomeType.Swamp: return new[] { "Drowned", "Festering", "Sodden", "Black", "Sunken" };
                case BiomeType.Desert: return new[] { "Buried", "Bleached", "Sunless", "Forgotten", "Dry" };
                default: return new[] { "Hollow", "Weeping", "Old", "Silent", "Mossy" };
            }
        }

        private static string[] NodeWords(ResourceTrade trade)
        {
            switch (trade)
            {
                case ResourceTrade.Miner: return new[] { "Mine", "Diggings", "Seams" };
                case ResourceTrade.Forester: return new[] { "Stand", "Loggings", "Sawpits" };
                case ResourceTrade.Farmer: return new[] { "Fields", "Farm", "Acres" };
                case ResourceTrade.Quarrier: return new[] { "Quarry", "Delves", "Cuttings" };
                default: return new[] { "Snares", "Traplines", "Runs" };
            }
        }

        /// <summary>One try at a site's name.</summary>
        public static string SiteName(WorldRegionData region, string regionName, SiteSpec site, int attempt = 0)
        {
            if (region == null || site == null) return "";
            var biome = region.Biome;
            var random = DeterministicRandom.ForSubject((ulong)(uint)region.Seed, Hash(site.SiteId) + (ulong)attempt);
            var roots = Roots(biome);

            switch (site.Type)
            {
                case LocationType.Castle:
                {
                    // The keep is the region's seat, so it carries the region's name.
                    string word = Pick(KeepWords, ref random);
                    if (regionName != null && regionName.EndsWith(word, StringComparison.OrdinalIgnoreCase)) word = "Keep";
                    return attempt == 0 ? $"{regionName} {word}" : $"{PlaceWord(biome, ref random)} {word}";
                }
                case LocationType.Outpost:
                    switch (random.Next(3))
                    {
                        case 0: return Compound(Pick(roots, ref random), "watch") ?? $"{Pick(roots, ref random)} Tower";
                        case 1: return $"{Compound(Pick(roots, ref random), "gate") ?? Pick(roots, ref random)} Tower";
                        default: return $"{Pick(roots, ref random)} Tower";
                    }
                case LocationType.NeutralHome:
                    return Compound(Pick(roots, ref random), Pick(VillageEndings, ref random)) ?? PlaceWord(biome, ref random);
                case LocationType.Dungeon:
                    // "Emberdeep" reads as a place under the ground; "Barleydeep" does not, so farmland's
                    // caves are always described rather than compounded.
                    switch (random.Next(biome == BiomeType.Grassland ? 2 : 3))
                    {
                        case 0: return Phrase(DeepAdjectives(biome), DeepNouns, ref random);
                        case 1: return $"The {Phrase(DeepAdjectives(biome), DeepNouns, ref random)}";
                        default: return Compound(Pick(roots, ref random), "deep") ?? Phrase(DeepAdjectives(biome), DeepNouns, ref random);
                    }
                case LocationType.Portal:
                    return $"The {Phrase(RiftAdjectives, RiftNouns, ref random)}";
                case LocationType.Ruin:
                    switch (random.Next(3))
                    {
                        case 0: return $"Old {Pick(roots, ref random)} {Pick(RuinNouns, ref random)}";
                        case 1: return $"The {Phrase(RuinAdjectives, RuinNouns, ref random)}";
                        default: return $"{Compound(Pick(roots, ref random), "side") ?? Pick(roots, ref random)} {Pick(RuinNouns, ref random)}";
                    }
                case LocationType.Camp:
                    switch (random.Next(3))
                    {
                        case 0: return $"{Pick(roots, ref random)} {Pick(CampNouns, ref random)}";
                        case 1: return $"The {Phrase(CampAdjectives, CampNouns, ref random)}";
                        default: return $"{Pick(CampAdjectives, ref random)} {Pick(CampNouns, ref random)}";
                    }
                case LocationType.ResourceNode:
                    // Named for what is worked there, so a "Quarry" is never a farm.
                    return $"{PlaceWord(biome, ref random)} {Pick(NodeWords(ResourceNodeRules.TradeOf(site.SiteId, biome)), ref random)}";
                default:
                    return PlaceWord(biome, ref random);
            }
        }

        /// <summary>
        /// Every site in a region, named and unique within it (named in the order the generator lists
        /// them; a clash rolls the later one again).
        /// </summary>
        public static Dictionary<string, string> SiteNames(WorldRegionData region, string regionName, IEnumerable<SiteSpec> sites)
        {
            var names = new Dictionary<string, string>();
            if (region == null || sites == null) return names;
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { regionName ?? "" };

            foreach (var site in sites.Where(s => s != null && !string.IsNullOrEmpty(s.SiteId)))
            {
                string name = null;
                for (int attempt = 0; attempt < 16; attempt++)
                {
                    name = SiteName(region, regionName, site, attempt);
                    if (taken.Add(name)) break;
                    name = null;
                }
                names[site.SiteId] = name ?? SiteName(region, regionName, site);
            }
            return names;
        }

        #endregion

        #region People

        /// <summary>
        /// The race a sheet shows, from its LinkName: "Orc" from <c>Ally_Orc_Mage_2</c>, "Lizard" from
        /// <c>Enemy_Lakeland_Lizard_Boss_1</c>. Empty when the name carries none (the older sheets).
        /// </summary>
        public static string Race(string linkName)
        {
            if (string.IsNullOrEmpty(linkName)) return "";
            var parts = linkName.Split('_');
            if (parts[0] == "Ally" && parts.Length >= 3) return parts[1];
            if (parts[0] == "Enemy" && parts.Length >= 4) return parts[2];
            if (parts[0] == "Npc" && parts.Length >= 3) return parts[parts.Length >= 4 ? 2 : 1];
            return "";
        }

        private sealed class Folk
        {
            public string[] Given;
            public string[] SurnameStarts;
            public string[] SurnameEnds;
        }

        private static readonly Folk Humans = new Folk
        {
            Given = new[] { "Aldric", "Brenna", "Corvin", "Dessa", "Eamon", "Fenna", "Garrick", "Hesper", "Isolde", "Jorunn", "Kestrel", "Lisbet", "Rhea", "Tamsin", "Wren", "Osric", "Elowen", "Perrin", "Harrow", "Vance", "Mab", "Corr", "Wynn", "Lio" },
            SurnameStarts = new[] { "Fair", "Dusk", "Hollow", "Ash", "Brook", "Oak", "Grey", "Hart", "Mill", "Thistle", "Aster", "Fallow" },
            SurnameEnds = new[] { "wind", "bane", "brand", "ford", "wood", "well", "stone", "ridge", "wick", "vane" },
        };

        private static readonly Folk Brutes = new Folk
        {
            Given = new[] { "Brog", "Krug", "Hakkar", "Grul", "Thrak", "Mogra", "Urzog", "Gorm", "Drakka", "Rukk", "Varga", "Skarn", "Ord", "Bruzga", "Ulka", "Bolg" },
            SurnameStarts = new[] { "Stone", "Iron", "Blood", "Skull", "Ash", "Grim", "Bone", "Mud", "Gut", "Rock" },
            SurnameEnds = new[] { "jaw", "maw", "tusk", "fist", "hide", "tide", "brow", "horn", "crusher", "splitter" },
        };

        private static readonly Folk Elves = new Folk
        {
            Given = new[] { "Nyx", "Sera", "Vael", "Ilm", "Zyra", "Iskar", "Velis", "Maerwen", "Sylith", "Kaelen", "Riven", "Ithra" },
            SurnameStarts = new[] { "Dusk", "Night", "Shade", "Moon", "Star", "Veil", "Umber", "Hollow" },
            SurnameEnds = new[] { "bane", "fall", "song", "veil", "thorn", "whisper", "brand", "glass" },
        };

        private static readonly Folk Fauns = new Folk
        {
            Given = new[] { "Pip", "Fennick", "Tansy", "Orrin", "Linnet", "Hob", "Sorrel", "Quince", "Bryn", "Marram", "Tibbet", "Wort" },
            SurnameStarts = new[] { "Thistle", "Quick", "Moss", "Clover", "Burr", "Hazel", "Bramble", "Dew" },
            SurnameEnds = new[] { "wick", "fletch", "foot", "hop", "leaf", "whistle", "cap", "root" },
        };

        private static readonly Folk Cats = new Folk
        {
            Given = new[] { "Mira", "Tibb", "Sable", "Kitsa", "Rhen", "Ashi", "Nima", "Zari", "Tamsa", "Oriel", "Pell", "Suri" },
            SurnameStarts = new[] { "Soft", "Silk", "Swift", "Night", "Amber", "Tuft", "Quiet", "Sly" },
            SurnameEnds = new[] { "paw", "whisker", "tail", "step", "eye", "stripe", "purr", "coat" },
        };

        private static readonly Folk Wolves = new Folk
        {
            Given = new[] { "Varek", "Ulla", "Grimm", "Hrolf", "Skadi", "Torva", "Fenn", "Rurik", "Ylva", "Brand", "Kaja", "Wulfric" },
            SurnameStarts = new[] { "Grey", "Frost", "Moon", "Iron", "Winter", "Storm", "Ash", "Pine" },
            SurnameEnds = new[] { "fang", "mane", "howl", "pelt", "claw", "runner", "coat", "track" },
        };

        private static readonly Folk Scaled = new Folk
        {
            Given = new[] { "Vosk", "Ssereth", "Kithra", "Zhal", "Ixxa", "Tessk", "Vrask", "Sathis", "Ksarr", "Oszh" },
            SurnameStarts = new[] { "Tide", "Scale", "Mire", "Coil", "Silt", "Green", "Cold", "Reed" },
            SurnameEnds = new[] { "tongue", "scale", "coil", "fang", "eye", "tail", "maw", "spine" },
        };

        private static readonly Folk Dead = new Folk
        {
            Given = new[] { "Mordrith", "Vaelis", "Corvane", "Ashur", "Nekhet", "Morwen", "Osk", "Carrow", "Sepra", "Hollis" },
            SurnameStarts = new[] { "Grave", "Bone", "Dust", "Pale", "Hollow", "Ash", "Cold", "Rot" },
            SurnameEnds = new[] { "shroud", "grin", "hand", "bane", "wail", "gaze", "mourn", "rattle" },
        };

        private static readonly Folk Demons = new Folk
        {
            Given = new[] { "Azgar", "Vhor", "Malzeth", "Bael", "Xoth", "Imrith", "Zagan", "Ruthar", "Ghesh", "Orzhul" },
            SurnameStarts = new[] { "Cinder", "Brand", "Hell", "Char", "Ember", "Soot", "Pyre", "Ash" },
            SurnameEnds = new[] { "horn", "maw", "heart", "brand", "tongue", "claw", "crown", "blight" },
        };

        private static readonly Folk Beasts = new Folk
        {
            Given = new[] { "Gnash", "Skrit", "Scabb", "Fenrik", "Tikk", "Varg", "Rakka", "Mangel", "Snikt", "Grizz" },
            SurnameStarts = new[] { "Mange", "Rot", "Gnaw", "Blood", "Grey", "Scab", "Filth", "Night" },
            SurnameEnds = new[] { "tooth", "tail", "fang", "claw", "ear", "hide", "snout", "howl" },
        };

        private static Folk FolkOf(string race)
        {
            switch (race)
            {
                case "Orc": case "Boarman": case "Troll": case "Minotaur": case "Worotour": case "Cyclops": case "Goblin":
                    return Brutes;
                case "DarkElf": case "Elf": return Elves;
                case "Faun": return Fauns;
                case "HalfCat": return Cats;
                case "HalfWolf": return Wolves;
                case "Lizard": case "Drakan": case "Drake": return Scaled;
                case "Skeleton": case "Zombie": case "Marked": return Dead;
                case "Demon": return Demons;
                case "Wolf": case "Rat": return Beasts;
                default: return Humans;
            }
        }

        private static string Surname(Folk folk, ref DeterministicRandom random)
        {
            for (int i = 0; i < 8; i++)
            {
                var word = Compound(Pick(folk.SurnameStarts, ref random), Pick(folk.SurnameEnds, ref random));
                if (word != null) return word;
            }
            return Cap(folk.SurnameStarts[0]) + folk.SurnameEnds[1];
        }

        /// <summary>A hero's full name for a sheet ("Brog Stonejaw", "Pip Quickfletch").</summary>
        public static string ForHero(string linkName, ulong seed)
        {
            var folk = FolkOf(Race(linkName));
            var random = DeterministicRandom.ForSubject(seed, 0x6865726F);
            return $"{Pick(folk.Given, ref random)} {Surname(folk, ref random)}";
        }

        /// <summary>
        /// What to call a character: the name it was given, else one rolled from its id - so the
        /// starting roster, which nobody named, reads as people rather than as "Orc Warrior", and
        /// the same character is called the same thing on every screen and by the server.
        ///
        /// <para>A "given" name that is only its type (<paramref name="typeName"/>, "Archer") counts as
        /// none: the starting roster used to be stamped with it, so every save made before then holds
        /// heroes called by their class.</para>
        /// </summary>
        public static string ForCharacter(string givenName, string linkName, string characterId, string typeName = null)
        {
            bool isOnlyItsType = !string.IsNullOrWhiteSpace(typeName)
                && string.Equals(givenName?.Trim(), typeName.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(givenName) && !isOnlyItsType) return givenName;
            if (string.IsNullOrEmpty(characterId)) return null;
            return ForHero(linkName, Hash(characterId));
        }

        private static string[] BossTitles(BiomeType biome)
        {
            switch (biome)
            {
                case BiomeType.Forest: return new[] { "Thornlord", "Warden", "Rootmother" };
                case BiomeType.Lakeland: return new[] { "Tidecaller", "Reedking", "Weirwarden" };
                case BiomeType.Highland: return new[] { "Frostwarden", "Stonelord", "Cairnkeeper" };
                case BiomeType.Volcanic: return new[] { "Flamecaller", "Ashlord", "Emberwarden" };
                case BiomeType.Swamp: return new[] { "Mirecaller", "Rotwarden", "Bogking" };
                case BiomeType.Desert: return new[] { "Duneseer", "Sandlord", "Tombwarden" };
                default: return new[] { "Warlord", "Reaver", "Raid-Leader" };
            }
        }

        private static readonly string[] Epithets = { "the Unbroken", "the Pale", "the Cruel", "the Old", "the Patient", "the Hollow", "the Quiet", "the Hungry" };

        private static string BiomeEpithet(BiomeType biome)
        {
            switch (biome)
            {
                case BiomeType.Forest: return "the Thorned";
                case BiomeType.Lakeland: return "the Drowned";
                case BiomeType.Highland: return "the Frozen";
                case BiomeType.Volcanic: return "the Burning";
                case BiomeType.Swamp: return "the Rotten";
                case BiomeType.Desert: return "the Sunblind";
                default: return "the Reaper";
            }
        }

        /// <summary>
        /// A boss's name ("Tidecaller Vosk", "Krug Ashmaw", "Morwen the Pale"): its race's names, its
        /// land's titles. Seeded by the fight, so the lord of a standing dungeon is the same lord on
        /// every attempt, and a new one moves in when the site recovers.
        /// </summary>
        public static string ForBoss(string linkName, BiomeType biome, ulong seed)
        {
            var folk = FolkOf(Race(linkName));
            var random = DeterministicRandom.ForSubject(seed, 0x626F7373);
            string given = Pick(folk.Given, ref random);
            switch (random.Next(3))
            {
                case 0: return $"{Pick(BossTitles(biome), ref random)} {given}";
                case 1: return $"{given} {Surname(folk, ref random)}";
                default:
                    return $"{given} {(random.Next(3) == 0 ? BiomeEpithet(biome) : Pick(Epithets, ref random))}";
            }
        }

        private static readonly string[] BandWords = { "Warband", "Raiders", "Reavers", "Raid" };
        private static readonly string[] BandEndings = { "walkers", "guard", "blades", "fangs", "riders", "knives" };

        /// <summary>
        /// Who sprang an ambush ("Grimjaw Warband", "The Fenwalkers", "Thornwood Raiders"): a faction's
        /// band on its own land, else a band named for the land.
        /// </summary>
        public static string ForWarband(FactionId faction, BiomeType biome, ulong seed)
        {
            var random = DeterministicRandom.ForSubject(seed, 0x62616E64);
            if (faction == FactionId.Grimjaw) return $"Grimjaw {Pick(BandWords, ref random)}";
            if (faction == FactionId.Ashkin) return $"Ashkin {Pick(BandWords, ref random)}";
            if (random.Next(2) == 0)
                return $"The {Compound(Pick(Roots(biome), ref random), Pick(BandEndings, ref random)) ?? PlaceWord(biome, ref random)}";
            return $"{PlaceWord(biome, ref random)} {Pick(BandWords, ref random)}";
        }

        /// <summary>A veteran worker's by-name, in their trade's voice ("the Steady Axe", "Stonejaw").</summary>
        private static readonly Dictionary<ResourceTrade, string[]> TradeByNames = new Dictionary<ResourceTrade, string[]>
        {
            [ResourceTrade.Miner] = new[] { "the Deep Pick", "Stonelung", "the Lamp", "Ironhand", "the Mole", "Seamfinder" },
            [ResourceTrade.Forester] = new[] { "the Steady Axe", "Oakarm", "the Feller", "Barkhide", "the Long Saw", "Greenwood" },
            [ResourceTrade.Farmer] = new[] { "the Sower", "Goldsheaf", "the Early Riser", "Furrowfoot", "the Scythe", "Rainwise" },
            [ResourceTrade.Quarrier] = new[] { "Stonejaw", "the Hammer", "Flintback", "the Wedge", "Granite", "the Mason" },
            [ResourceTrade.Trapper] = new[] { "the Quiet Snare", "Quickhands", "the Fox", "Longstride", "the Tracker", "Hideclaw" }
        };

        /// <summary>By-names a trait leans toward, whatever the trade.</summary>
        private static readonly Dictionary<WorkerTrait, string[]> TraitByNames = new Dictionary<WorkerTrait, string[]>
        {
            [WorkerTrait.Steady] = new[] { "the Steady", "Neverstop" },
            [WorkerTrait.Hometown] = new[] { "the Homegrown", "Hearthborn" },
            [WorkerTrait.Versatile] = new[] { "Twohands", "the Handy" },
            [WorkerTrait.Prospector] = new[] { "the Prospector", "Richvein" },
            [WorkerTrait.Lucky] = new[] { "the Lucky", "Fortune's Friend" },
            [WorkerTrait.Foreman] = new[] { "the Boss", "Loudvoice" }
        };

        /// <summary>
        /// A veteran worker's by-name (Workers spec §3.5): mostly their trade's, one in three their
        /// traits'. A function of the worker's id and traits. The trade's word and the one-in-three
        /// draw come from streams of their own, so a trait gained by promotion changes the by-name
        /// only when that draw already fell to the traits.
        /// </summary>
        public static string WorkerByName(ResourceTrade trade, IReadOnlyCollection<WorkerTrait> traits, ulong seed)
        {
            var tradeRandom = DeterministicRandom.ForSubject(seed, 0x77726B72);
            string tradeWord = TradeByNames.TryGetValue(trade, out var words) ? Pick(words, ref tradeRandom) : "the Hand";
            var traitRandom = DeterministicRandom.ForSubject(seed, 0x74726169);
            if (traitRandom.Next(3) != 0) return tradeWord;
            var traitWords = (traits ?? Array.Empty<WorkerTrait>()).OrderBy(t => t)
                .Where(TraitByNames.ContainsKey).SelectMany(t => TraitByNames[t]).ToArray();
            return traitWords.Length > 0 ? Pick(traitWords, ref traitRandom) : tradeWord;
        }

        #endregion
    }
}
