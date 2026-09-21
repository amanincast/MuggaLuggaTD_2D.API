using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Passive PvP, through <see cref="WorldPvPService.ResolveAsync"/>.
///
/// <para>Two things about this service shape every test here.</para>
///
/// <para><b>It has never run.</b> Two-client multiplayer has not been played, so nothing below is a
/// regression test — it is the first time these rules have been checked at all.</para>
///
/// <para><b>It still reads the flat world.</b> The service looks its target up in a top-level
/// <c>Locations</c> array, which is how worlds were stored before regions. Current worlds have no
/// such array, so the rules below are exercised against the shape the service actually reads, and
/// <see cref="AnAttackInARegionWorld_CannotFindItsTarget"/> records the gap between that and the
/// world the game now runs on.</para>
///
/// <para>The dice belong to the server and cannot be injected, so the fights that need a particular
/// result are rigged by power and run until it comes up. Where a test only needs the result to make
/// sense, it checks the invariant across many fights instead.</para>
/// </summary>
public class WorldPvPServiceTests
{
    private static string Contract => SharedContract.Version;

    private const string DefenderKeep = "r0:0";

    // -----------------------------------------------------------------
    // Refusals that need no world at all
    // -----------------------------------------------------------------

    [Fact]
    public async Task AnAttackerRunningDifferentRulesIsTurnedAway()
    {
        // Both sides compute power with the same shared code. A stale client would price the same
        // party differently, so the attack is refused rather than resolved under rules it disagrees
        // with.
        using var fixture = await Arrange();

        var (outcome, world) = await fixture.Service.ResolveAsync(
            fixture.InstanceId, TestIds.Player, "Mike",
            new PvPAttackRequest(DefenderKeep, new List<string> { "hero-1" }, "0.0.1"));

        Assert.Equal(PvPAttackError.ContractMismatch, outcome.Error);
        Assert.Null(world);
    }

    [Fact]
    public async Task AnAttackOnAWorldThatDoesNotExistIsRefused()
    {
        using var db = TestDb.Create();
        var instance = await db.AddInstanceAsync();
        var service = new WorldPvPService(db, new FakeGameContent(), NullLogger<WorldPvPService>.Instance);

        var (outcome, _) = await service.ResolveAsync(
            instance.Id, TestIds.Player, "Mike",
            new PvPAttackRequest(DefenderKeep, new List<string> { "hero-1" }, Contract));

        Assert.Equal(PvPAttackError.WorldNotFound, outcome.Error);
    }

    // -----------------------------------------------------------------
    // The gap: PvP has not moved to regions
    // -----------------------------------------------------------------

    [Fact]
    public async Task AnAttackInARegionWorld_CannotFindItsTarget()
    {
        // A record of a live defect, not an endorsement of it.
        //
        // The world is stored as regions now, and a site is regenerated from its region's seed
        // rather than listed. This service still looks for a top-level "Locations" array, which no
        // current world has — so every attack on a real, rival-held site is refused as if the target
        // did not exist. The client sends exactly this request from the region dossier.
        //
        // When PvP is ported to regions, delete this test and un-skip the one below it.
        using var db = TestDb.Create();
        var instance = await db.AddInstanceAsync();
        var region = TestWorld.OwnedBy(TestIds.Rival);
        await db.AddWorldAsync(instance.Id, TestWorld.Blob(region));
        await db.AddPlayerSaveAsync(instance.Id, TestIds.Player,
            TestSave.ToJson(TestSave.Roster(TestSave.Character("hero-1", level: 10))));

        var service = new WorldPvPService(db, new FakeGameContent(), NullLogger<WorldPvPService>.Instance);

        var (outcome, _) = await service.ResolveAsync(
            instance.Id, TestIds.Player, "Mike",
            new PvPAttackRequest(TestWorld.KeepIn(region), new List<string> { "hero-1" }, Contract));

        Assert.Equal(PvPAttackError.LocationNotFound, outcome.Error);
    }

    [Fact(Skip = "PvP still reads the pre-region flat world; see AnAttackInARegionWorld_CannotFindItsTarget.")]
    public async Task ARivalsKeepInARegionWorld_CanBeBesieged()
    {
        // What should happen: a rival-held region's keep is the siege target, its defence comes from
        // the region's hold, and taking it takes the region.
        using var db = TestDb.Create();
        var instance = await db.AddInstanceAsync();
        var region = TestWorld.OwnedBy(TestIds.Rival);
        await db.AddWorldAsync(instance.Id, TestWorld.Blob(region));
        await db.AddPlayerSaveAsync(instance.Id, TestIds.Player,
            TestSave.ToJson(TestSave.Roster(TestSave.Character("hero-1", level: 50))));

        var service = new WorldPvPService(db, new FakeGameContent(), NullLogger<WorldPvPService>.Instance);

        var (outcome, world) = await service.ResolveAsync(
            instance.Id, TestIds.Player, "Mike",
            new PvPAttackRequest(TestWorld.KeepIn(region), new List<string> { "hero-1" }, Contract));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(TestIds.Player, TestWorld.ReadRegion(world, region.RegionId).OwnerUserId);
    }

    // -----------------------------------------------------------------
    // Who may be attacked
    // -----------------------------------------------------------------

    [Fact]
    public async Task OnlyAnotherPlayersHoldingCanBeAttackedThisWay()
    {
        // Neutral and enemy ground is PvE. Sending it through here would hand over a capture on the
        // attacker's say-so, with no fight the server can price.
        using var fixture = await Arrange(ownership: LocationOwnership.Neutral, ownerUserId: null);

        var (outcome, world) = await fixture.Service.ResolveAsync(
            fixture.InstanceId, TestIds.Player, "Mike",
            new PvPAttackRequest(DefenderKeep, new List<string> { "hero-1" }, Contract));

        Assert.Equal(PvPAttackError.NotAttackable, outcome.Error);
        Assert.Null(world);
    }

    [Fact]
    public async Task APlayerCannotAttackTheirOwnHolding()
    {
        using var fixture = await Arrange(ownerUserId: TestIds.Player);

        var (outcome, _) = await fixture.Service.ResolveAsync(
            fixture.InstanceId, TestIds.Player, "Mike",
            new PvPAttackRequest(DefenderKeep, new List<string> { "hero-1" }, Contract));

        Assert.Equal(PvPAttackError.NotAttackable, outcome.Error);
    }

    // -----------------------------------------------------------------
    // Who may attack — validated against the persisted roster, not the request
    // -----------------------------------------------------------------

    [Fact]
    public async Task APlayerCannotAttackWithCharactersTheyDoNotOwn()
    {
        // The party is filtered against the saved roster, so naming someone else's hero — or one
        // that never existed — contributes nothing rather than fighting for free.
        using var fixture = await Arrange();

        var (outcome, _) = await fixture.Service.ResolveAsync(
            fixture.InstanceId, TestIds.Player, "Mike",
            new PvPAttackRequest(DefenderKeep, new List<string> { "not-my-hero" }, Contract));

        Assert.Equal(PvPAttackError.NoAttackers, outcome.Error);
    }

    [Fact]
    public async Task APlayerWithNoSaveAtAllCannotAttack()
    {
        using var fixture = await Arrange(withAttackerSave: false);

        var (outcome, _) = await fixture.Service.ResolveAsync(
            fixture.InstanceId, TestIds.Player, "Mike",
            new PvPAttackRequest(DefenderKeep, new List<string> { "hero-1" }, Contract));

        Assert.Equal(PvPAttackError.NoAttackers, outcome.Error);
    }

    [Fact]
    public async Task AnEmptyPartyCannotAttack()
    {
        using var fixture = await Arrange();

        var (outcome, _) = await fixture.Service.ResolveAsync(
            fixture.InstanceId, TestIds.Player, "Mike",
            new PvPAttackRequest(DefenderKeep, new List<string>(), Contract));

        Assert.Equal(PvPAttackError.NoAttackers, outcome.Error);
    }

    [Fact]
    public async Task ACharacterStandingGarrisonCannotAlsoBeOutAttacking()
    {
        // Committed is committed: a hero defending one of the player's own holdings is read out of
        // the world blob, so the client cannot claim otherwise.
        using var fixture = await Arrange(alsoGarrisonForAttacker: new[] { "hero-1" });

        var (outcome, _) = await fixture.Service.ResolveAsync(
            fixture.InstanceId, TestIds.Player, "Mike",
            new PvPAttackRequest(DefenderKeep, new List<string> { "hero-1" }, Contract));

        Assert.Equal(PvPAttackError.NoAttackers, outcome.Error);
    }

    [Fact]
    public async Task APrisonerCannotAttack()
    {
        using var fixture = await Arrange(prisonersAtTarget: new[] { "hero-1" });

        var (outcome, _) = await fixture.Service.ResolveAsync(
            fixture.InstanceId, TestIds.Player, "Mike",
            new PvPAttackRequest(DefenderKeep, new List<string> { "hero-1" }, Contract));

        Assert.Equal(PvPAttackError.NoAttackers, outcome.Error);
    }

    [Fact]
    public async Task NamingTheSameHeroSeveralTimesDoesNotMultiplyTheirStrength()
    {
        using var fixture = await Arrange(attackerLevel: 5);

        var (outcome, _) = await fixture.Service.ResolveAsync(
            fixture.InstanceId, TestIds.Player, "Mike",
            new PvPAttackRequest(DefenderKeep, new List<string> { "hero-1", "hero-1", "hero-1" }, Contract));

        Assert.True(outcome.Succeeded, outcome.Message);

        var expected = PartyPowerCalculator.CalculatePartyPower(
            TestSave.Roster(TestSave.Character("hero-1", level: 5)),
            new[] { "hero-1" },
            Array.Empty<Abilities.Models.GameAbility>());

        Assert.Equal(expected, outcome.Response!.AttackerPower);
    }

    // -----------------------------------------------------------------
    // How the fight resolves
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheDefenceComesFromTheGarrisonSnapshotOnTheLocation()
    {
        // Not from anything the attacker sends: the request carries no power at all.
        using var fixture = await Arrange(garrisonPower: 777f);

        var (outcome, _) = await fixture.Service.ResolveAsync(
            fixture.InstanceId, TestIds.Player, "Mike",
            new PvPAttackRequest(DefenderKeep, new List<string> { "hero-1" }, Contract));

        Assert.Equal(777f, outcome.Response!.DefenderPower);
    }

    [Fact]
    public async Task EveryFightIsInternallyConsistent()
    {
        // The dice are the server's, so rather than forcing a result this runs a lot of fights and
        // checks the result always follows from the roll it reports: a d20, a power modifier, and a
        // fixed threshold. A win the numbers do not support would show up here.
        for (int i = 0; i < 200; i++)
        {
            using var fixture = await Arrange(attackerLevel: 3, garrisonPower: 300f);

            var (outcome, _) = await fixture.Service.ResolveAsync(
                fixture.InstanceId, TestIds.Player, "Mike",
                new PvPAttackRequest(DefenderKeep, new List<string> { "hero-1" }, Contract));

            var result = outcome.Response!;
            Assert.InRange(result.D20Roll, 1, 20);
            Assert.InRange(result.Modifier, -PassivePvPResolver.MaxModifier, PassivePvPResolver.MaxModifier);
            Assert.Equal(result.D20Roll + result.Modifier, result.Total);
            Assert.Equal(result.Total >= PassivePvPResolver.WinThreshold, result.AttackerWins);
        }
    }

    [Fact]
    public async Task AnOverwhelmingAttackerStillCannotBeCertainOfWinning()
    {
        // The modifier is clamped so the strong side never has a guaranteed result — a 1 on the die
        // is still a loss. This checks the clamp survives however lopsided the fight.
        using var fixture = await Arrange(attackerLevel: 500, garrisonPower: 0f);

        var (outcome, _) = await fixture.Service.ResolveAsync(
            fixture.InstanceId, TestIds.Player, "Mike",
            new PvPAttackRequest(DefenderKeep, new List<string> { "hero-1" }, Contract));

        Assert.Equal(PassivePvPResolver.MaxModifier, outcome.Response!.Modifier);
    }

    [Fact]
    public async Task WinningTakesTheHoldingAndItsDefendersWithIt()
    {
        // Rigged heavily in the attacker's favour and repeated until the dice cooperate; a win is a
        // 95% outcome here, so this settles almost immediately.
        await UntilAsync(win: true, async (fixture, outcome, world) =>
        {
            Assert.Equal("Captured", outcome.Response!.ConquestOutcome);
            Assert.Null(outcome.Response.DefeatOutcome);

            var location = WorldBlobEditor.FindLocation(world, DefenderKeep)!;
            Assert.Equal(LocationOwnership.Player, WorldBlobEditor.GetOwnership(location));
            Assert.Equal(TestIds.Player, WorldBlobEditor.GetOwnerUserId(location));

            // The defenders do not evaporate — they are held at the keep they lost, for their owner
            // to rescue by retaking it.
            Assert.Empty((JsonArray)location["GarrisonCharacterIds"]!);
            Assert.Equal(0f, location["GarrisonPower"]!.GetValue<float>());
            Assert.Contains("defender-1", Strings(location["CapturedCharacterIds"]));

            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task LosingCostsTheAttackerSomethingTheServerRecords()
    {
        // Rigged against the attacker, so a loss is the 95% outcome.
        await UntilAsync(win: false, async (fixture, outcome, world) =>
        {
            var response = outcome.Response!;
            Assert.Null(response.ConquestOutcome);
            Assert.Contains(response.DefeatOutcome,
                new[] { "PartyMemberKilled", "PartyMemberCaptured", "PartyCaptured", "Escaped" });

            var location = WorldBlobEditor.FindLocation(world, DefenderKeep)!;

            // The holding does not change hands on a loss.
            Assert.Equal(TestIds.Rival, WorldBlobEditor.GetOwnerUserId(location));

            // Captured attackers are written into the world, not merely reported — a client that
            // ignores its own defeat still finds them imprisoned on next load.
            if (response.DefeatOutcome is "PartyMemberCaptured" or "PartyCaptured")
            {
                var prisoners = Strings(location["CapturedCharacterIds"]);
                Assert.NotEmpty(response.AffectedCharacterIds);
                foreach (var id in response.AffectedCharacterIds)
                    Assert.Contains(id, prisoners);
            }

            await Task.CompletedTask;
        });
    }

    // -----------------------------------------------------------------
    // Arrangement
    // -----------------------------------------------------------------

    /// <summary>
    /// Runs rigged fights until one lands the requested way, then asserts against it.
    ///
    /// <para>The service rolls its own dice from <c>Random.Shared</c>, which a test cannot seed. The
    /// power gap is set to make the wanted result a 95% outcome, so the loop is a formality — but it
    /// is a loop rather than a single attempt so the suite does not fail once every twenty runs.</para>
    /// </summary>
    private static async Task UntilAsync(
        bool win, Func<Fixture, PvPAttackOutcome, JsonNode?, Task> assert)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            using var fixture = win
                ? await Arrange(attackerLevel: 100, garrisonPower: 0f, defenders: new[] { "defender-1" })
                : await Arrange(attackerLevel: 1, garrisonPower: 100_000f, defenders: new[] { "defender-1" });

            var (outcome, world) = await fixture.Service.ResolveAsync(
                fixture.InstanceId, TestIds.Player, "Mike",
                new PvPAttackRequest(DefenderKeep, new List<string> { "hero-1" }, Contract));

            Assert.True(outcome.Succeeded, outcome.Message);
            if (outcome.Response!.AttackerWins != win) continue;

            await assert(fixture, outcome, world);
            return;
        }

        Assert.Fail($"100 fights rigged for the attacker to {(win ? "win" : "lose")} never did — " +
                    "the power modifier is no longer reaching the die.");
    }

    private sealed class Fixture : IDisposable
    {
        public required ApplicationDbContext Db { get; init; }
        public required Guid InstanceId { get; init; }
        public required WorldPvPService Service { get; init; }

        public void Dispose() => Db.Dispose();
    }

    /// <summary>
    /// A world holding one location, in the flat pre-region shape this service still reads, plus the
    /// attacker's persisted roster.
    /// </summary>
    private static async Task<Fixture> Arrange(
        LocationOwnership ownership = LocationOwnership.Player,
        string? ownerUserId = TestIds.Rival,
        float garrisonPower = 100f,
        long attackerLevel = 5,
        bool withAttackerSave = true,
        string[]? defenders = null,
        string[]? prisonersAtTarget = null,
        string[]? alsoGarrisonForAttacker = null)
    {
        var db = TestDb.Create();
        var instance = await db.AddInstanceAsync();

        var locations = new JsonArray
        {
            Location(DefenderKeep, ownership, ownerUserId, garrisonPower, defenders, prisonersAtTarget)
        };

        if (alsoGarrisonForAttacker != null)
        {
            // Somewhere the attacker holds, with those heroes standing on its walls.
            locations.Add(Location("r0:9", LocationOwnership.Player, TestIds.Player, 50f,
                alsoGarrisonForAttacker, null));
        }

        await db.AddWorldAsync(instance.Id, new JsonObject
        {
            ["FormatVersion"] = WorldRegionBlob.LegacyFormatVersion,
            ["Locations"] = locations
        });

        if (withAttackerSave)
        {
            await db.AddPlayerSaveAsync(instance.Id, TestIds.Player,
                TestSave.ToJson(TestSave.Roster(TestSave.Character("hero-1", attackerLevel))));
        }

        return new Fixture
        {
            Db = db,
            InstanceId = instance.Id,
            Service = new WorldPvPService(db, new FakeGameContent(), NullLogger<WorldPvPService>.Instance)
        };
    }

    private static JsonObject Location(
        string locationId, LocationOwnership ownership, string? ownerUserId,
        float garrisonPower, string[]? garrison, string[]? prisoners)
    {
        return new JsonObject
        {
            ["LocationId"] = locationId,
            ["Type"] = (int)LocationType.Castle,
            ["Ownership"] = (int)ownership,
            ["OwnerUserId"] = ownerUserId ?? string.Empty,
            ["OwnerDisplayName"] = ownerUserId ?? string.Empty,
            ["GarrisonPower"] = garrisonPower,
            ["GarrisonCharacterIds"] = ToArray(garrison),
            ["CapturedCharacterIds"] = ToArray(prisoners)
        };
    }

    private static JsonArray ToArray(string[]? values)
    {
        var array = new JsonArray();
        foreach (var value in values ?? Array.Empty<string>())
            array.Add(value);
        return array;
    }

    private static IReadOnlyList<string> Strings(JsonNode? node)
        => (node as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? new List<string>();
}
