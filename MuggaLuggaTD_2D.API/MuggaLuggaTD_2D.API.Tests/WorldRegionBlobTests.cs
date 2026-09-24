using System.Text.Json.Nodes;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The world blob's reader and editor.
///
/// <para>This is the layer every server-authoritative world change goes through, and it is where the
/// region design's central bet lives: sites are not stored, they are regenerated from a seed, and
/// only divergence is written down. If resolving a site id against the live world is wrong, then
/// every PvE claim is judged against the wrong place.</para>
/// </summary>
public class WorldRegionBlobTests
{
    // -----------------------------------------------------------------
    // Format version — what decides whether a world is regenerated
    // -----------------------------------------------------------------

    [Fact]
    public void AWorldWithNoFormatVersion_ReadsAsTheLegacyFormat()
    {
        // Format 1 predates the field entirely, so its absence is what identifies it.
        var world = JsonNode.Parse("{\"Locations\":[]}");

        Assert.Equal(WorldRegionBlob.LegacyFormatVersion, WorldRegionBlob.GetFormatVersion(world));
        Assert.False(WorldRegionBlob.IsRegionWorld(world));
    }

    [Fact]
    public void AWorldFromAnOlderRegionFormat_IsNotTreatedAsCurrent()
    {
        // The generator decides where sites stand, so an older generator's world has site ids that
        // mean different places. It must be regenerated rather than read.
        var stale = WorldRegionBlob.CurrentFormatVersion - 1;
        var world = JsonNode.Parse("{\"FormatVersion\":" + stale + ",\"Regions\":[]}");

        Assert.False(WorldRegionBlob.IsRegionWorld(world));
    }

    [Fact]
    public void AFreshlyBuiltWorld_IsAtTheCurrentFormat()
    {
        var world = TestWorld.Blob(TestWorld.Region());

        Assert.Equal(WorldRegionBlob.CurrentFormatVersion, WorldRegionBlob.GetFormatVersion(world));
        Assert.True(WorldRegionBlob.IsRegionWorld(world));
    }

    [Fact]
    public void AWorldFromTheFuture_IsAccepted()
    {
        // A server rolled back below a world it already wrote should not silently regenerate it.
        var ahead = WorldRegionBlob.CurrentFormatVersion + 1;
        var world = JsonNode.Parse("{\"FormatVersion\":" + ahead + ",\"Regions\":[]}");

        Assert.True(WorldRegionBlob.IsRegionWorld(world));
    }

    // -----------------------------------------------------------------
    // Reading regions back
    // -----------------------------------------------------------------

    [Fact]
    public void ARegionSurvivesBeingWrittenAndReadBack()
    {
        var original = TestWorld.Region("r7", q: 3, r: -2, tier: 3, biome: BiomeType.Highland);
        original.Entrenchment = 4;
        original.Resolve = 62;
        original.IsCapital = true;

        // Through the string, because that is how it is actually stored.
        var world = TestWorld.RoundTrip(TestWorld.Blob(original));
        var read = TestWorld.ReadRegion(world, "r7");

        Assert.Equal("r7", read.RegionId);
        Assert.Equal(new HexCoord(3, -2), read.Hex);
        Assert.Equal(original.Seed, read.Seed);
        Assert.Equal(BiomeType.Highland, read.Biome);
        Assert.Equal(3, read.Tier);
        Assert.Equal(4, read.Entrenchment);
        Assert.Equal(62, read.Resolve);
        Assert.True(read.IsCapital);
    }

    [Fact]
    public void ARegionsSeedIsWhatRebuildsItsSites_SoTheSameRegionAlwaysHasTheSameSites()
    {
        // The whole design rests on this: the server regenerates a region to judge a claim, and must
        // land on exactly the sites the client drew.
        var region = TestWorld.Region("r1");
        var read = TestWorld.ReadRegion(TestWorld.RoundTrip(TestWorld.Blob(region)), "r1");

        var before = TestWorld.SitesIn(region).Select(s => $"{s.SiteId}|{s.Type}|{s.Cell}|{s.Level}|{s.Tier}");
        var after = TestWorld.SitesIn(read).Select(s => $"{s.SiteId}|{s.Type}|{s.Cell}|{s.Level}|{s.Tier}");

        Assert.Equal(before, after);
    }

    [Fact]
    public void FindingARegionThatIsNotThere_ReturnsNothing()
    {
        var world = TestWorld.Blob(TestWorld.Region("r0"));

        Assert.Null(WorldRegionBlob.FindRegion(world, "r99"));
        Assert.Null(WorldRegionBlob.FindRegion(world, null));
        Assert.Null(WorldRegionBlob.FindRegion(null, "r0"));
    }

    [Fact]
    public void ReadingAllRegions_ReturnsEveryOne()
    {
        var world = TestWorld.Blob(
            TestWorld.Region("r0"),
            TestWorld.Region("r1", q: 1),
            TestWorld.OwnedBy(TestIds.Player, "r2"));

        var regions = WorldRegionBlob.ReadAllRegions(world);

        Assert.Equal(new[] { "r0", "r1", "r2" }, regions.Select(r => r.RegionId));
        Assert.Equal(TestIds.Player, regions[2].OwnerUserId);
    }

    // -----------------------------------------------------------------
    // Resolving a site id against the live world
    // -----------------------------------------------------------------

    [Fact]
    public void ASiteIdResolvesToTheSiteTheSeedGenerates()
    {
        var region = TestWorld.Region();
        var world = TestWorld.Blob(region);
        var dungeon = TestWorld.SiteOfType(region, LocationType.Dungeon);

        var resolved = WorldRegionBlob.ResolveSite(world, dungeon.SiteId);

        Assert.NotNull(resolved);
        Assert.Equal(dungeon.SiteId, resolved!.Site.SiteId);
        Assert.Equal(LocationType.Dungeon, resolved.Site.Type);
        Assert.Equal(region.RegionId, resolved.Region.RegionId);
        Assert.False(resolved.IsCleared);
    }

    [Theory]
    [InlineData("r0:999")]          // a site index the region does not have
    [InlineData("r9:0")]            // a region the world does not have
    [InlineData("not-a-site-id")]   // no region prefix at all
    [InlineData("")]
    [InlineData(null)]
    public void ASiteIdTheWorldDoesNotContain_ResolvesToNothing(string? siteId)
    {
        // A client naming a site that does not exist is refused here, which is what stops a claim
        // being judged against a place the server had to invent.
        var world = TestWorld.Blob(TestWorld.Region());

        Assert.Null(WorldRegionBlob.ResolveSite(world, siteId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryRegionHasExactlyOneKeep_AndItIsTheFirstSite(int tier)
    {
        // The keep is placed first so it takes the best ground; a region without one could never be
        // taken, and taking the keep is how a region changes hands.
        var region = TestWorld.Region($"r{tier}", tier: tier);
        var sites = TestWorld.SitesIn(region);

        Assert.Equal(LocationType.Castle, sites[0].Type);
        Assert.Single(sites, s => s.Type == LocationType.Castle);
    }

    // -----------------------------------------------------------------
    // Site overrides — the only thing that is written down
    // -----------------------------------------------------------------

    [Fact]
    public void ClearingASite_IsRememberedForThatSiteAlone()
    {
        var region = TestWorld.Region();
        var world = TestWorld.Blob(region);
        var dungeons = TestWorld.SitesIn(region).Where(s => s.Type == LocationType.Dungeon).ToList();
        Assert.True(dungeons.Count >= 2, "A tier 2 region should generate several dungeons.");

        var regionNode = WorldRegionBlob.FindRegion(world, region.RegionId)!;
        WorldRegionBlob.MarkCleared(regionNode, dungeons[0].SiteId);

        var stored = TestWorld.RoundTrip(world);
        Assert.True(TestWorld.IsCleared(stored, dungeons[0].SiteId));
        Assert.False(TestWorld.IsCleared(stored, dungeons[1].SiteId));
    }

    [Fact]
    public void ClearingASiteTwice_LeavesOneOverride()
    {
        var region = TestWorld.Region();
        var world = TestWorld.Blob(region);
        var siteId = TestWorld.DungeonIn(region);
        var regionNode = WorldRegionBlob.FindRegion(world, region.RegionId)!;

        WorldRegionBlob.MarkCleared(regionNode, siteId);
        WorldRegionBlob.MarkCleared(regionNode, siteId);

        var overrides = (JsonObject)regionNode["SiteOverrides"]!;
        Assert.Single(overrides);
        Assert.True(WorldRegionBlob.IsCleared(overrides[siteId]));
    }

    [Fact]
    public void AnOverrideKeepsWhateverElseIsAlreadyRecordedAgainstTheSite()
    {
        // Garrisons are written by the client into the same override. Clearing a site must not wipe
        // them, or a keep would lose its defenders the moment anything else happened there.
        var region = TestWorld.Region();
        var world = TestWorld.Blob(region);
        var siteId = TestWorld.DungeonIn(region);
        var regionNode = WorldRegionBlob.FindRegion(world, region.RegionId)!;

        var entry = WorldRegionBlob.EnsureOverride(regionNode, siteId);
        entry["GarrisonPower"] = 250f;
        entry["StoredYield"] = 7;

        WorldRegionBlob.MarkCleared(regionNode, siteId);

        var stored = TestWorld.RoundTrip(world);
        var after = WorldRegionBlob.GetOverride(WorldRegionBlob.FindRegion(stored, region.RegionId)!, siteId)!;
        Assert.True(after["Cleared"]!.GetValue<bool>());
        Assert.Equal(250f, after["GarrisonPower"]!.GetValue<float>());
        Assert.Equal(7, after["StoredYield"]!.GetValue<int>());
    }

    [Fact]
    public void ASiteWithNoOverride_IsNotCleared()
    {
        var region = TestWorld.Region();
        var world = TestWorld.Blob(region);

        Assert.False(TestWorld.IsCleared(world, TestWorld.DungeonIn(region)));
        Assert.False(WorldRegionBlob.IsCleared(null));
    }

    // -----------------------------------------------------------------
    // Capturing a region
    // -----------------------------------------------------------------

    [Fact]
    public void CapturingARegion_HandsItToThePlayer()
    {
        var world = TestWorld.Blob(TestWorld.Region("r0"));
        var regionNode = WorldRegionBlob.FindRegion(world, "r0")!;

        WorldRegionBlob.CaptureRegion(regionNode, TestIds.Player, "Mike");

        var read = TestWorld.ReadRegion(TestWorld.RoundTrip(world), "r0");
        Assert.Equal(LocationOwnership.Player, read.Ownership);
        Assert.Equal(TestIds.Player, read.OwnerUserId);
        Assert.Equal("Mike", read.OwnerDisplayName);
        Assert.Equal(FactionId.Player, read.Faction);
        Assert.True(read.IsOwnedByPlayer(TestIds.Player));
        Assert.False(read.IsOwnedByPlayer(TestIds.Rival));
    }

    [Fact]
    public void ARegionTakenByForce_DoesNotInheritItsPreviousOwnersMorale()
    {
        var ground = TestWorld.OwnedBy(TestIds.Rival);
        ground.Resolve = 12;
        var world = TestWorld.Blob(ground);
        var regionNode = WorldRegionBlob.FindRegion(world, ground.RegionId)!;

        WorldRegionBlob.CaptureRegion(regionNode, TestIds.Player, "Mike");

        Assert.Equal(100, TestWorld.ReadRegion(world, ground.RegionId).Resolve);
    }

    [Fact]
    public void CapturingARegion_LeavesItsFortificationsStanding()
    {
        // Entrenchment is built by repairing ruins. Taking the ground takes the walls with it —
        // resetting it would quietly undo the defender's investment on every hand-over.
        var ground = TestWorld.Region("r0");
        ground.Entrenchment = 4;
        ground.Tier = 3;
        var world = TestWorld.Blob(ground);

        WorldRegionBlob.CaptureRegion(WorldRegionBlob.FindRegion(world, "r0")!, TestIds.Player, "Mike");

        var read = TestWorld.ReadRegion(world, "r0");
        Assert.Equal(4, read.Entrenchment);
        Assert.Equal(3, read.Tier);
    }

    [Fact]
    public void CapturingARegion_DoesNotDisturbTheRestOfTheWorld()
    {
        var world = TestWorld.Blob(TestWorld.Region("r0"), TestWorld.Region("r1", q: 1));
        var untouchedBefore = WorldRegionBlob.FindRegion(world, "r1")!.ToJsonString();

        WorldRegionBlob.CaptureRegion(WorldRegionBlob.FindRegion(world, "r0")!, TestIds.Player, "Mike");

        Assert.Equal(untouchedBefore, WorldRegionBlob.FindRegion(world, "r1")!.ToJsonString());
    }

    [Fact]
    public void CapturingARegion_KeepsItsSitesWhereTheyAre()
    {
        // Ownership is a region-level fact. If a capture disturbed the seed, every site id in the
        // region would start meaning a different place and existing overrides would attach to the
        // wrong ones.
        var region = TestWorld.Region("r0");
        var world = TestWorld.Blob(region);
        var before = TestWorld.SitesIn(region).Select(s => s.SiteId + s.Cell).ToList();

        WorldRegionBlob.CaptureRegion(WorldRegionBlob.FindRegion(world, "r0")!, TestIds.Player, "Mike");

        var after = TestWorld.SitesIn(TestWorld.ReadRegion(world, "r0")).Select(s => s.SiteId + s.Cell).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public void BuildingAWorld_WritesTheSeedItWasBuiltFrom()
    {
        var world = TestWorld.Blob(TestWorld.Region());

        Assert.Equal(TestWorld.RegionSeed, world["WorldSeed"]!.GetValue<int>());
    }

    // -----------------------------------------------------------------
    // Reading what stands in a region — what a raid is measured against
    // -----------------------------------------------------------------

    [Fact]
    public void ARegionsGarrisonIsTheSumOfWhatStandsAtItsSites()
    {
        // A region's defence is spread across its sites, so the server has to add them up the same
        // way the client's dossier does. Reading one site's snapshot would let an attacker be
        // measured against the weakest door.
        var region = TestWorld.Region();
        var world = TestWorld.Blob(region);
        var regionNode = WorldRegionBlob.FindRegion(world, region.RegionId)!;
        var sites = TestWorld.SitesIn(region);

        foreach (var (site, power) in sites.Take(3).Zip(new[] { 1000f, 250f, 75f }))
        {
            var entry = WorldRegionBlob.EnsureOverride(regionNode, site.SiteId);
            entry["GarrisonPower"] = power;
            entry["GarrisonCharacterIds"] = new JsonArray("a", "b");
        }

        var read = TestWorld.ReadRegion(TestWorld.RoundTrip(world), region.RegionId);

        Assert.Equal(1325f, RegionHoldCalculator.GarrisonPowerOf(read));
        Assert.Equal(6, RegionHoldCalculator.GarrisonCountOf(read));
    }

    [Fact]
    public void ARegionWithNothingStationedInItReadsAsEmpty()
    {
        var read = TestWorld.ReadRegion(TestWorld.RoundTrip(TestWorld.Blob(TestWorld.Region())), "r0");

        Assert.Empty(read.SiteOverrides);
        Assert.Equal(0, RegionHoldCalculator.GarrisonPowerOf(read));
        Assert.Equal(0, RegionHoldCalculator.GarrisonCountOf(read));
    }

    [Fact]
    public void AClearedSiteSurvivesBeingReadIntoTheModel()
    {
        var region = TestWorld.Region();
        var world = TestWorld.Blob(region);
        var siteId = TestWorld.DungeonIn(region);
        WorldRegionBlob.MarkCleared(WorldRegionBlob.FindRegion(world, region.RegionId)!, siteId);

        var read = TestWorld.ReadRegion(TestWorld.RoundTrip(world), region.RegionId);

        Assert.True(read.SiteOverrides[siteId].Cleared);
    }

    // -----------------------------------------------------------------
    // Resolve
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(50, 50)]
    [InlineData(-20, 0)]     // raided past breaking
    [InlineData(140, 100)]   // steadied past full
    public void ResolveIsKeptInsideItsBounds(int written, int expected)
    {
        var world = TestWorld.Blob(TestWorld.Region("r0"));
        var regionNode = WorldRegionBlob.FindRegion(world, "r0")!;

        Assert.Equal(expected, WorldRegionBlob.SetResolve(regionNode, written));
        Assert.Equal(expected, TestWorld.ReadRegion(TestWorld.RoundTrip(world), "r0").Resolve);
    }

    // -----------------------------------------------------------------
    // Champions already committed elsewhere
    // -----------------------------------------------------------------

    [Fact]
    public void ChampionsStandingGarrisonInYourOwnRegionsAreCommitted()
    {
        var mine = TestWorld.OwnedBy(TestIds.Player, "r0");
        var world = TestWorld.Blob(mine);
        var entry = WorldRegionBlob.EnsureOverride(
            WorldRegionBlob.FindRegion(world, "r0")!, TestWorld.KeepIn(mine));
        entry["GarrisonCharacterIds"] = new JsonArray("hero-1", "hero-2");

        var committed = WorldRegionBlob.CollectCommittedCharacterIds(world, TestIds.Player);

        Assert.Equal(new[] { "hero-1", "hero-2" }, committed.OrderBy(id => id));
    }

    [Fact]
    public void AnotherPlayersGarrisonDoesNotCommitYourChampions()
    {
        // Their keep holds their people. Otherwise a defender could freeze an attacker's roster by
        // garrisoning characters with matching ids.
        var theirs = TestWorld.OwnedBy(TestIds.Rival, "r1");
        var world = TestWorld.Blob(TestWorld.OwnedBy(TestIds.Player, "r0"), theirs);
        var entry = WorldRegionBlob.EnsureOverride(
            WorldRegionBlob.FindRegion(world, "r1")!, TestWorld.KeepIn(theirs));
        entry["GarrisonCharacterIds"] = new JsonArray("hero-1");

        Assert.Empty(WorldRegionBlob.CollectCommittedCharacterIds(world, TestIds.Player));
    }

    [Fact]
    public void PrisonersAreCommittedWhereverTheyAreHeld()
    {
        // A captured champion is out of the war while they are held — whoever happens to hold the
        // ground now. The stamp is what makes them held: captivity expires (CaptivityRules), and an
        // unstamped capture reads as already home.
        var theirs = TestWorld.OwnedBy(TestIds.Rival, "r1");
        var world = TestWorld.Blob(theirs);
        var entry = WorldRegionBlob.EnsureOverride(
            WorldRegionBlob.FindRegion(world, "r1")!, TestWorld.KeepIn(theirs));
        entry["CapturedCharacterIds"] = new JsonArray("hero-1");
        entry["CapturedAtUtcTicks"] = DateTime.UtcNow.Ticks;

        Assert.Contains("hero-1", WorldRegionBlob.CollectCommittedCharacterIds(world, TestIds.Player));
    }

    [Fact]
    public void APrisonerWhoseTimeIsUpIsNotCommitted()
    {
        // Reading the list rather than asking the rule would lock a freed character out of their own
        // party for ever, because the ids outlive the captivity.
        var theirs = TestWorld.OwnedBy(TestIds.Rival, "r1");
        var world = TestWorld.Blob(theirs);
        var entry = WorldRegionBlob.EnsureOverride(
            WorldRegionBlob.FindRegion(world, "r1")!, TestWorld.KeepIn(theirs));
        entry["CapturedCharacterIds"] = new JsonArray("hero-1");
        entry["CapturedAtUtcTicks"] = DateTime.UtcNow
            .AddHours(-MuggaLuggaTD.Shared.Gameplay.CaptivityRules.PrisonerReturnHours - 1).Ticks;

        Assert.Empty(WorldRegionBlob.CollectCommittedCharacterIds(world, TestIds.Player));
    }

    [Fact]
    public void AFreePlayerHasNobodyCommitted()
    {
        var world = TestWorld.Blob(TestWorld.OwnedBy(TestIds.Player, "r0"), TestWorld.Region("r1", q: 1));

        Assert.Empty(WorldRegionBlob.CollectCommittedCharacterIds(world, TestIds.Player));
    }
}
