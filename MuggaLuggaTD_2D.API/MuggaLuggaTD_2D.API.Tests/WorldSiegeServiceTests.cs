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
/// Declaring a siege and moving it through its windows (<c>docs/design/siege.md</c> §4, §7a).
///
/// <para>Most of what is pinned here is about <i>limits</i>, because a siege is the one action that
/// can take a region from another player: it must be gated behind attrition, it must not land on
/// ground that has only just changed hands, it must not run into the season's last day, and it must
/// cost the attacker their army for as long as it lasts. The windows are driven by a clock the test
/// owns, since a real siege runs for most of a day.</para>
/// </summary>
public class WorldSiegeServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeGameContent _content = new();
    private readonly FakeHubContext _hub = new();
    private readonly FakeSessionLog _log = new();
    private readonly FakeClock _clock = new();

    private WorldSiegeService Service => new(
        _db, _content, _hub, _log, NullLogger<WorldSiegeService>.Instance, _clock,
        new SeasonScoreService(
            _db,
            new GoldService(_db, _log, NullLogger<GoldService>.Instance),
            new WorldProvisioningService(_db, NullLogger<WorldProvisioningService>.Instance),
            _hub,
            _log,
            NullLogger<SeasonScoreService>.Instance),
        WarLog);

    private WarLogService WarLog => new(_db, _hub, NullLogger<WarLogService>.Instance, _clock);

    private WorldRaidService Raids => new(_db, _content, NullLogger<WorldRaidService>.Instance);

    private const string Target = "r1";

    public void Dispose() => _db.Dispose();

    // -----------------------------------------------------------------
    // Declaring
    // -----------------------------------------------------------------

    [Fact]
    public async Task AWornDownRivalRegionCanBeBesieged()
    {
        var instanceId = await SeedAsync();

        var outcome = await Service.DeclareAsync(instanceId, TestIds.Player, Request());

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(nameof(SiegeState.Mustering), outcome.Siege!.State);
        Assert.Equal(_clock.UtcNow + SiegeRules.Muster, outcome.Siege.MusterEndsAt);
        Assert.Equal(TestIds.Rival, outcome.Siege.DefenderUserId);
        Assert.Contains("SiegeUpdated", _hub.MethodsSentTo(instanceId));
    }

    [Fact]
    public async Task ARegionAtFullMoraleMustBeRaidedFirst()
    {
        // Attrition first: the decisive action is gated behind the cheap, rate-limited one.
        var instanceId = await SeedAsync(resolve: 100);

        var outcome = await Service.DeclareAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(SiegeError.Refused, outcome.Error);
        Assert.Equal(SiegeRefusal.ResolveTooHigh, outcome.Refusal);
    }

    [Fact]
    public async Task APlayersSeatCannotBeBesieged()
    {
        var instanceId = await SeedAsync(capital: true);

        var outcome = await Service.DeclareAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(SiegeRefusal.Capital, outcome.Refusal);
    }

    [Fact]
    public async Task AnArmyShortOfTheGateIsTurnedAway()
    {
        var instanceId = await SeedAsync(garrisonPower: 50_000f);

        var outcome = await Service.DeclareAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(SiegeRefusal.BelowGate, outcome.Refusal);
        Assert.Contains("gate", outcome.Message);
        Assert.Empty(await _db.Sieges.ToListAsync());
    }

    [Fact]
    public async Task AnArmyOfChampionsYouDoNotOwnIsNoArmy()
    {
        var instanceId = await SeedAsync();

        var outcome = await Service.DeclareAsync(instanceId, TestIds.Player, Request(army: new[] { "someone-elses-hero" }));

        Assert.Equal(SiegeError.NoArmy, outcome.Error);
    }

    [Fact]
    public async Task AClientRunningDifferentRulesIsTurnedAway()
    {
        var instanceId = await SeedAsync();

        var outcome = await Service.DeclareAsync(instanceId, TestIds.Player, Request(version: "0.0.1"));

        Assert.Equal(SiegeError.ContractMismatch, outcome.Error);
    }

    // -----------------------------------------------------------------
    // The truce
    // -----------------------------------------------------------------

    [Fact]
    public async Task ARegionThatHasJustChangedHandsCannotBeBesieged()
    {
        // The rule that stops the same region ping-ponging between two players.
        var instanceId = await SeedAsync(claimedAgo: TimeSpan.FromHours(2));

        var outcome = await Service.DeclareAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(SiegeRefusal.UnderTruce, outcome.Refusal);
    }

    [Fact]
    public async Task TheTruceLiftsAfterADay()
    {
        var instanceId = await SeedAsync(claimedAgo: TimeSpan.FromHours(SiegeRules.TruceHours + 1));

        var outcome = await Service.DeclareAsync(instanceId, TestIds.Player, Request());

        Assert.True(outcome.Succeeded, outcome.Message);
    }

    [Fact]
    public async Task ARegionThatHasJustChangedHandsCannotBeRaidedEither()
    {
        // The truce is about the region, not about one kind of attack. A raid on fresh ground would
        // start wearing it down before its new owner had stationed anyone.
        var instanceId = await SeedAsync(resolve: 100, claimedAgo: TimeSpan.FromHours(2), clockIsReal: true);

        var (outcome, world) = await Raids.RaidAsync(instanceId, TestIds.Player,
            new RegionRaidRequest(Target, new List<string> { "hero-1" }, SharedContract.Version));

        Assert.Equal(RaidError.UnderTruce, outcome.Error);
        Assert.Null(world);
    }

    [Fact]
    public void CapturingARegionStartsItsTruce()
    {
        var world = TestWorld.Blob(TestWorld.Region(Target));
        var node = WorldRegionBlob.FindRegion(world, Target)!;
        var at = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

        WorldRegionBlob.CaptureRegion(node, TestIds.Player, "Player", at);

        var region = WorldRegionBlob.ReadRegion(node);
        Assert.True(SiegeRules.IsUnderTruce(region, at.AddHours(1)));
        Assert.False(SiegeRules.IsUnderTruce(region, at.AddHours(SiegeRules.TruceHours)));
    }

    // -----------------------------------------------------------------
    // The season's last day
    // -----------------------------------------------------------------

    [Fact]
    public async Task NoSiegeMayBeDeclaredOnTheSeasonsLastDay()
    {
        var instanceId = await SeedAsync(seasonEndsIn: TimeSpan.FromHours(20));

        var outcome = await Service.DeclareAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(SiegeRefusal.SeasonClosing, outcome.Refusal);
    }

    [Fact]
    public async Task ASiegeDeclaredJustBeforeTheCutoffStillFinishesBeforeTheBell()
    {
        // The cutoff is sized so no siege is ever left half-fought when the realm resets.
        var instanceId = await SeedAsync(seasonEndsIn: TimeSpan.FromHours(SiegeRules.SeasonCutoffHours + 1));

        var outcome = await Service.DeclareAsync(instanceId, TestIds.Player, Request());

        Assert.True(outcome.Succeeded, outcome.Message);
        var instance = await _db.GameInstances.FirstAsync(g => g.Id == instanceId);
        Assert.True(outcome.Siege!.AssaultEndsAt < instance.SeasonEndsAt);
    }

    // -----------------------------------------------------------------
    // One siege per region, one per attacker
    // -----------------------------------------------------------------

    [Fact]
    public async Task ARegionAlreadyUnderSiegeCannotBeBesiegedAgain()
    {
        var instanceId = await SeedAsync(secondAttacker: TestIds.Owner);
        await DeclareAsync(instanceId);

        var outcome = await Service.DeclareAsync(instanceId, TestIds.Owner, Request());

        Assert.Equal(SiegeError.RegionAlreadyBesieged, outcome.Error);
    }

    [Fact]
    public async Task AnAttackerLaysOneSiegeAtATime()
    {
        var other = TestWorld.OwnedBy(TestIds.Rival, "r2");
        other.Resolve = 40;
        var instanceId = await SeedAsync(extra: other, roster: new[] { "hero-1", "hero-2" });
        await DeclareAsync(instanceId);

        var outcome = await Service.DeclareAsync(instanceId, TestIds.Player, Request("r2", new[] { "hero-2" }));

        Assert.Equal(SiegeError.AlreadyBesieging, outcome.Error);
    }

    // -----------------------------------------------------------------
    // The locked army
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheBesiegingArmyCannotAlsoRaid()
    {
        var other = TestWorld.OwnedBy(TestIds.Rival, "r2");
        var instanceId = await SeedAsync(extra: other, clockIsReal: true);
        await DeclareAsync(instanceId);

        var (outcome, _) = await Raids.RaidAsync(instanceId, TestIds.Player,
            new RegionRaidRequest("r2", new List<string> { "hero-1" }, SharedContract.Version));

        Assert.Equal(RaidError.NoAttackers, outcome.Error);
    }

    [Fact]
    public async Task OnlyTheAttackerIsToldWhoIsInTheArmy()
    {
        var instanceId = await SeedAsync();
        await DeclareAsync(instanceId);

        var asAttacker = await Service.LiveSiegesAsync(instanceId, TestIds.Player);
        var asDefender = await Service.LiveSiegesAsync(instanceId, TestIds.Rival);

        Assert.Equal(new[] { "hero-1" }, asAttacker.Single().ArmyCharacterIds);
        Assert.Empty(asDefender.Single().ArmyCharacterIds);
        Assert.True(asDefender.Single().MarchingPower > 0);
    }

    // -----------------------------------------------------------------
    // The windows
    // -----------------------------------------------------------------

    [Fact]
    public async Task WhenMusterClosesTheRegionsHoldIsFrozen()
    {
        var instanceId = await SeedAsync();
        await DeclareAsync(instanceId);

        _clock.Advance(SiegeRules.Muster);
        await Service.AdvanceAsync(instanceId);

        var siege = await _db.Sieges.SingleAsync();
        Assert.Equal(SiegeState.Assault, siege.State);
        Assert.NotNull(siege.FrozenHold);
        long frozen = siege.FrozenHold!.Value;

        // Reinforcing after the defender's window has shut changes nothing the attacker faces.
        await GarrisonAsync(instanceId, 50_000f);
        _clock.Advance(TimeSpan.FromHours(1));
        await Service.AdvanceAsync(instanceId);

        Assert.Equal(frozen, (await _db.Sieges.SingleAsync()).FrozenHold);
    }

    [Fact]
    public async Task ReinforcingDuringMusterCountsTowardsTheHold()
    {
        // The muster is the defender's window: what they station in it is what the attacker faces.
        var instanceId = await SeedAsync();
        await DeclareAsync(instanceId);
        long undefended = RegionHoldCalculator.AssessRegion(
            await ReadRegionAsync(instanceId), await ReadAllAsync(instanceId), 0).Hold;

        await GarrisonAsync(instanceId, 5_000f);
        _clock.Advance(SiegeRules.Muster);
        await Service.AdvanceAsync(instanceId);

        Assert.True((await _db.Sieges.SingleAsync()).FrozenHold > undefended);
    }

    [Fact]
    public async Task TheDefenderMayCloseTheMusterEarly()
    {
        var instanceId = await SeedAsync();
        var declared = await DeclareAsync(instanceId);

        var outcome = await Service.DeclareReadyAsync(instanceId, TestIds.Rival, declared.Id);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(nameof(SiegeState.Assault), outcome.Siege!.State);
        Assert.Equal(_clock.UtcNow + SiegeRules.AssaultWindow, outcome.Siege.AssaultEndsAt);
        Assert.NotNull(outcome.Siege.FrozenHold);
    }

    [Fact]
    public async Task OnlyTheDefenderMayDeclareReady()
    {
        var instanceId = await SeedAsync();
        var declared = await DeclareAsync(instanceId);

        var outcome = await Service.DeclareReadyAsync(instanceId, TestIds.Player, declared.Id);

        Assert.Equal(SiegeError.NotDefender, outcome.Error);
    }

    [Fact]
    public async Task AnAssaultTheAttackerNeverComesForLapsesAndFreesTheArmy()
    {
        var other = TestWorld.OwnedBy(TestIds.Rival, "r2");
        var instanceId = await SeedAsync(extra: other);
        await DeclareAsync(instanceId);

        _clock.Advance(SiegeRules.Muster + SiegeRules.AssaultWindow);
        await Service.AdvanceAsync(instanceId);

        var siege = await _db.Sieges.SingleAsync();
        Assert.Equal(SiegeState.Lapsed, siege.State);
        Assert.Equal(siege.AssaultEndsAt, siege.ResolvedAt);
        Assert.Empty(await MarchingArmy.SiegeLockedIdsAsync(_db, instanceId, TestIds.Player));
    }

    [Fact]
    public async Task ALapsedSiegeBarsTheAttackerFromThatRegionForADay()
    {
        var instanceId = await SeedAsync();
        await DeclareAsync(instanceId);
        _clock.Advance(SiegeRules.Muster + SiegeRules.AssaultWindow);

        var tooSoon = await Service.DeclareAsync(instanceId, TestIds.Player, Request());
        Assert.Equal(SiegeError.OnCooldown, tooSoon.Error);

        _clock.Advance(SiegeRules.RedeclareCooldown);
        var later = await Service.DeclareAsync(instanceId, TestIds.Player, Request());
        Assert.True(later.Succeeded, later.Message);
    }

    [Fact]
    public async Task ASiegeWhoseRegionChangesHandsIsCancelledAndCostsNothing()
    {
        var instanceId = await SeedAsync();
        await DeclareAsync(instanceId);

        // Somebody else takes the region out from under the siege.
        await ReplaceOwnerAsync(instanceId, TestIds.Owner);
        await Service.AdvanceAsync(instanceId);

        var siege = await _db.Sieges.SingleAsync();
        Assert.Equal(SiegeState.Cancelled, siege.State);
        Assert.True(await Service.RedeclareAllowedAtAsync(instanceId, TestIds.Player, Target) <= _clock.UtcNow);
    }

    [Fact]
    public async Task TheSweepAdvancesEverySiegeThatIsDue()
    {
        var instanceId = await SeedAsync();
        await DeclareAsync(instanceId);
        _clock.Advance(SiegeRules.Muster);

        int advanced = await Service.AdvanceAllDueAsync();

        Assert.Equal(1, advanced);
        Assert.Equal(SiegeState.Assault, (await _db.Sieges.SingleAsync()).State);
    }

    [Fact]
    public async Task NothingIsDueBeforeItsTime()
    {
        var instanceId = await SeedAsync();
        await DeclareAsync(instanceId);
        _clock.Advance(SiegeRules.Muster - TimeSpan.FromMinutes(1));

        Assert.Equal(0, await Service.AdvanceAllDueAsync());
        Assert.Equal(SiegeState.Mustering, (await _db.Sieges.SingleAsync()).State);
    }

    // -----------------------------------------------------------------
    // The assault
    // -----------------------------------------------------------------

    [Fact]
    public async Task NoAssaultWhileTheDefenderIsStillMustering()
    {
        var instanceId = await SeedAsync();
        var declared = await DeclareAsync(instanceId);

        var (outcome, assault) = await Service.BeginAssaultAsync(instanceId, TestIds.Player, declared.Id, SharedContract.Version);

        Assert.Equal(SiegeError.WrongState, outcome.Error);
        Assert.Null(assault);
    }

    [Fact]
    public async Task OnlyTheBesiegerMayAssault()
    {
        var instanceId = await SeedAsync();
        var siegeId = await ReachAssaultAsync(instanceId);

        var (outcome, _) = await Service.BeginAssaultAsync(instanceId, TestIds.Rival, siegeId, SharedContract.Version);

        Assert.Equal(SiegeError.NotAttacker, outcome.Error);
    }

    [Fact]
    public async Task TheServerHandsTheAttackerTheFightFromTheFrozenHold()
    {
        var instanceId = await SeedAsync();
        var siegeId = await ReachAssaultAsync(instanceId);

        var (outcome, assault) = await Service.BeginAssaultAsync(instanceId, TestIds.Player, siegeId, SharedContract.Version);

        Assert.True(outcome.Succeeded, outcome.Message);
        var siege = await _db.Sieges.SingleAsync();
        var expected = SiegeAssaultRules.EncounterFor(siege.MarchingPower, siege.FrozenHold!.Value, 1, 0);
        Assert.Equal(expected.EnemyLevel, assault!.EnemyLevel);
        Assert.Equal(expected.Waves, assault.Waves);
        Assert.Equal(siege.AssaultRunId, assault.RunId);
        Assert.Equal(new[] { "hero-1" }, assault.ArmyCharacterIds);
    }

    [Fact]
    public async Task ASiegeGetsOneAssault()
    {
        // A player who could start a fresh assault after every loss would retry until the fight went their way.
        var instanceId = await SeedAsync();
        var siegeId = await ReachAssaultAsync(instanceId);
        await BeginAsync(instanceId, siegeId);

        var (again, _) = await Service.BeginAssaultAsync(instanceId, TestIds.Player, siegeId, SharedContract.Version);

        Assert.Equal(SiegeError.AssaultSpent, again.Error);
    }

    [Fact]
    public async Task WinningTheAssaultTakesTheRegionWreckedAndItsGarrisonPrisoner()
    {
        var instanceId = await SeedAsync(garrisonIds: new[] { "rival-hero" });
        var siegeId = await ReachAssaultAsync(instanceId);
        var assault = await BeginAsync(instanceId, siegeId);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var (outcome, result) = await Service.ClaimAssaultAsync(instanceId, TestIds.Player, siegeId,
            new SiegeAssaultClaimRequest(assault.RunId, true, SharedContract.Version));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(nameof(SiegeState.Won), result!.Outcome);
        Assert.Equal(1, result.CapturedCount);

        var world = await _db.ReadWorldAsync(instanceId);
        var region = TestWorld.ReadRegion(world, Target);
        Assert.True(region.IsOwnedByPlayer(TestIds.Player));
        Assert.Equal(SiegeAssaultRules.WreckedResolve, region.Resolve);
        Assert.Equal(0, region.Entrenchment);
        Assert.Contains(region.SiteOverrides.Values, o => o.CapturedCharacterIds.Contains("rival-hero"));
        Assert.All(region.SiteOverrides.Values, o => Assert.Empty(o.GarrisonCharacterIds));

        // Fresh ground: the old owner cannot strike straight back.
        Assert.True(SiegeRules.IsUnderTruce(region, _clock.UtcNow));

        Assert.Contains("WorldViewGameDataUpdated", _hub.MethodsSentTo(instanceId));
        Assert.Empty(await MarchingArmy.SiegeLockedIdsAsync(_db, instanceId, TestIds.Player));
    }

    [Fact]
    public async Task ASiegeWonPaysTheAttackerALump()
    {
        var instanceId = await SeedAsync();
        var siegeId = await ReachAssaultAsync(instanceId);
        var assault = await BeginAsync(instanceId, siegeId);
        _clock.Advance(TimeSpan.FromMinutes(5));

        await Service.ClaimAssaultAsync(instanceId, TestIds.Player, siegeId,
            new SiegeAssaultClaimRequest(assault.RunId, true, SharedContract.Version));

        var score = await _db.SeasonScores.AsNoTracking().FirstAsync(s => s.UserId == TestIds.Player);
        Assert.True(score.SettledPoints >= SeasonScoreRules.PointsFor(SeasonDeed.SiegeWon));
    }

    [Fact]
    public async Task LosingTheAssaultRepelsTheSiegeAndStiffensTheRegion()
    {
        var instanceId = await SeedAsync();
        var siegeId = await ReachAssaultAsync(instanceId);
        var assault = await BeginAsync(instanceId, siegeId);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var (outcome, result) = await Service.ClaimAssaultAsync(instanceId, TestIds.Player, siegeId,
            new SiegeAssaultClaimRequest(assault.RunId, false, SharedContract.Version));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(nameof(SiegeState.Repelled), result!.Outcome);

        var region = TestWorld.ReadRegion(await _db.ReadWorldAsync(instanceId), Target);
        Assert.True(region.IsOwnedByPlayer(TestIds.Rival));
        Assert.Equal(40 + SiegeAssaultRules.RepelResolveBonus, region.Resolve);

        var defender = await _db.SeasonScores.AsNoTracking().FirstAsync(s => s.UserId == TestIds.Rival);
        Assert.True(defender.SettledPoints >= SeasonScoreRules.PointsFor(SeasonDeed.SiegeRepelled));
    }

    [Fact]
    public async Task AWinClaimedFasterThanItCouldBeFoughtIsRefused()
    {
        var instanceId = await SeedAsync();
        var siegeId = await ReachAssaultAsync(instanceId);
        var assault = await BeginAsync(instanceId, siegeId);

        var (outcome, _) = await Service.ClaimAssaultAsync(instanceId, TestIds.Player, siegeId,
            new SiegeAssaultClaimRequest(assault.RunId, true, SharedContract.Version));

        Assert.Equal(SiegeError.TooFast, outcome.Error);
        Assert.True(TestWorld.ReadRegion(await _db.ReadWorldAsync(instanceId), Target).IsOwnedByPlayer(TestIds.Rival));
    }

    [Fact]
    public async Task AClaimMustNameThisSiegesAssault()
    {
        var instanceId = await SeedAsync();
        var siegeId = await ReachAssaultAsync(instanceId);
        await BeginAsync(instanceId, siegeId);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var (outcome, _) = await Service.ClaimAssaultAsync(instanceId, TestIds.Player, siegeId,
            new SiegeAssaultClaimRequest(Guid.NewGuid(), true, SharedContract.Version));

        Assert.Equal(SiegeError.RunMismatch, outcome.Error);
    }

    [Fact]
    public async Task AnAssaultNeverReportedIsADefenceThatHeld()
    {
        var instanceId = await SeedAsync();
        var siegeId = await ReachAssaultAsync(instanceId);
        await BeginAsync(instanceId, siegeId);

        _clock.Advance(SiegeRules.AssaultWindow + SiegeAssaultRules.ClaimGrace);
        Assert.Equal(1, await Service.AdvanceAllDueAsync());

        Assert.Equal(SiegeState.Repelled, (await _db.Sieges.SingleAsync()).State);
    }

    [Fact]
    public async Task AStartedAssaultDoesNotLapseAtTheWindowsCloseButMayStillBeClaimed()
    {
        // A fight begun a minute before the window shut must not be forfeited by the clock.
        var instanceId = await SeedAsync();
        var siegeId = await ReachAssaultAsync(instanceId);
        _clock.Advance(SiegeRules.AssaultWindow - TimeSpan.FromMinutes(1));
        var assault = await BeginAsync(instanceId, siegeId);
        _clock.Advance(TimeSpan.FromMinutes(10));

        await Service.AdvanceAsync(instanceId);
        Assert.Equal(SiegeState.Assault, (await _db.Sieges.SingleAsync()).State);

        var (outcome, result) = await Service.ClaimAssaultAsync(instanceId, TestIds.Player, siegeId,
            new SiegeAssaultClaimRequest(assault.RunId, true, SharedContract.Version));
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(nameof(SiegeState.Won), result!.Outcome);
    }

    [Fact]
    public async Task ARepelledSiegeBarsTheAttackerForADay()
    {
        var instanceId = await SeedAsync();
        var siegeId = await ReachAssaultAsync(instanceId);
        var assault = await BeginAsync(instanceId, siegeId);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Service.ClaimAssaultAsync(instanceId, TestIds.Player, siegeId,
            new SiegeAssaultClaimRequest(assault.RunId, false, SharedContract.Version));

        var tooSoon = await Service.DeclareAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(SiegeError.OnCooldown, tooSoon.Error);
    }

    [Fact]
    public void AnArmyThatBarelyClearsTheGateFacesTheLongestFight()
    {
        var atGate = SiegeAssaultRules.EncounterFor(1000, 1667, 1, 0);
        var overwhelming = SiegeAssaultRules.EncounterFor(1000, 400, 1, 2);

        Assert.Equal(SiegeAssaultRules.MaximumWaves, atGate.Waves);
        Assert.Equal(SiegeAssaultRules.MinimumWaves, overwhelming.Waves);
        Assert.True(atGate.EnemyLevel > overwhelming.EnemyLevel);
        Assert.Equal(2, overwhelming.EliteCount);
    }

    // -----------------------------------------------------------------
    // The war log
    // -----------------------------------------------------------------

    [Fact]
    public async Task ASiegeWritesItsWholeStoryToTheWarLog()
    {
        // A defender who slept through it should be told, in order, what happened.
        var instanceId = await SeedAsync(garrisonIds: new[] { "rival-hero" });
        var siegeId = await ReachAssaultAsync(instanceId);
        var assault = await BeginAsync(instanceId, siegeId);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Service.ClaimAssaultAsync(instanceId, TestIds.Player, siegeId,
            new SiegeAssaultClaimRequest(assault.RunId, true, SharedContract.Version));

        var log = await WarLog.ReadAsync(instanceId);

        Assert.Equal(
            new[]
            {
                nameof(WarLogKind.SiegeWon), nameof(WarLogKind.SiegeAssaultBegun),
                nameof(WarLogKind.SiegeMusterClosed), nameof(WarLogKind.SiegeDeclared)
            },
            log.Select(e => e.Kind));

        var won = log.First();
        Assert.Equal(TestIds.Player, won.ActorUserId);
        Assert.Equal(TestIds.Rival, won.SubjectUserId);
        Assert.Equal(Target, won.RegionId);
        Assert.Contains("1 champion taken prisoner", won.Detail);
        Assert.Contains("WarLogEntryAdded", _hub.MethodsSentTo(instanceId));
    }

    [Fact]
    public async Task ALapsedSiegeIsLoggedAtTheMomentItLapsed()
    {
        var instanceId = await SeedAsync();
        var declared = await DeclareAsync(instanceId);
        var siege = await _db.Sieges.SingleAsync(s => s.Id == declared.Id);

        // Nobody looks until long after: the log still says when it happened, not when it was noticed.
        _clock.Advance(SiegeRules.Muster + SiegeRules.AssaultWindow + TimeSpan.FromHours(3));
        await Service.AdvanceAsync(instanceId);

        var lapsed = (await WarLog.ReadAsync(instanceId)).First(e => e.Kind == nameof(WarLogKind.SiegeLapsed));
        Assert.Equal(siege.AssaultEndsAt, lapsed.OccurredAt);
    }

    [Fact]
    public async Task TheWarLogReadsOnlyTheCurrentSeason()
    {
        var instanceId = await SeedAsync();
        await DeclareAsync(instanceId);

        var instance = await _db.GameInstances.FirstAsync(g => g.Id == instanceId);
        instance.SeasonNumber++;
        await _db.SaveChangesAsync();

        Assert.Empty(await WarLog.ReadAsync(instanceId));
    }

    // -----------------------------------------------------------------
    // Arrangement
    // -----------------------------------------------------------------

    private async Task<Guid> ReachAssaultAsync(Guid instanceId)
    {
        var declared = await DeclareAsync(instanceId);
        _clock.Advance(SiegeRules.Muster);
        await Service.AdvanceAsync(instanceId);
        return declared.Id;
    }

    private async Task<SiegeAssaultResponse> BeginAsync(Guid instanceId, Guid siegeId)
    {
        var (outcome, assault) = await Service.BeginAssaultAsync(instanceId, TestIds.Player, siegeId, SharedContract.Version);
        Assert.True(outcome.Succeeded, outcome.Message);
        return assault!;
    }

    private static SiegeDeclareRequest Request(string regionId = Target, string[]? army = null, string? version = null)
        => new(regionId, (army ?? new[] { "hero-1" }).ToList(), version ?? SharedContract.Version);

    private async Task<SiegeResponse> DeclareAsync(Guid instanceId)
    {
        var outcome = await Service.DeclareAsync(instanceId, TestIds.Player, Request());
        Assert.True(outcome.Succeeded, outcome.Message);
        return outcome.Siege!;
    }

    /// <summary>
    /// The player's region and a rival's, worn down to 40 resolve by default so a siege may be
    /// declared, plus the player's saved roster of level-20 heroes.
    /// </summary>
    private async Task<Guid> SeedAsync(
        int resolve = 40,
        bool capital = false,
        float garrisonPower = 0f,
        TimeSpan? claimedAgo = null,
        TimeSpan? seasonEndsIn = null,
        WorldRegionData? extra = null,
        string[]? roster = null,
        string? secondAttacker = null,
        string[]? garrisonIds = null,
        bool clockIsReal = false)
    {
        var instance = await _db.AddInstanceAsync();

        // Pin the season to the test clock, so "the last day" means the test clock's last day.
        instance.SeasonStartedAt = _clock.UtcNow.AddDays(-1);
        instance.SeasonLengthDays = 30;
        if (seasonEndsIn.HasValue)
        {
            instance.SeasonLengthDays = 1;
            instance.SeasonStartedAt = _clock.UtcNow + seasonEndsIn.Value - TimeSpan.FromDays(1);
        }
        await _db.SaveChangesAsync();

        var mine = TestWorld.OwnedBy(TestIds.Player, "r0");
        var rival = TestWorld.OwnedBy(TestIds.Rival, Target);
        rival.Resolve = resolve;
        rival.IsCapital = capital;

        if (claimedAgo.HasValue)
        {
            // The raid path reads the real clock, so a truce it must see is stamped against that.
            var reference = clockIsReal ? DateTime.UtcNow : _clock.UtcNow;
            rival.ClaimedAtUtcTicks = (reference - claimedAgo.Value).Ticks;
        }

        var regions = new List<WorldRegionData> { mine, rival };
        if (extra != null) regions.Add(extra);

        var world = TestWorld.Blob(regions.ToArray());
        if (garrisonPower > 0) Garrison(world, rival, garrisonPower);
        if (garrisonIds != null)
        {
            var entry = WorldRegionBlob.EnsureOverride(WorldRegionBlob.FindRegion(world, Target)!, TestWorld.KeepIn(rival));
            entry["GarrisonCharacterIds"] = new JsonArray(garrisonIds.Select(id => (JsonNode)id!).ToArray());
        }
        await _db.AddWorldAsync(instance.Id, world);

        var heroes = (roster ?? new[] { "hero-1" }).Select(id => TestSave.Character(id, 20)).ToArray();
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, TestSave.ToJson(TestSave.Roster(heroes)));

        if (secondAttacker != null)
        {
            if (!await _db.Users.AnyAsync(u => u.Id == secondAttacker))
            {
                _db.Users.Add(new ApplicationUser { Id = secondAttacker, UserName = secondAttacker });
                await _db.SaveChangesAsync();
            }
            await _db.AddPlayerSaveAsync(instance.Id, secondAttacker, TestSave.ToJson(TestSave.Roster(heroes)));
        }

        foreach (var id in new[] { TestIds.Player, TestIds.Rival })
        {
            if (!await _db.Users.AnyAsync(u => u.Id == id))
                _db.Users.Add(new ApplicationUser { Id = id, UserName = id });
        }
        await _db.SaveChangesAsync();

        return instance.Id;
    }

    private static void Garrison(JsonNode world, WorldRegionData region, float power)
    {
        var entry = WorldRegionBlob.EnsureOverride(
            WorldRegionBlob.FindRegion(world, region.RegionId)!, TestWorld.KeepIn(region));
        entry["GarrisonPower"] = power;
    }

    private async Task GarrisonAsync(Guid instanceId, float power)
    {
        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == instanceId);
        var world = JsonNode.Parse(row.GameData)!;
        var region = WorldRegionBlob.ReadRegion(WorldRegionBlob.FindRegion(world, Target)!);
        Garrison(world, region, power);
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();
    }

    private async Task ReplaceOwnerAsync(Guid instanceId, string newOwner)
    {
        var row = await _db.WorldViewGameData.FirstAsync(w => w.GameInstanceId == instanceId);
        var world = JsonNode.Parse(row.GameData)!;
        WorldRegionBlob.CaptureRegion(WorldRegionBlob.FindRegion(world, Target)!, newOwner, newOwner, _clock.UtcNow);
        row.GameData = world.ToJsonString();
        await _db.SaveChangesAsync();
    }

    private async Task<WorldRegionData> ReadRegionAsync(Guid instanceId)
        => TestWorld.ReadRegion(await _db.ReadWorldAsync(instanceId), Target);

    private async Task<List<WorldRegionData>> ReadAllAsync(Guid instanceId)
        => WorldRegionBlob.ReadAllRegions(await _db.ReadWorldAsync(instanceId));
}
