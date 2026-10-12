using System.Text.Json.Nodes;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Factions 2, step 1: the catalogue replaces the hard-coded Grimjaw/Ashkin pair, with no change in behaviour. A world
/// with no recorded cast plays the old pair; a faction that arrives by an event is in play once it holds land.
/// </summary>
public class FactionCatalogTests
{
    [Fact]
    public void TheCatalogue_KnowsThePair_AndTheRiftborn()
    {
        Assert.True(FactionCatalog.Has(FactionId.Grimjaw));
        Assert.True(FactionCatalog.Has(FactionId.Ashkin));
        Assert.True(FactionCatalog.Has(FactionId.Riftborn));
        Assert.False(FactionCatalog.Has(FactionId.None));
        Assert.False(FactionCatalog.Has(FactionId.Player));
        Assert.Equal(new[] { FactionId.Grimjaw, FactionId.Ashkin }, FactionCatalog.DefaultCast);
        Assert.Equal("The Grimjaw", FactionCatalog.TitleOf(FactionId.Grimjaw));
        Assert.Equal("the Ashkin", FactionCatalog.Ashkin.TitleInSentence);
        Assert.True(FactionStrengthRules.IsFaction(FactionId.Ashkin));
    }

    [Theory]
    [InlineData(BiomeType.Grassland, FactionId.Grimjaw)]
    [InlineData(BiomeType.Forest, FactionId.Grimjaw)]
    [InlineData(BiomeType.Lakeland, FactionId.Grimjaw)]
    [InlineData(BiomeType.Swamp, FactionId.Grimjaw)]
    [InlineData(BiomeType.Desert, FactionId.Grimjaw)]
    [InlineData(BiomeType.Highland, FactionId.Ashkin)]
    [InlineData(BiomeType.Volcanic, FactionId.Ashkin)]
    public void Seating_GivesEachBiomeToTheFactionItAlwaysWentTo(BiomeType biome, FactionId expected)
    {
        // The old rule: the Ashkin take Volcanic and Highland, the Grimjaw everything else.
        Assert.Equal(expected, FactionCatalog.SeatFor(biome, FactionCatalog.DefaultCast));
    }

    [Fact]
    public void AGeneratedWorld_NamesItsFactions_AsEveryOtherWriterDoes()
    {
        var regions = WorldMapGenerator.Generate(12345, new List<WorldMapGenerator.PlayerSeat>());
        var held = regions.Where(r => FactionCatalog.Has(r.Faction)).ToList();
        Assert.NotEmpty(held);
        Assert.All(held, r => Assert.Equal(FactionCatalog.TitleOf(r.Faction), r.OwnerDisplayName));
        Assert.DoesNotContain(regions, r => r.OwnerDisplayName == "Grimjaw Clan");
    }

    [Fact]
    public void AWarband_IsNamedForItsFaction()
    {
        StringAssert(Naming.ForWarband(FactionId.Grimjaw, BiomeType.Forest, 7), "Grimjaw ");
        StringAssert(Naming.ForWarband(FactionId.Ashkin, BiomeType.Volcanic, 7), "Ashkin ");

        static void StringAssert(string name, string prefix) => Assert.StartsWith(prefix, name);
    }

    [Fact]
    public void AWorldWithNoCast_PlaysTheOldPair_AndARecordedOneIsRead()
    {
        Assert.Equal(FactionCatalog.DefaultCast, WorldRegionBlob.ReadCast(JsonNode.Parse("""{"Regions":[]}""")));
        Assert.Equal(new[] { FactionId.Ashkin },
            WorldRegionBlob.ReadCast(JsonNode.Parse("""{"Factions":[2, 2, 0, 1]}""")));
    }

    [Fact]
    public void InPlay_IsTheCast_ThenAnyOtherHoldingLand()
    {
        var regions = new List<WorldRegionData>
        {
            new() { RegionId = "a", Faction = FactionId.Ashkin, Ownership = LocationOwnership.Enemy },
        };
        Assert.Equal(new[] { FactionId.Grimjaw, FactionId.Ashkin }, FactionCatalog.InPlay(null, regions));
        Assert.Equal(new[] { FactionId.Grimjaw, FactionId.Ashkin }, FactionCatalog.InPlay(new[] { FactionId.Grimjaw }, regions));
    }
}
