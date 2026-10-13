using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Factions 2, step 2: a different cast of three in each world. What a player should be able to rely on: the cast is
/// the same every time a world is built from its seed; it always has a raider; each of the three holds land; and
/// drawing it moved nothing else about the map (biomes, tiers, seats).
/// </summary>
public class FactionCastTests
{
    private static readonly List<WorldMapGenerator.PlayerSeat> Seats = Enumerable.Range(0, 4)
        .Select(i => new WorldMapGenerator.PlayerSeat($"user-{i}", $"Herald {i}")).ToList();

    /// <summary>
    /// The map without its factions, over 50 seeds, folded into one number: every region's id, biome, tier, seed and
    /// owning player. Taken from the generator before the cast existed (2026-10-12), so a change to the seating that
    /// shifts the shared random stream fails here.
    /// </summary>
    private const ulong MapFingerprint = 9118074844005794433UL;

    private static ulong Fingerprint()
    {
        ulong hash = 1469598103934665603UL;
        void Add(string s)
        {
            foreach (char c in s) { hash ^= c; hash *= 1099511628211UL; }
            hash ^= '|'; hash *= 1099511628211UL;
        }
        for (int seed = 1; seed <= 50; seed++)
            foreach (var r in WorldMapGenerator.Generate(seed, Seats))
                Add($"{r.RegionId}:{(int)r.Biome}:{r.Tier}:{r.Seed}:{r.OwnerUserId}:{r.IsCapital}");
        return hash;
    }

    [Fact]
    public void DrawingTheCast_MovesNothingElseOnTheMap()
    {
        Assert.Equal(MapFingerprint, Fingerprint());
    }
    [Fact]
    public void AWorldsCast_IsThreeDistinctFactions_TheSameEveryTime_WithARaider()
    {
        var seen = new HashSet<FactionId>();
        for (int seed = 1; seed <= 200; seed++)
        {
            var regions = WorldMapGenerator.Generate(seed, Seats);
            var cast = WorldMapGenerator.CastFor(seed, regions);
            Assert.Equal(FactionCatalog.CastSize, cast.Distinct().Count());
            Assert.Equal(cast, WorldMapGenerator.CastFor(seed, WorldMapGenerator.Generate(seed, Seats)));
            Assert.Contains(cast, f => FactionCatalog.Of(f)!.Raider);
            Assert.All(cast, f => Assert.True(FactionCatalog.Of(f)!.Seatable));
            seen.UnionWith(cast);
        }
        // Over many worlds, every seatable faction turns up somewhere.
        Assert.All(FactionCatalog.All.Where(f => f.Seatable), f => Assert.Contains(f.Id, seen));
    }

    [Fact]
    public void EveryFactionOfTheCast_HoldsLandFromTheStart_AndNobodyElseDoes()
    {
        for (int seed = 1; seed <= 100; seed++)
        {
            var regions = WorldMapGenerator.Generate(seed, Seats);
            var cast = WorldMapGenerator.CastFor(seed, regions);
            foreach (var faction in cast)
                Assert.True(regions.Count(r => r.Faction == faction) >= WorldMapGenerator.MinimumFactionRegions,
                    $"seed {seed}: {faction} holds too little");
            Assert.All(regions.Where(r => r.Ownership == LocationOwnership.Enemy), r => Assert.Contains(r.Faction, cast));
            Assert.All(regions.Where(r => r.Faction != FactionId.None && r.Ownership == LocationOwnership.Enemy),
                r => Assert.Equal(FactionCatalog.TitleOf(r.Faction), r.OwnerDisplayName));
        }
    }

    [Fact]
    public void ANewWorld_RecordsItsCast_AndAnOldOneReadsAsTheOldPair()
    {
        var regions = WorldMapGenerator.Generate(7, Seats);
        var cast = WorldMapGenerator.CastFor(7, regions);
        Assert.Equal(cast, WorldRegionBlob.ReadCast(WorldRegionBlob.BuildWorld(7, regions, cast)));
        Assert.Equal(FactionCatalog.DefaultCast, WorldRegionBlob.ReadCast(WorldRegionBlob.BuildWorld(7, regions)));
    }

    [Fact]
    public void AFactionsLand_IsFoughtByItsOwnPeoples_AndOtherLandByTheBiomes()
    {
        var peoples = new FakeGameContent().EnemyPeoples;
        var known = peoples.Values.SelectMany(p => p).ToHashSet();
        foreach (var faction in FactionCatalog.All.Where(f => f.Seatable))
        {
            var theirs = QuestRules.PeoplesOf(BiomeType.Grassland, peoples, faction.Id);
            // Its own peoples that content has; a faction whose peoples are not in it falls back to the biome's.
            var expected = faction.People.Where(known.Contains).ToList();
            Assert.Equal(expected.Count > 0 ? expected : QuestRules.PeoplesOf(BiomeType.Grassland, peoples), theirs);
        }
        Assert.Equal(QuestRules.PeoplesOf(BiomeType.Desert, peoples), QuestRules.PeoplesOf(BiomeType.Desert, peoples, FactionId.None));
        Assert.Equal(QuestRules.PeoplesOf(BiomeType.Desert, peoples), QuestRules.PeoplesOf(BiomeType.Desert, peoples, FactionId.Riftborn));

        var held = new WorldRegionData { RegionId = "r1", Ownership = LocationOwnership.Enemy, Faction = FactionId.HornedKin };
        Assert.Equal(FactionId.HornedKin, FactionCatalog.PeopleHolderOf(held));
        held.OwnerUserId = "someone";
        Assert.Equal(FactionId.None, FactionCatalog.PeopleHolderOf(held));
    }
}
