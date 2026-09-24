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
/// Raiding a rival region, through <see cref="WorldRaidService.RaidAsync"/>.
///
/// <para>This replaced passive PvP, which had been dead since the world became regions — it looked
/// its target up in a flat list of locations that format 4 worlds do not have, so every attack was
/// refused. The replacement is not a port: the old service handed the holding to whoever won one
/// d20, and against regions that would have meant a single roll taking a region. The design forbids
/// it in one sentence — <b>no single fight may be worth a region</b> — so a raid takes nothing and
/// wears resolve down instead.</para>
///
/// <para>Most of what is pinned here is therefore about <i>limits</i>: who may be raided, what a
/// raid cannot do, and that the cooldown holds. The dice are the server's and cannot be seeded, so
/// tests that need a particular result rig the power gap and repeat.</para>
/// </summary>
public class WorldRaidServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeGameContent _content = new();

    private WorldRaidService Service => new(_db, _content, NullLogger<WorldRaidService>.Instance);

    private static string Contract => SharedContract.Version;

    private const string Rivals = "r1";

    public void Dispose() => _db.Dispose();

    // -----------------------------------------------------------------
    // Who may be raided
    // -----------------------------------------------------------------

    [Fact]
    public async Task AnAttackerRunningDifferentRulesIsTurnedAway()
    {
        // The dossier showed the player a bar and an expected cost. Those have to be the ones the
        // server applies, or the two sides disagree about what the march was worth.
        var instanceId = await SeedAsync();

        var (outcome, world) = await Service.RaidAsync(
            instanceId, TestIds.Player, Request(version: "0.0.1"));

        Assert.Equal(RaidError.ContractMismatch, outcome.Error);
        Assert.Null(world);
    }

    [Fact]
    public async Task ARaidAgainstAWorldThatDoesNotExistIsRefused()
    {
        var instance = await _db.AddInstanceAsync();

        var (outcome, _) = await Service.RaidAsync(instance.Id, TestIds.Player, Request());

        Assert.Equal(RaidError.WorldNotFound, outcome.Error);
    }

    [Fact]
    public async Task ARegionTheWorldDoesNotContainIsRefused()
    {
        var instanceId = await SeedAsync();

        var (outcome, _) = await Service.RaidAsync(
            instanceId, TestIds.Player, Request(regionId: "r-nowhere"));

        Assert.Equal(RaidError.RegionNotFound, outcome.Error);
    }

    [Fact]
    public async Task UnclaimedLandIsNotRaided_ItIsTaken()
    {
        // Neutral and NPC ground is PvE: go and clear its keep. Raiding it would wear down a morale
        // nobody is defending.
        var instanceId = await SeedAsync(rival: TestWorld.Region(Rivals));

        var (outcome, _) = await Service.RaidAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(RaidError.NotRaidable, outcome.Error);
        Assert.Contains("keep", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APlayerCannotRaidTheirOwnRegion()
    {
        var instanceId = await SeedAsync(rival: TestWorld.OwnedBy(TestIds.Player, Rivals));

        var (outcome, _) = await Service.RaidAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(RaidError.NotRaidable, outcome.Error);
    }

    [Fact]
    public async Task ASeatCannotBeRaided()
    {
        // Capitals are off the board (design §7). A seat cannot be besieged, so wearing its resolve
        // down could never lead anywhere — raiding one is simply not a move.
        var capital = TestWorld.OwnedBy(TestIds.Rival, Rivals);
        capital.IsCapital = true;
        var instanceId = await SeedAsync(rival: capital);

        var (outcome, _) = await Service.RaidAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(RaidError.NotRaidable, outcome.Error);
        Assert.Contains("seat", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------
    // Who may march
    // -----------------------------------------------------------------

    [Fact]
    public async Task APlayerCannotMarchWithCharactersTheyDoNotOwn()
    {
        var instanceId = await SeedAsync();

        var (outcome, _) = await Service.RaidAsync(
            instanceId, TestIds.Player, Request(party: new[] { "not-my-hero" }));

        Assert.Equal(RaidError.NoAttackers, outcome.Error);
    }

    [Fact]
    public async Task APlayerWithNoSaveCannotMarch()
    {
        var instanceId = await SeedAsync(withAttackerSave: false);

        var (outcome, _) = await Service.RaidAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(RaidError.NoAttackers, outcome.Error);
    }

    [Fact]
    public async Task AnEmptyMarchIsRefused()
    {
        var instanceId = await SeedAsync();

        var (outcome, _) = await Service.RaidAsync(
            instanceId, TestIds.Player, Request(party: Array.Empty<string>()));

        Assert.Equal(RaidError.NoAttackers, outcome.Error);
    }

    [Fact]
    public async Task ACharacterStandingGarrisonCannotAlsoBeOutRaiding()
    {
        // Committed is committed. Garrisons live in each site's override now, so this is the region
        // world's version of a check the flat world did per location.
        var mine = TestWorld.OwnedBy(TestIds.Player, "r0");
        var instanceId = await SeedAsync(mine: mine, garrisonAtMine: new[] { "hero-1" });

        var (outcome, _) = await Service.RaidAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(RaidError.NoAttackers, outcome.Error);
    }

    [Fact]
    public async Task APrisonerCannotMarch()
    {
        var instanceId = await SeedAsync(prisonersAtRival: new[] { "hero-1" });

        var (outcome, _) = await Service.RaidAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(RaidError.NoAttackers, outcome.Error);
    }

    [Fact]
    public async Task AnotherPlayersGarrisonDoesNotTieUpYourChampions()
    {
        // The rival's keep holds *their* people. A shared id would otherwise let a defender lock an
        // attacker's roster by garrisoning.
        var instanceId = await SeedAsync(garrisonAtRival: new[] { "hero-1" }, attackerLevel: 40);

        var (outcome, _) = await Service.RaidAsync(instanceId, TestIds.Player, Request());

        Assert.True(outcome.Succeeded, outcome.Message);
    }

    // -----------------------------------------------------------------
    // The bar
    // -----------------------------------------------------------------

    [Fact]
    public async Task AMarchBelowTheRaidBarIsTurnedBack()
    {
        // A level 1 hero against a garrisoned tier-2 region is nowhere near the bar.
        var instanceId = await SeedAsync(garrisonPowerAtRival: 5_000f, attackerLevel: 1);

        var (outcome, world) = await Service.RaidAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(RaidError.BelowRaidBar, outcome.Error);
        Assert.Null(world);
        Assert.Empty(await _db.RegionRaids.ToListAsync());
    }

    [Fact]
    public async Task TheBarIsMeasuredAgainstTheWholeRegion_NotOneSitesGarrison()
    {
        // A region's defence is the sum of what stands at its sites. Measuring against a single
        // site's snapshot — which is what the old flat-world PvP did — would let an attacker pick
        // the weakest door and walk past everything else stationed there.
        var instanceId = await SeedAsync(garrisonAcrossRivalSites: new[] { 3_000f, 3_000f, 3_000f });

        var region = TestWorld.ReadRegion(await _db.ReadWorldAsync(instanceId), Rivals);
        Assert.Equal(9_000f, RegionHoldCalculator.GarrisonPowerOf(region));

        var (outcome, _) = await Service.RaidAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(RaidError.BelowRaidBar, outcome.Error);
    }

    // -----------------------------------------------------------------
    // What a raid actually does
    // -----------------------------------------------------------------

    [Fact]
    public async Task AWinningRaidWearsTheRegionDownAndTakesNothingElse()
    {
        await UntilAsync(win: true, attempt =>
        {
            var region = TestWorld.ReadRegion(attempt.World, Rivals);
            var response = attempt.Response;

            // Resolve moved, and that is the entire effect.
            Assert.True(response.ResolveDamage > 0);
            Assert.Equal(response.ResolveAfter, region.Resolve);
            Assert.True(region.Resolve < RegionResolveRules.Maximum);

            // The region did NOT change hands. This is the whole point of the design.
            Assert.Equal(TestIds.Rival, region.OwnerUserId);
            Assert.Equal(LocationOwnership.Player, region.Ownership);

            // Nor did anything else about it move.
            Assert.Equal(1, region.Entrenchment);
            Assert.False(region.IsCapital);
        });
    }

    [Fact]
    public async Task ARepelledRaidLeavesTheRegionExactlyAsItWas()
    {
        await UntilAsync(win: false, attempt =>
        {
            var region = TestWorld.ReadRegion(attempt.World, Rivals);

            Assert.Equal(0, attempt.Response.ResolveDamage);
            Assert.Equal(RegionResolveRules.Maximum, region.Resolve);
            Assert.Equal(TestIds.Rival, region.OwnerUserId);
        });
    }

    [Fact]
    public async Task NoSingleRaidIsWorthMoreThanItsBoundedDamage()
    {
        // The sentence the whole design rests on, as an assertion: whatever an attacker brings, one
        // raid moves resolve by at most MaximumResolveDamage and never takes the region.
        for (int attempt = 0; attempt < 40; attempt++)
        {
            using var db = TestDb.Create();
            var instanceId = await SeedAsync(db, attackerLevel: 5_000);
            var service = new WorldRaidService(db, _content, NullLogger<WorldRaidService>.Instance);

            var (outcome, world) = await service.RaidAsync(instanceId, TestIds.Player, Request());
            Assert.True(outcome.Succeeded, outcome.Message);

            var region = TestWorld.ReadRegion(world, Rivals);
            Assert.InRange(outcome.Response!.ResolveDamage, 0, RaidResolver.MaximumResolveDamage);
            Assert.Equal(TestIds.Rival, region.OwnerUserId);
        }
    }

    [Fact]
    public async Task EveryRaidIsInternallyConsistent()
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            using var db = TestDb.Create();
            var instanceId = await SeedAsync(db, attackerLevel: 30);
            var service = new WorldRaidService(db, _content, NullLogger<WorldRaidService>.Instance);

            var (outcome, _) = await service.RaidAsync(instanceId, TestIds.Player, Request());
            Assert.True(outcome.Succeeded, outcome.Message);

            var r = outcome.Response!;
            Assert.InRange(r.D20Roll, 1, 20);
            Assert.Equal(r.D20Roll + r.Modifier, r.Total);
            Assert.Equal(r.Total >= PassivePvPResolver.WinThreshold, r.AttackerWins);
            Assert.Equal(r.ResolveBefore - r.ResolveDamage, r.ResolveAfter);
            Assert.True(r.MarchingPower >= r.RaidBar);
            if (!r.AttackerWins) Assert.Equal(0, r.ResolveDamage);
        }
    }

    [Fact]
    public async Task ARaidIsRecordedWhetherItLandedOrNot()
    {
        // The log is how a defender who was asleep finds out who has been at their border — and a
        // repelled raid is still someone at the border.
        var instanceId = await SeedAsync(attackerLevel: 40);

        var (outcome, _) = await Service.RaidAsync(instanceId, TestIds.Player, Request());
        Assert.True(outcome.Succeeded, outcome.Message);

        var raid = await _db.RegionRaids.SingleAsync();
        Assert.Equal(TestIds.Player, raid.UserId);
        Assert.Equal(Rivals, raid.RegionId);
        Assert.Equal(TestIds.Rival, raid.DefenderUserId);
        Assert.Equal(outcome.Response!.AttackerWins, raid.AttackerWon);
        Assert.Equal(outcome.Response.ResolveAfter, raid.ResolveAfter);
    }

    // -----------------------------------------------------------------
    // The cooldown — which is the anti-cheat
    // -----------------------------------------------------------------

    [Fact]
    public async Task APlayerCannotRaidTheSameRegionTwiceInARow()
    {
        // Rate-limiting the attacker is what bounds what cheating inside a fight can buy. It is the
        // load-bearing guard, not the dice.
        var instanceId = await SeedAsync(attackerLevel: 40);

        var (first, _) = await Service.RaidAsync(instanceId, TestIds.Player, Request());
        Assert.True(first.Succeeded, first.Message);

        var (second, world) = await Service.RaidAsync(instanceId, TestIds.Player, Request());

        Assert.Equal(RaidError.OnCooldown, second.Error);
        Assert.Null(world);
        Assert.Single(await _db.RegionRaids.ToListAsync());
    }

    [Fact]
    public async Task ARepelledRaidStillCostsTheCooldown()
    {
        // Otherwise a cheating attacker retries until the dice cooperate and the rate limit means
        // nothing at all.
        await UntilAsync(win: false, async attempt =>
        {
            var (again, _) = await attempt.Service.RaidAsync(attempt.InstanceId, TestIds.Player, Request());
            Assert.Equal(RaidError.OnCooldown, again.Error);
        });
    }

    [Fact]
    public async Task TheCooldownLapsesAfterItsHours()
    {
        var instanceId = await SeedAsync(attackerLevel: 40);
        await Service.RaidAsync(instanceId, TestIds.Player, Request());
        await AgeLastRaidAsync(RaidResolver.Cooldown + TimeSpan.FromMinutes(1));

        var (outcome, _) = await Service.RaidAsync(instanceId, TestIds.Player, Request());

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(2, await _db.RegionRaids.CountAsync());
    }

    [Fact]
    public async Task ACooldownIsPerRegion_NotPerPlayer()
    {
        // Raiding one neighbour should not stop you raiding another; the limit is on grinding a
        // particular region down.
        var other = TestWorld.OwnedBy(TestIds.Rival, "r2");
        var instanceId = await SeedAsync(attackerLevel: 40, extraRegions: new[] { other });

        await Service.RaidAsync(instanceId, TestIds.Player, Request());
        var (outcome, _) = await Service.RaidAsync(instanceId, TestIds.Player, Request(regionId: "r2"));

        Assert.True(outcome.Succeeded, outcome.Message);
    }

    [Fact]
    public async Task ACooldownIsPerAttacker_NotPerRegion()
    {
        // One player raiding a region must not shield it from everyone else — that would make a
        // friendly raid the cheapest defence in the game.
        var instanceId = await SeedAsync(attackerLevel: 40, alsoSeatedAttacker: TestIds.Owner);

        await Service.RaidAsync(instanceId, TestIds.Player, Request());
        var (outcome, _) = await Service.RaidAsync(instanceId, TestIds.Owner, Request());

        Assert.True(outcome.Succeeded, outcome.Message);
    }

    [Fact]
    public async Task ARefusedRaidDoesNotBurnTheCooldown()
    {
        // Being told "you are too weak" must not cost the attempt, or a mistaken click locks the
        // player out for hours.
        var instanceId = await SeedAsync(garrisonPowerAtRival: 50_000f, attackerLevel: 1);

        await Service.RaidAsync(instanceId, TestIds.Player, Request());

        Assert.Empty(await _db.RegionRaids.ToListAsync());
        Assert.Equal(DateTime.MinValue,
            await Service.CooldownEndsAtAsync(instanceId, TestIds.Player, Rivals));
    }

    [Fact]
    public async Task TheCooldownIsReportedSoTheClientCanShowTheWait()
    {
        var instanceId = await SeedAsync(attackerLevel: 40);

        var (outcome, _) = await Service.RaidAsync(instanceId, TestIds.Player, Request());

        var endsAt = await Service.CooldownEndsAtAsync(instanceId, TestIds.Player, Rivals);
        Assert.Equal(outcome.Response!.CooldownEndsAt, endsAt);
        Assert.True(endsAt > DateTime.UtcNow);
    }

    // -----------------------------------------------------------------
    // Arrangement
    // -----------------------------------------------------------------

    private static RegionRaidRequest Request(
        string regionId = Rivals, string[]? party = null, string? version = null)
    {
        return new RegionRaidRequest(
            regionId,
            (party ?? new[] { "hero-1" }).ToList(),
            version ?? SharedContract.Version);
    }

    private Task<Guid> SeedAsync(
        WorldRegionData? rival = null,
        WorldRegionData? mine = null,
        float garrisonPowerAtRival = 0f,
        float[]? garrisonAcrossRivalSites = null,
        string[]? garrisonAtRival = null,
        string[]? garrisonAtMine = null,
        string[]? prisonersAtRival = null,
        WorldRegionData[]? extraRegions = null,
        long attackerLevel = 20,
        bool withAttackerSave = true,
        string? alsoSeatedAttacker = null)
        => SeedAsync(_db, rival, mine, garrisonPowerAtRival, garrisonAcrossRivalSites, garrisonAtRival,
            garrisonAtMine, prisonersAtRival, extraRegions, attackerLevel, withAttackerSave, alsoSeatedAttacker);

    /// <summary>
    /// A world with the player's region and a rival's, plus the player's persisted roster. Garrisons
    /// are written as site overrides, because that is where a region's defenders actually live.
    /// </summary>
    private static async Task<Guid> SeedAsync(
        ApplicationDbContext db,
        WorldRegionData? rival = null,
        WorldRegionData? mine = null,
        float garrisonPowerAtRival = 0f,
        float[]? garrisonAcrossRivalSites = null,
        string[]? garrisonAtRival = null,
        string[]? garrisonAtMine = null,
        string[]? prisonersAtRival = null,
        WorldRegionData[]? extraRegions = null,
        long attackerLevel = 20,
        bool withAttackerSave = true,
        string? alsoSeatedAttacker = null)
    {
        var instance = await db.AddInstanceAsync();

        mine ??= TestWorld.OwnedBy(TestIds.Player, "r0");
        rival ??= TestWorld.OwnedBy(TestIds.Rival, Rivals);

        var regions = new List<WorldRegionData> { mine, rival };
        if (extraRegions != null) regions.AddRange(extraRegions);

        var world = TestWorld.Blob(regions.ToArray());

        if (garrisonPowerAtRival > 0)
            Garrison(world, rival, garrisonPowerAtRival, garrisonAtRival);
        else if (garrisonAtRival != null)
            Garrison(world, rival, 100f, garrisonAtRival);

        if (garrisonAcrossRivalSites != null)
        {
            var sites = TestWorld.SitesIn(rival);
            for (int i = 0; i < garrisonAcrossRivalSites.Length && i < sites.Count; i++)
            {
                var entry = WorldRegionBlob.EnsureOverride(
                    WorldRegionBlob.FindRegion(world, rival.RegionId)!, sites[i].SiteId);
                entry["GarrisonPower"] = garrisonAcrossRivalSites[i];
            }
        }

        if (garrisonAtMine != null)
            Garrison(world, mine, 100f, garrisonAtMine);

        if (prisonersAtRival != null)
        {
            var entry = WorldRegionBlob.EnsureOverride(
                WorldRegionBlob.FindRegion(world, rival.RegionId)!, TestWorld.KeepIn(rival));
            entry["CapturedCharacterIds"] = new JsonArray(prisonersAtRival.Select(id => (JsonNode)id!).ToArray());
            // Stamped, because captivity expires and an unstamped capture reads as already home.
            entry["CapturedAtUtcTicks"] = DateTime.UtcNow.Ticks;
        }

        await db.AddWorldAsync(instance.Id, world);

        if (withAttackerSave)
        {
            var roster = TestSave.Roster(TestSave.Character("hero-1", attackerLevel));
            await db.AddPlayerSaveAsync(instance.Id, TestIds.Player, TestSave.ToJson(roster));

            if (alsoSeatedAttacker != null)
                await db.AddPlayerSaveAsync(instance.Id, alsoSeatedAttacker, TestSave.ToJson(roster));
        }

        return instance.Id;
    }

    private static void Garrison(JsonNode world, WorldRegionData region, float power, string[]? characterIds)
    {
        var entry = WorldRegionBlob.EnsureOverride(
            WorldRegionBlob.FindRegion(world, region.RegionId)!, TestWorld.KeepIn(region));

        entry["GarrisonPower"] = power;
        entry["GarrisonCharacterIds"] = new JsonArray(
            (characterIds ?? Array.Empty<string>()).Select(id => (JsonNode)id!).ToArray());
    }

    private async Task AgeLastRaidAsync(TimeSpan age)
    {
        var raid = await _db.RegionRaids.OrderByDescending(r => r.RaidedAt).FirstAsync();
        raid.RaidedAt = DateTime.UtcNow - age;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Runs raids until one lands the requested way, then asserts against it.
    ///
    /// <para>The dice come from <c>Random.Shared</c>, which a test cannot seed. At matched power a
    /// raid is a coin flip, so either outcome arrives within a couple of attempts.</para>
    /// </summary>
    private async Task UntilAsync(bool win, Action<RaidAttempt> assert)
        => await UntilAsync(win, attempt =>
        {
            assert(attempt);
            return Task.CompletedTask;
        });

    /// <summary>One raid that came out the way a test needed, with the world it happened in.</summary>
    private sealed record RaidAttempt(
        Guid InstanceId, WorldRaidService Service, RegionRaidResponse Response, JsonNode? World);

    private async Task UntilAsync(bool win, Func<RaidAttempt, Task> assert)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            // Each attempt gets its own database, because a raid that came out the wrong way has
            // already spent its cooldown and mutated its world.
            using var db = TestDb.Create();
            var instanceId = await SeedAsync(db, attackerLevel: 20, garrisonPowerAtRival: 1_500f);
            var service = new WorldRaidService(db, _content, NullLogger<WorldRaidService>.Instance);

            var (outcome, world) = await service.RaidAsync(instanceId, TestIds.Player, Request());
            Assert.True(outcome.Succeeded, outcome.Message);
            if (outcome.Response!.AttackerWins != win) continue;

            await assert(new RaidAttempt(instanceId, service, outcome.Response, world));
            return;
        }

        Assert.Fail($"100 raids never came out as a {(win ? "win" : "repulse")} — the dice are not reaching the result.");
    }
}
