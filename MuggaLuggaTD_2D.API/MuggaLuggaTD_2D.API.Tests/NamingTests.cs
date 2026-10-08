using MuggaLuggaTD.Shared.World;
using Xunit.Abstractions;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Generated names. Nothing about a name is stored, so the promises are: the same seed always gives
/// the same name (on both sides), a world never has two regions of one name, a region never has two
/// sites of one name, and every name reads as a name (no empty, no doubled letter at a join).
/// </summary>
public class NamingTests
{
    private readonly ITestOutputHelper _output;

    public NamingTests(ITestOutputHelper output) => _output = output;

    private static readonly List<WorldMapGenerator.PlayerSeat> Seats = new()
    {
        new("user-a", "Aldric"), new("user-b", "Brenna"), new("user-c", "Corvin")
    };

    [Fact]
    public void RegionNames_AreUniqueAcrossTheWorld_AndStable()
    {
        foreach (int seed in new[] { 1, 7, 42, 1234, 99991 })
        {
            var world = WorldMapGenerator.Generate(seed, Seats);
            var names = Naming.RegionNames(world);

            Assert.Equal(world.Count, names.Count);
            Assert.Equal(names.Count, names.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(names.Values, n => Assert.False(string.IsNullOrWhiteSpace(n)));

            // Order of the input must not matter: two players may hold their regions in any order.
            var again = Naming.RegionNames(Enumerable.Reverse(WorldMapGenerator.Generate(seed, Seats)).ToList());
            Assert.Equal(names, again);
        }
    }

    [Fact]
    public void SiteNames_AreUniqueWithinARegion_AndTheKeepCarriesTheRegionsName()
    {
        var world = WorldMapGenerator.Generate(42, Seats);
        var regionNames = Naming.RegionNames(world);

        foreach (var region in world)
        {
            var layout = RegionGenerator.Generate(region);
            var names = Naming.SiteNames(region, regionNames[region.RegionId], layout.Sites);

            Assert.Equal(layout.Sites.Count, names.Count);
            Assert.Equal(names.Count, names.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(names.Values, n => Assert.False(string.IsNullOrWhiteSpace(n)));

            var keep = layout.Sites.FirstOrDefault(s => s.Type == LocationType.Castle);
            if (keep != null) Assert.StartsWith(regionNames[region.RegionId], names[keep.SiteId]);
        }
    }

    [Fact]
    public void People_AreNamedInTheirOwnVoice_AndTheSameSeedGivesTheSameName()
    {
        Assert.Equal("Orc", Naming.Race("Ally_Orc_Mage_2"));
        Assert.Equal("Lizard", Naming.Race("Enemy_Lakeland_Lizard_Boss_1"));
        Assert.Equal("", Naming.Race("Ally_Archer"));

        Assert.Equal(Naming.ForHero("Ally_Orc_Mage_2", 77), Naming.ForHero("Ally_Orc_Mage_2", 77));
        Assert.Equal(Naming.ForBoss("Enemy_Lakeland_Lizard_Boss_1", BiomeType.Lakeland, 5),
            Naming.ForBoss("Enemy_Lakeland_Lizard_Boss_1", BiomeType.Lakeland, 5));

        // A name someone was given wins; an unnamed character is named from its id, the same way twice.
        Assert.Equal("Sir Aldric", Naming.ForCharacter("Sir Aldric", "Ally_Human_Warrior_1", "c1"));
        Assert.Equal(Naming.ForCharacter(null, "Ally_Human_Warrior_1", "c1"), Naming.ForCharacter("", "Ally_Human_Warrior_1", "c1"));
        Assert.Contains(' ', Naming.ForCharacter(null, "Ally_Human_Warrior_1", "c1"));

        // A starter stamped with its type ("Archer") was never named: it gets the rolled name.
        Assert.Equal(Naming.ForCharacter(null, "Ally_Archer", "c2"), Naming.ForCharacter("Archer", "Ally_Archer", "c2", "Archer"));
        Assert.Equal("Sir Aldric", Naming.ForCharacter("Sir Aldric", "Ally_Archer", "c2", "Archer"));

        // Different seeds should mostly give different people.
        var heroes = Enumerable.Range(0, 200).Select(i => Naming.ForHero("Ally_Human_Warrior_1", (ulong)i)).ToList();
        Assert.True(heroes.Distinct().Count() > 150, $"only {heroes.Distinct().Count()} distinct of 200");
    }

    [Fact]
    public void NoJoinDoublesALetter()
    {
        var words = new List<string>();
        foreach (var world in new[] { WorldMapGenerator.Generate(3, Seats), WorldMapGenerator.Generate(8, Seats) })
        {
            var regionNames = Naming.RegionNames(world);
            words.AddRange(regionNames.Values);
            foreach (var region in world)
                words.AddRange(Naming.SiteNames(region, regionNames[region.RegionId], RegionGenerator.Generate(region).Sites).Values);
        }
        foreach (var race in new[] { "Ally_Orc_Mage_1", "Ally_Faun_Archer_1", "Enemy_Volcanic_Demon_Boss_1", "Enemy_Desert_Skeleton_Boss_1" })
            for (ulong i = 0; i < 50; i++)
            {
                words.Add(Naming.ForHero(race, i));
                words.Add(Naming.ForBoss(race, BiomeType.Volcanic, i));
            }

        // "Ashhold", "Stonestone": a compound whose halves meet on the same letter reads as a typo.
        // A triple letter can only come from such a join.
        Assert.DoesNotContain(words, w => System.Text.RegularExpressions.Regex.IsMatch(w, @"(\w)\1\1"));
        // "The Hollow Hollow": a phrase that repeats its own word.
        Assert.DoesNotContain(words, w => System.Text.RegularExpressions.Regex.IsMatch(w, @"\b(\w+) \1\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    /// <summary>Not an assertion: a sample to read, for whoever next tunes the word lists.</summary>
    [Fact]
    public void Sample()
    {
        var world = WorldMapGenerator.Generate(42, Seats);
        var regionNames = Naming.RegionNames(world);
        foreach (var region in world.Take(8))
        {
            var sites = Naming.SiteNames(region, regionNames[region.RegionId], RegionGenerator.Generate(region).Sites);
            _output.WriteLine($"{region.Biome,-9} {regionNames[region.RegionId],-14} | {string.Join(", ", sites.Values)}");
            _output.WriteLine($"          boss {Naming.ForBoss("Enemy_X_Troll_Boss_1", region.Biome, (ulong)region.Seed)} · band {Naming.ForWarband(region.Faction, region.Biome, (ulong)region.Seed)}");
        }
        foreach (var sheet in new[] { "Ally_Human_Warrior_1", "Ally_Orc_Mage_1", "Ally_Faun_Archer_1", "Ally_HalfCat_Cleric_1", "Ally_DarkElf_Mage_1", "Ally_HalfWolf_Warrior_1" })
            _output.WriteLine($"{sheet,-24} {Naming.ForHero(sheet, 1)}, {Naming.ForHero(sheet, 2)}");
    }
}
