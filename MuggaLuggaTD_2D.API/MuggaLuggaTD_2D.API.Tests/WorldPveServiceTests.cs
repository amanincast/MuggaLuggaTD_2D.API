using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// PvE conquest, through <see cref="WorldPveService.BeginAsync"/> and
/// <see cref="WorldPveService.ClaimAsync"/> — the two doors a client can actually reach.
///
/// <para>Tested through those rather than through the private target check on purpose. Both of the
/// bugs this service has had were in what the rule <i>should</i> be, not in whether the code matched
/// itself, and a test poking the private method would have agreed with the bug. A test that says
/// "a player can fight the dungeons in their own region" is a test that would have caught one.</para>
///
/// <para>The server cannot referee the fight — it does not simulate real-time combat. What it owns is
/// everything around it: eligibility, proof that a run was opened, one claim per run, re-validation
/// against the live world, and the payout. That is what is pinned here.</para>
/// </summary>
public class WorldPveServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeGameContent _content = new();

    private WorldPveService Service => new(_db, _content, NullLogger<WorldPveService>.Instance);

    private static string Contract => SharedContract.Version;

    public void Dispose() => _db.Dispose();

    // -----------------------------------------------------------------
    // Beginning a run
    // -----------------------------------------------------------------

    [Fact]
    public async Task AClientRunningDifferentRulesIsTurnedAway()
    {
        // The two sides must agree about what a site is before either can talk about clearing one.
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);

        var (outcome, runId) = await Service.BeginAsync(
            instanceId, TestIds.Player, new PveBeginRequest(TestWorld.DungeonIn(region), "0.0.1"));

        Assert.Equal(PveError.ContractMismatch, outcome.Error);
        Assert.Equal(Guid.Empty, runId);
        Assert.Empty(await _db.PveRuns.ToListAsync());
    }

    [Fact]
    public async Task ARunAgainstAWorldThatDoesNotExistIsRefused()
    {
        var instance = await _db.AddInstanceAsync();

        var (outcome, _) = await Service.BeginAsync(
            instance.Id, TestIds.Player, new PveBeginRequest("r0:1", Contract));

        Assert.Equal(PveError.WorldNotFound, outcome.Error);
    }

    [Fact]
    public async Task ASiteTheWorldDoesNotContainIsRefusedAtTheDoor()
    {
        // The client names a site; the server regenerates the region and looks rather than believing
        // it. An invented site never gets as far as opening a run.
        var instanceId = await SeedWorldAsync(TestWorld.Region());

        var (outcome, _) = await Service.BeginAsync(
            instanceId, TestIds.Player, new PveBeginRequest("r0:999", Contract));

        Assert.Equal(PveError.LocationNotFound, outcome.Error);
        Assert.Empty(await _db.PveRuns.ToListAsync());
    }

    [Fact]
    public async Task ADungeonInUnclaimedLandCanBeEntered()
    {
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var siteId = TestWorld.DungeonIn(region);

        var (outcome, runId) = await Service.BeginAsync(
            instanceId, TestIds.Player, new PveBeginRequest(siteId, Contract));

        Assert.True(outcome.Succeeded);
        Assert.NotEqual(Guid.Empty, runId);

        var run = await _db.PveRuns.SingleAsync();
        Assert.Equal(siteId, run.LocationId);
        Assert.Equal((int)LocationType.Dungeon, run.LocationType);
        Assert.Equal(TestIds.Player, run.UserId);
        Assert.Null(run.ClaimedAt);
    }

    [Fact]
    public async Task APlayerCanFightTheDungeonsInTheirOwnRegion()
    {
        // Holding a region does not empty it. Clearing its dungeons is how resolve is restored, so
        // refusing every site in an owned region shut off a loop the design leans on — a player who
        // took a region could never fight in it again.
        var region = TestWorld.OwnedBy(TestIds.Player);
        var instanceId = await SeedWorldAsync(region);

        var (outcome, _) = await Service.BeginAsync(
            instanceId, TestIds.Player, new PveBeginRequest(TestWorld.DungeonIn(region), Contract));

        Assert.True(outcome.Succeeded, outcome.Message);
    }

    [Fact]
    public async Task APlayerCannotFightTheKeepTheyAlreadyHold()
    {
        // The keep and the settlements come with the ground. Only the hostile sites are still a fight.
        var region = TestWorld.OwnedBy(TestIds.Player);
        var instanceId = await SeedWorldAsync(region);

        var (outcome, _) = await Service.BeginAsync(
            instanceId, TestIds.Player, new PveBeginRequest(TestWorld.KeepIn(region), Contract));

        Assert.Equal(PveError.NotPveTarget, outcome.Error);
    }

    [Fact]
    public async Task ARivalsRegionIsNotAPveTarget()
    {
        // Handing over another player's territory for beating some monsters would bypass the siege
        // entirely — and PvE is the one result the server cannot check.
        var region = TestWorld.OwnedBy(TestIds.Rival);
        var instanceId = await SeedWorldAsync(region);

        var (outcome, _) = await Service.BeginAsync(
            instanceId, TestIds.Player, new PveBeginRequest(TestWorld.DungeonIn(region), Contract));

        Assert.Equal(PveError.NotPveTarget, outcome.Error);
        Assert.Contains("siege", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EvenTheKeepOfARivalsRegionIsNotAPveTarget()
    {
        var region = TestWorld.OwnedBy(TestIds.Rival);
        var instanceId = await SeedWorldAsync(region);

        var (outcome, _) = await Service.BeginAsync(
            instanceId, TestIds.Player, new PveBeginRequest(TestWorld.KeepIn(region), Contract));

        Assert.Equal(PveError.NotPveTarget, outcome.Error);
    }

    [Fact]
    public async Task TheKeepOfAnUnclaimedRegionCanBeTaken()
    {
        // A neutral region's keep is enemy-held, not empty: taking it is how unclaimed land is won.
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);

        var (outcome, _) = await Service.BeginAsync(
            instanceId, TestIds.Player, new PveBeginRequest(TestWorld.KeepIn(region), Contract));

        Assert.True(outcome.Succeeded, outcome.Message);
    }

    [Fact]
    public async Task ASiteWithNoCombatToOfferIsRefused()
    {
        // Settlements and resource nodes are not fights, so there is nothing to claim at one.
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);

        foreach (var type in new[] { LocationType.NeutralHome, LocationType.ResourceNode })
        {
            var (outcome, _) = await Service.BeginAsync(
                instanceId, TestIds.Player, new PveBeginRequest(TestWorld.SiteOfType(region, type).SiteId, Contract));

            Assert.Equal(PveError.NotPveTarget, outcome.Error);
        }
    }

    [Fact]
    public async Task ADungeonThatHasAlreadyBeenClearedIsRefused()
    {
        // A cleared site still generates from the seed, so without this it could be farmed forever.
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var siteId = TestWorld.DungeonIn(region);
        await EditStoredWorldAsync(instanceId, world =>
            WorldRegionBlob.MarkCleared(WorldRegionBlob.FindRegion(world, region.RegionId)!, siteId));

        var (outcome, _) = await Service.BeginAsync(
            instanceId, TestIds.Player, new PveBeginRequest(siteId, Contract));

        Assert.Equal(PveError.NotPveTarget, outcome.Error);
        Assert.Contains("cleared", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReEnteringASiteReplacesTheOpenRunRatherThanStackingUp()
    {
        // Otherwise a player could walk in and out of a dungeon to bank a pile of claimable runs and
        // spend them all on one clear.
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var request = new PveBeginRequest(TestWorld.DungeonIn(region), Contract);

        var (_, first) = await Service.BeginAsync(instanceId, TestIds.Player, request);
        var (_, second) = await Service.BeginAsync(instanceId, TestIds.Player, request);
        var (_, third) = await Service.BeginAsync(instanceId, TestIds.Player, request);

        var open = await _db.PveRuns.Where(r => r.ClaimedAt == null).ToListAsync();
        Assert.Equal(third, Assert.Single(open).Id);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task TwoPlayersCanHaveTheirOwnRunAtTheSameSite()
    {
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var request = new PveBeginRequest(TestWorld.DungeonIn(region), Contract);

        await Service.BeginAsync(instanceId, TestIds.Player, request);
        await Service.BeginAsync(instanceId, TestIds.Rival, request);

        Assert.Equal(2, await _db.PveRuns.CountAsync(r => r.ClaimedAt == null));
    }

    // -----------------------------------------------------------------
    // Claiming a run
    // -----------------------------------------------------------------

    [Fact]
    public async Task AClaimForARunThatWasNeverOpenedIsRefused()
    {
        // This is the whole point of runs: a conquest with no attempt behind it cannot be applied.
        var instanceId = await SeedWorldAsync(TestWorld.Region());

        var (outcome, response, world) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(Guid.NewGuid(), Contract));

        Assert.Equal(PveError.RunNotFound, outcome.Error);
        Assert.Null(response);
        Assert.Null(world);
    }

    [Fact]
    public async Task APlayerCannotClaimSomebodyElsesRun()
    {
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var runId = await OpenRunAsync(instanceId, TestIds.Rival, TestWorld.DungeonIn(region));

        var (outcome, _, _) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.Equal(PveError.RunNotFound, outcome.Error);
    }

    [Fact]
    public async Task ARunCannotBeSpentInADifferentWorld()
    {
        var region = TestWorld.Region();
        var here = await SeedWorldAsync(region);
        var elsewhere = await SeedWorldAsync(region, ownerId: "user-other-owner");
        var runId = await OpenRunAsync(here, TestIds.Player, TestWorld.DungeonIn(region));

        var (outcome, _, _) = await Service.ClaimAsync(
            elsewhere, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.Equal(PveError.RunNotFound, outcome.Error);
    }

    [Fact]
    public async Task AClearClaimedTooQuicklyToHaveBeenFoughtIsRefused()
    {
        // A floor against a scripted claim fired the instant the run opens, not a balance knob.
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, TestWorld.DungeonIn(region));

        var (outcome, _, world) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.Equal(PveError.RunTooFast, outcome.Error);
        Assert.Null(world);
        Assert.Null((await _db.PveRuns.SingleAsync()).ClaimedAt);
    }

    [Fact]
    public async Task ARunLeftOpenForHoursCannotBeClaimed()
    {
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, TestWorld.DungeonIn(region));
        await AgeRunAsync(runId, WorldPveService.RunExpiry + TimeSpan.FromMinutes(1));

        var (outcome, _, _) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.Equal(PveError.RunNotFound, outcome.Error);
    }

    [Fact]
    public async Task ClearingADungeonMarksItSpentAndPaysForTheClear()
    {
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var site = TestWorld.SiteOfType(region, LocationType.Dungeon);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, site.SiteId);
        await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

        var (outcome, response, world) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(nameof(ConquestOutcome.RemoveLocation), response!.ConquestOutcome);
        Assert.Equal(site.SiteId, response.SiteId);
        Assert.True(TestWorld.IsCleared(world, site.SiteId));
        Assert.True(response.Experience > 0);
        Assert.NotNull((await _db.PveRuns.SingleAsync()).ClaimedAt);
    }

    [Fact]
    public async Task ThePayoutIsPricedFromTheSite_NotFromAnythingTheClientSays()
    {
        // The claim carries no XP and no loot — only a run id. The budget comes from the site's own
        // level and tier through the shared calculator, which is what stops a self-reported total
        // inflating the roster the server later computes PvP power from.
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var site = TestWorld.SiteOfType(region, LocationType.Dungeon);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, site.SiteId);
        await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

        var (_, response, _) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        var expected = RunRewardCalculator.Calculate(
            site.Level, site.Tier, _content.RunTuning, _content.DroppableItems, new Random(1));

        Assert.Equal(expected.Experience, response!.Experience);
    }

    [Fact]
    public async Task ARunCanOnlyBeClaimedOnce()
    {
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, TestWorld.DungeonIn(region));
        await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

        var (first, _, _) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));
        var (second, response, world) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.True(first.Succeeded);
        Assert.Equal(PveError.RunAlreadyClaimed, second.Error);
        Assert.Null(response);
        Assert.Null(world);
    }

    [Fact]
    public async Task TakingTheKeepTakesTheRegion()
    {
        // Regions are what a player owns; the keep is how one changes hands.
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, TestWorld.KeepIn(region));
        await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

        var (outcome, response, world) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(nameof(ConquestOutcome.CaptureForPlayer), response!.ConquestOutcome);

        var captured = TestWorld.ReadRegion(world, region.RegionId);
        Assert.Equal(LocationOwnership.Player, captured.Ownership);
        Assert.Equal(TestIds.Player, captured.OwnerUserId);
        Assert.Equal("Mike", captured.OwnerDisplayName);
    }

    [Fact]
    public async Task ARivalTakingTheRegionMidRunVoidsTheClaim()
    {
        // The claim is judged against the world as it is now, not as it was when the run opened.
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, TestWorld.DungeonIn(region));
        await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

        await EditStoredWorldAsync(instanceId, world =>
            WorldRegionBlob.CaptureRegion(WorldRegionBlob.FindRegion(world, region.RegionId)!, TestIds.Rival, "Rival"));

        var (outcome, _, _) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.Equal(PveError.NotPveTarget, outcome.Error);
    }

    [Fact]
    public async Task ASiteSomebodyElseClearedFirstCannotBeClaimedAgain()
    {
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var siteId = TestWorld.DungeonIn(region);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, siteId);
        await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

        await EditStoredWorldAsync(instanceId, world =>
            WorldRegionBlob.MarkCleared(WorldRegionBlob.FindRegion(world, region.RegionId)!, siteId));

        var (outcome, _, _) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.Equal(PveError.NotPveTarget, outcome.Error);
    }

    [Fact]
    public async Task ARunHeldOpenAcrossAWorldRegenerationIsClosedRatherThanLeftSpendable()
    {
        // If the site id ever came back — a new world, same ids — an unclosed run would be a stored
        // conquest waiting to be spent on whatever now stands there.
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, TestWorld.DungeonIn(region));
        await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

        // The world is replaced by one with different regions entirely.
        await ReplaceStoredWorldAsync(instanceId, TestWorld.Blob(TestWorld.Region("r-elsewhere", q: 9)));

        var (outcome, _, _) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.Equal(PveError.LocationNotFound, outcome.Error);
        Assert.NotNull((await _db.PveRuns.SingleAsync()).ClaimedAt);
    }

    [Fact]
    public async Task AClaimUnderDifferentRulesIsTurnedAway()
    {
        var region = TestWorld.Region();
        var instanceId = await SeedWorldAsync(region);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, TestWorld.DungeonIn(region));
        await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

        var (outcome, _, world) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, "0.0.1"));

        Assert.Equal(PveError.ContractMismatch, outcome.Error);
        Assert.Null(world);
        Assert.Null((await _db.PveRuns.SingleAsync()).ClaimedAt);
    }

    [Fact]
    public async Task ClearingADungeonInYourOwnRegionWorksEndToEnd()
    {
        // The loop the own-region fix exists for: hold the ground, still fight what is under it.
        var region = TestWorld.OwnedBy(TestIds.Player);
        var instanceId = await SeedWorldAsync(region);
        var siteId = TestWorld.DungeonIn(region);

        var (beginOutcome, runId) = await Service.BeginAsync(
            instanceId, TestIds.Player, new PveBeginRequest(siteId, Contract));
        Assert.True(beginOutcome.Succeeded, beginOutcome.Message);

        await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

        var (claimOutcome, response, world) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.True(claimOutcome.Succeeded, claimOutcome.Message);
        Assert.True(TestWorld.IsCleared(world, siteId));
        Assert.True(response!.Experience > 0);

        // And the region is still theirs afterwards.
        Assert.Equal(TestIds.Player, TestWorld.ReadRegion(world, region.RegionId).OwnerUserId);
    }

    // -----------------------------------------------------------------
    // Resolve — the defender's answer to being raided
    // -----------------------------------------------------------------

    [Fact]
    public async Task ClearingAHostileSiteInYourOwnRegionSteadiesIt()
    {
        // The other half of raiding. A rival wears a region's resolve down from outside; the owner
        // answers by going in and dealing with what is under it. Without this, being raided has no
        // reply at all.
        var region = TestWorld.OwnedBy(TestIds.Player);
        region.Resolve = 60;
        var instanceId = await SeedWorldAsync(region);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, TestWorld.DungeonIn(region));
        await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

        var (outcome, response, world) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(RegionResolveRules.RestoredPerClear, response!.ResolveRestored);
        Assert.Equal(60 + RegionResolveRules.RestoredPerClear, TestWorld.ReadRegion(world, region.RegionId).Resolve);
    }

    [Fact]
    public async Task ResolveCannotBeRestoredPastFull()
    {
        var region = TestWorld.OwnedBy(TestIds.Player);
        region.Resolve = RegionResolveRules.Maximum - 3;
        var instanceId = await SeedWorldAsync(region);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, TestWorld.DungeonIn(region));
        await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

        var (_, response, world) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.Equal(RegionResolveRules.Maximum, TestWorld.ReadRegion(world, region.RegionId).Resolve);

        // And it reports what was actually restored, not what it was worth.
        Assert.Equal(3, response!.ResolveRestored);
    }

    [Fact]
    public async Task ClearingUnclaimedLandSteadiesNothing()
    {
        // Resolve is the morale of a region you hold. Clearing a dungeon in neutral country is a
        // fight, not a show of force that steadies anyone's territory.
        var region = TestWorld.Region();
        region.Resolve = 50;
        var instanceId = await SeedWorldAsync(region);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, TestWorld.DungeonIn(region));
        await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

        var (_, response, world) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.Equal(0, response!.ResolveRestored);
        Assert.Equal(50, TestWorld.ReadRegion(world, region.RegionId).Resolve);
    }

    [Fact]
    public async Task TakingAKeepDoesNotAlsoCountAsSteadyingTheRegion()
    {
        // A capture already sets resolve to full. Adding a restoration on top would be counting the
        // same act twice, and the capture is the thing that decides the number.
        var region = TestWorld.Region();
        region.Resolve = 40;
        var instanceId = await SeedWorldAsync(region);
        var runId = await OpenRunAsync(instanceId, TestIds.Player, TestWorld.KeepIn(region));
        await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

        var (_, response, world) = await Service.ClaimAsync(
            instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));

        Assert.Equal(0, response!.ResolveRestored);
        Assert.Equal(RegionResolveRules.Maximum, TestWorld.ReadRegion(world, region.RegionId).Resolve);
    }

    [Fact]
    public async Task AWornDownRegionCanBeBroughtBackByClearingItsSites()
    {
        // End to end: a region raided down to nothing is recoverable by its owner. That it is
        // recoverable *only while its sites last* is the known tension recorded on RegionResolveRules.
        var region = TestWorld.OwnedBy(TestIds.Player);
        region.Resolve = 20;
        var instanceId = await SeedWorldAsync(region);

        var dungeons = TestWorld.SitesIn(region)
            .Where(s => s.Type == LocationType.Dungeon).Take(3).ToList();

        int expected = 20;
        foreach (var dungeon in dungeons)
        {
            var runId = await OpenRunAsync(instanceId, TestIds.Player, dungeon.SiteId);
            await AgeRunAsync(runId, TimeSpan.FromMinutes(2));

            var (outcome, _, world) = await Service.ClaimAsync(
                instanceId, TestIds.Player, "Mike", new PveClaimRequest(runId, Contract));
            Assert.True(outcome.Succeeded, outcome.Message);

            expected += RegionResolveRules.RestoredPerClear;

            // The claim does not persist; the controller does. Write it back so the next clear sees it.
            await ReplaceStoredWorldAsync(instanceId, world!);
            Assert.Equal(expected, TestWorld.ReadRegion(world, region.RegionId).Resolve);
        }

        Assert.Equal(50, expected);
    }

    // -----------------------------------------------------------------
    // Setup helpers
    // -----------------------------------------------------------------

    private async Task<Guid> SeedWorldAsync(params WorldRegionData[] regions)
        => await SeedWorldAsync(regions, TestIds.Owner);

    private async Task<Guid> SeedWorldAsync(WorldRegionData[] regions, string ownerId)
    {
        var instance = await _db.AddInstanceAsync(ownerId, ownerId);
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(regions));
        return instance.Id;
    }

    private async Task<Guid> SeedWorldAsync(WorldRegionData region, string ownerId)
        => await SeedWorldAsync(new[] { region }, ownerId);

    /// <summary>Opens a run the way the service does, so a claim has something legitimate to find.</summary>
    private async Task<Guid> OpenRunAsync(Guid instanceId, string userId, string siteId)
    {
        var (outcome, runId) = await Service.BeginAsync(instanceId, userId, new PveBeginRequest(siteId, Contract));
        Assert.True(outcome.Succeeded, outcome.Message);
        return runId;
    }

    /// <summary>
    /// Backdates a run so a test can reach the claim path without waiting out
    /// <see cref="WorldPveService.MinimumRunDuration"/> in real time.
    /// </summary>
    private async Task AgeRunAsync(Guid runId, TimeSpan age)
    {
        var run = await _db.PveRuns.SingleAsync(r => r.Id == runId);
        run.StartedAt = DateTime.UtcNow - age;
        await _db.SaveChangesAsync();
    }

    /// <summary>Changes the stored world, as another player's action would.</summary>
    private async Task EditStoredWorldAsync(Guid instanceId, Action<JsonNode> edit)
    {
        var row = await _db.WorldViewGameData.SingleAsync(w => w.GameInstanceId == instanceId);
        var world = JsonNode.Parse(row.GameData)!;
        edit(world);
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();
    }

    private async Task ReplaceStoredWorldAsync(Guid instanceId, JsonNode world)
    {
        var row = await _db.WorldViewGameData.SingleAsync(w => w.GameInstanceId == instanceId);
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();
    }
}
