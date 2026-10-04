using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

public enum PartyError
{
    None = 0,
    ContractMismatch,
    WorldNotFound,
    PartyNotFound,
    NoRoster,
    TooManyCompanies,
    TooManyMembers,
    NotYourCharacter,
    CharacterCommitted,
    InAnotherCompany,
    BadName,
    BadBanner,
    LastCompany,
    Busy,
    SiteNotFound,
    NotInThisRegion,
    AlreadyThere,
    NoRoute,
    Empty,
    NotAmbushed,
    RunNotFound,
    RunTooFast,
}

public record PartyOutcome(PartyError Error, string? Message = null)
{
    public bool Succeeded => Error == PartyError.None;
}

/// <summary>
/// A player's companies: forming, naming and manning them, and the one answer to "is this character
/// free". <c>docs/design/parties-and-travel.md</c> (Unity repo), phase 1.
///
/// <para><b>The first company is the party the player already had.</b> The first read of a player's
/// companies in a realm forms one from the save's <c>ActiveCharacterIds</c>, standing at their capital's
/// keep, so nothing changes for a player who never forms a second.</para>
///
/// <para><b>What wins over a company.</b> A character in a company can still be garrisoned (the
/// garrison takes them out of the company — <see cref="ReleaseAsync"/>), marched on a raid or siege,
/// or captured. Those are commitments of the world; a company is a standing arrangement. What a
/// commitment does do is keep a character out of a fight and out of any company's roster until it
/// ends.</para>
/// </summary>
public class PartyService
{
    private readonly ApplicationDbContext _context;
    private readonly TavernService _tavern;
    private readonly IGameContentProvider _content;
    private readonly MaterialWalletService _wallet;
    private readonly GoldService _gold;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<PartyService> _logger;

    /// <summary>Where ambushes and a won ambush's spoils are rolled from. Tests fix it.</summary>
    public Random Dice { get; set; } = Random.Shared;

    private readonly ItemLedgerService _items;

    /// <summary>Ticks First Steps where they happen; optional so tests can build this without it.</summary>
    private readonly FirstStepsService? _firstSteps;

    /// <summary>Catches companies in auto mode up before they are read; optional so tests can build this without it.</summary>
    private readonly AutoFightService? _auto;

    public PartyService(ApplicationDbContext context, TavernService tavern, IGameContentProvider content,
        MaterialWalletService wallet, GoldService gold, ISessionLog sessionLog, ILogger<PartyService> logger,
        ItemLedgerService items, FirstStepsService? firstSteps = null, AutoFightService? auto = null)
    {
        _firstSteps = firstSteps;
        _auto = auto;
        _items = items;
        _context = context;
        _tavern = tavern;
        _content = content;
        _wallet = wallet;
        _gold = gold;
        _sessionLog = sessionLog;
        _logger = logger;
    }

    /// <summary>This player's companies, forming the first from their party if they have none yet.</summary>
    public async Task<(PartyOutcome Outcome, PartiesResponse? Response)> ListAsync(Guid gameInstanceId, string userId)
    {
        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null) return (new PartyOutcome(PartyError.WorldNotFound, "This realm has no world yet."), null);

        // A company in auto mode has been busy since it was last read (auto-fight.md): catch it up first.
        if (_auto != null) await _auto.SettleAsync(gameInstanceId, userId);
        await EnsureFirstAsync(gameInstanceId, userId, world);
        return (new PartyOutcome(PartyError.None), await ResponseAsync(gameInstanceId, userId, world));
    }

    /// <summary>
    /// The other players' companies <paramref name="userId"/> can see now (phase 5): those standing
    /// or walking in a region the viewer holds or borders (<see cref="RegionSight"/>), or where one of
    /// the viewer's own companies is. Of a company's road only the legs in sight are sent, and of its
    /// ends only those in sight. Arrivals and ambushes are settled first, as any read settles them -
    /// both are fixed by the clock, so it does not matter whose read it is.
    /// </summary>
    public async Task<(PartyOutcome Outcome, RivalCompaniesResponse? Response)> OthersAsync(Guid gameInstanceId, string userId)
    {
        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null) return (new PartyOutcome(PartyError.WorldNotFound, "This realm has no world yet."), null);

        var now = DateTime.UtcNow;
        var all = await _context.PlayerParties.Where(p => p.GameInstanceId == gameInstanceId).ToListAsync();
        bool settled = false;
        foreach (var p in all) settled |= SettleArrival(p, now);
        if (settled) await _context.SaveChangesAsync();

        var sight = RegionSight.Lit(WorldRegionBlob.ReadAllRegions(world), userId);
        foreach (var mine in all.Where(p => p.UserId == userId && p.RegionId != null)) sight.Add(mine.RegionId!);

        var seen = all
            .Where(p => p.UserId != userId && p.RegionId != null && sight.Contains(p.RegionId))
            .Where(p => MarchingArmy.ReadIds(p.CharacterIdsJson).Count > 0)
            .OrderBy(p => p.UserId).ThenBy(p => p.SortOrder)
            .ToList();

        var owners = seen.Select(p => p.UserId).Distinct().ToList();
        var names = await _context.Users.AsNoTracking()
            .Where(u => owners.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.UserName })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.UserName ?? "A rival");
        var sheets = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            var save = await MarchingArmy.LoadPlayerSaveAsync(_context, _logger, gameInstanceId, owner);
            sheets[owner] = save?.Characters?
                .Where(c => c?.Id != null && !string.IsNullOrEmpty(c.SpriteLibraryAssetLocation))
                .GroupBy(c => c.Id)
                .ToDictionary(g => g.Key, g => g.First().SpriteLibraryAssetLocation, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        string? InSight(string? siteId) =>
            siteId != null && sight.Contains(SiteSpec.RegionIdOf(siteId)) ? siteId : null;

        var companies = seen.Select(p =>
        {
            var journey = JourneyOf(p);
            if (journey != null)
                journey = journey with
                {
                    FromSiteId = InSight(journey.FromSiteId),
                    ToSiteId = InSight(journey.ToSiteId),
                    Legs = journey.Legs.Where(l => sight.Contains(l.RegionId)).ToList(),
                };
            var looks = MarchingArmy.ReadIds(p.CharacterIdsJson)
                .Select(id => sheets[p.UserId].TryGetValue(id, out var sheet) ? sheet : null)
                .Where(s => s != null).Select(s => s!).ToList();
            return new RivalCompanyDto(p.Id, p.Name, p.Banner, p.State, p.RegionId, p.SiteId, journey,
                p.UserId, names.TryGetValue(p.UserId, out var name) ? name : "A rival", looks);
        }).ToList();

        return (new PartyOutcome(PartyError.None), new RivalCompaniesResponse(companies, now));
    }

    public async Task<(PartyOutcome Outcome, PartiesResponse? Response)> CreateAsync(
        Guid gameInstanceId, string userId, PartyCreateRequest request)
    {
        if (request.SharedContractVersion != SharedContract.Version)
            return (Mismatch, null);

        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null) return (new PartyOutcome(PartyError.WorldNotFound, "This realm has no world yet."), null);

        var parties = await EnsureFirstAsync(gameInstanceId, userId, world);
        var standing = await _tavern.RosterStandingAsync(gameInstanceId, userId);
        if (!CompanyRules.CanForm(parties.Count, standing.Cap))
        {
            return (new PartyOutcome(PartyError.TooManyCompanies,
                $"You can lead {CompanyRules.MaxCompanies(standing.Cap)} companies with a roster of {standing.Cap}. " +
                "A larger roster - more land, or slots bought at the Tavern - lets you form another."), null);
        }

        int ordinal = parties.Count + 1;
        var party = new PlayerParty
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            Name = CompanyRules.DefaultName(ordinal),
            Banner = CompanyRules.DefaultBanner(ordinal),
            SortOrder = parties.Count == 0 ? 0 : parties.Max(p => p.SortOrder) + 1,
        };
        StandAtCapital(party, world, userId);

        var check = await ApplyAsync(party, request.Name, request.Banner, request.CharacterIds, parties, world, gameInstanceId, userId);
        if (!check.Succeeded) return (check, null);

        _context.PlayerParties.Add(party);
        await _context.SaveChangesAsync();
        _sessionLog.Log("PARTY-FORM", $"user={userId} party={party.Id} name=\"{party.Name}\" members={party.CharacterIdsJson}");
        return (check, await ResponseAsync(gameInstanceId, userId, world));
    }

    public async Task<(PartyOutcome Outcome, PartiesResponse? Response)> UpdateAsync(
        Guid gameInstanceId, string userId, Guid partyId, PartyUpdateRequest request)
    {
        if (request.SharedContractVersion != SharedContract.Version)
            return (Mismatch, null);

        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null) return (new PartyOutcome(PartyError.WorldNotFound, "This realm has no world yet."), null);

        var parties = await EnsureFirstAsync(gameInstanceId, userId, world);
        var party = parties.FirstOrDefault(p => p.Id == partyId);
        if (party == null) return (new PartyOutcome(PartyError.PartyNotFound, "No such company."), null);

        // A company on the road keeps who it set out with; its name and colours may still change.
        if (request.CharacterIds != null && party.AutoMode)
            return (new PartyOutcome(PartyError.Busy, $"{party.Name} is in auto mode. Take it out of auto mode to change who marches with it."), null);
        if (request.CharacterIds != null && party.State != CompanyState.Idle)
            return (new PartyOutcome(PartyError.Busy, $"{party.Name} is away. Change who marches with it when it is at rest."), null);

        var check = await ApplyAsync(party, request.Name, request.Banner, request.CharacterIds, parties, world, gameInstanceId, userId);
        if (!check.Succeeded) return (check, null);

        party.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        _sessionLog.Log("PARTY-SET", $"user={userId} party={party.Id} name=\"{party.Name}\" members={party.CharacterIdsJson}");
        return (check, await ResponseAsync(gameInstanceId, userId, world));
    }

    /// <summary>
    /// Disbands a company; its members are simply free. A player always keeps one, and a company that
    /// is away - on the road, in a fight, stationed - comes home before it can be disbanded.
    /// </summary>
    public async Task<(PartyOutcome Outcome, PartiesResponse? Response)> DisbandAsync(
        Guid gameInstanceId, string userId, Guid partyId)
    {
        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null) return (new PartyOutcome(PartyError.WorldNotFound, "This realm has no world yet."), null);

        var parties = await EnsureFirstAsync(gameInstanceId, userId, world);
        var party = parties.FirstOrDefault(p => p.Id == partyId);
        if (party == null) return (new PartyOutcome(PartyError.PartyNotFound, "No such company."), null);
        if (parties.Count <= 1) return (new PartyOutcome(PartyError.LastCompany, "You always lead at least one company."), null);
        if (party.AutoMode)
            return (new PartyOutcome(PartyError.Busy, $"{party.Name} is in auto mode. Take it out of auto mode first."), null);
        if (party.State != CompanyState.Idle)
            return (new PartyOutcome(PartyError.Busy, "A company can only be disbanded when it is at rest."), null);

        _context.PlayerParties.Remove(party);
        await _context.SaveChangesAsync();
        _sessionLog.Log("PARTY-DISBAND", $"user={userId} party={party.Id} name=\"{party.Name}\"");
        return (new PartyOutcome(PartyError.None), await ResponseAsync(gameInstanceId, userId, world));
    }

    /// <summary>
    /// Sends a company to a site in the region it stands in (§3). The server finds the route on the
    /// region's roads (<see cref="RegionRoadNetwork"/>, the one the client paints), times it
    /// (<see cref="TravelRules"/>: one to five minutes) and stamps it. Nothing ticks: the company is
    /// noticed to have arrived by the first read after it does.
    /// </summary>
    public async Task<(PartyOutcome Outcome, PartiesResponse? Response)> TravelAsync(
        Guid gameInstanceId, string userId, Guid partyId, PartyTravelRequest request)
    {
        if (request.SharedContractVersion != SharedContract.Version)
            return (Mismatch, null);

        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null) return (new PartyOutcome(PartyError.WorldNotFound, "This realm has no world yet."), null);

        var parties = await EnsureFirstAsync(gameInstanceId, userId, world);
        var party = parties.FirstOrDefault(p => p.Id == partyId);
        if (party == null) return (new PartyOutcome(PartyError.PartyNotFound, "No such company."), null);
        if (party.AutoMode)
            return (new PartyOutcome(PartyError.Busy, $"{party.Name} is in auto mode and goes where it chooses. Take it out of auto mode to lead it."), null);
        if (party.State != CompanyState.Idle)
            return (new PartyOutcome(PartyError.Busy, $"{party.Name} is already on the move."), null);

        var members = MarchingArmy.ReadIds(party.CharacterIdsJson);
        if (members.Count == 0) return (new PartyOutcome(PartyError.Empty, $"{party.Name} has nobody in it to send."), null);

        // Anyone tied up since they joined stays behind as far as the rules go: a company does not
        // march half-garrisoned.
        var commitments = await CommitmentsAsync(_context, world, gameInstanceId, userId);
        var committed = members.FirstOrDefault(commitments.ContainsKey);
        if (committed != null)
            return (new PartyOutcome(PartyError.CharacterCommitted,
                $"{committed} is {Describe(commitments[committed])}; take them out of {party.Name} first."), null);

        var regionId = SiteSpec.RegionIdOf(request.SiteId);
        if (string.IsNullOrEmpty(party.RegionId) || string.IsNullOrEmpty(party.SiteId))
            return (new PartyOutcome(PartyError.NoRoute, $"{party.Name} has nowhere to set out from."), null);
        if (string.Equals(request.SiteId, party.SiteId, StringComparison.Ordinal))
            return (new PartyOutcome(PartyError.AlreadyThere, $"{party.Name} is already there."), null);

        var regions = WorldRegionBlob.ReadAllRegions(world).ToList();
        var region = regions.FirstOrDefault(r => r.RegionId == regionId);
        var home = regions.FirstOrDefault(r => r.RegionId == party.RegionId);
        if (region == null || home == null) return (new PartyOutcome(PartyError.SiteNotFound, "That region is not in this world."), null);

        var to = RegionGenerator.Generate(region).FindSite(request.SiteId);
        if (to == null) return (new PartyOutcome(PartyError.SiteNotFound, "There is no such place in this region."), null);
        var from = RegionGenerator.Generate(home).FindSite(party.SiteId);
        if (from == null) return (new PartyOutcome(PartyError.NoRoute, $"{party.Name} has nowhere to set out from."), null);

        // Across the world, region by region, each walked on its own roads (§6, phase 4).
        var byHex = regions.GroupBy(r => r.Hex).ToDictionary(g => g.Key, g => g.First());
        WorldRegionData? RegionAt(HexCoord hex) => byHex.TryGetValue(hex, out var r) ? r : null;
        var route = TravelRules.PlanRoute(RegionAt, home, from.Cell, region, to.Cell);
        if (route == null)
            return (new PartyOutcome(PartyError.NoRoute, "No road or open ground leads there from where the company stands."), null);

        var now = DateTime.UtcNow;
        party.State = CompanyState.Travelling;
        party.FromSiteId = party.SiteId;
        party.RegionId = home.RegionId;   // where it is now; settled forward leg by leg as it walks
        party.ToSiteId = to.SiteId;
        party.SiteId = null;
        party.DepartedAt = now;
        party.ArrivesAt = now + route.Duration;
        party.RouteJson = JsonSerializer.Serialize(route.Legs);
        party.UpdatedAt = now;

        // The road is rolled now, once (§4): re-reading the journey cannot re-roll it, and the client
        // is not told until it strikes. Every region walked is a chance of its own, and one of the
        // player's companies patrolling it makes it safer (auto-fight.md §6).
        var byId = regions.ToDictionary(r => r.RegionId);
        var patrolled = parties.Where(p => p.AutoMode && AutoFightRules.Guards(p.AutoOrder, p.AutoStatus) && p.AutoRegionId != null)
            .Select(p => p.AutoRegionId!).ToHashSet(StringComparer.Ordinal);
        double chance = AmbushRules.ChanceForRoute(route.Legs.Select(leg =>
        {
            var land = byId[leg.RegionId];
            var walk = TimeSpan.FromSeconds(leg.Seconds.Count > 0 ? leg.Seconds[^1] - leg.Seconds[0] : 0);
            return (land.Tier, land.IsOwnedByPlayer(userId), walk, patrolled.Contains(leg.RegionId));
        }));
        party.AmbushAt = AmbushRules.Roll(chance, Dice);
        party.HaltedAt = null;
        party.AmbushRunId = null;
        party.AmbushRunStartedAt = null;
        await _context.SaveChangesAsync();

        _sessionLog.Log("PARTY-TRAVEL",
            $"user={userId} party={party.Id} {party.FromSiteId}->{party.ToSiteId} regions={route.Legs.Count} secs={route.Duration.TotalSeconds:F0} " +
            $"ambush-chance={chance:F2} ambush={(party.AmbushAt.HasValue ? party.AmbushAt.Value.ToString("F2") : "none")}");
        if (_firstSteps != null) await _firstSteps.RecordAsync(gameInstanceId, userId, FirstStepsRules.March);
        return (new PartyOutcome(PartyError.None), await ResponseAsync(gameInstanceId, userId, world));
    }

    /// <summary>
    /// The company of this player's standing at <paramref name="siteId"/>, at rest, or null. PvE begin
    /// asks this: you fight where you stand. Settles arrivals first.
    /// </summary>
    public static async Task<PlayerParty?> CompanyAtAsync(ApplicationDbContext context, Guid gameInstanceId,
        string userId, Guid partyId, string siteId)
    {
        var party = await context.PlayerParties
            .FirstOrDefaultAsync(p => p.Id == partyId && p.GameInstanceId == gameInstanceId && p.UserId == userId);
        if (party == null || party.AutoMode) return null;
        if (SettleArrival(party, DateTime.UtcNow)) await context.SaveChangesAsync();
        return party.State == CompanyState.Idle && string.Equals(party.SiteId, siteId, StringComparison.Ordinal)
            ? party
            : null;
    }

    /// <summary>
    /// Settles a company on the road by the clock: halts it where its ambush strikes, or lands it where
    /// it was going (or back where it set out, if it turned round). Returns whether anything changed.
    /// </summary>
    private static bool SettleArrival(PlayerParty party, DateTime now)
    {
        // A company in auto mode walks by its own replay (AutoFightService), which lands it itself.
        if (party.AutoMode) return false;
        if (party.State is not (CompanyState.Travelling or CompanyState.Returning)
            || party.DepartedAt == null || party.ArrivesAt == null)
            return false;

        if (party.State == CompanyState.Travelling && party.AmbushAt is double share)
        {
            var strikes = party.DepartedAt.Value + TimeSpan.FromTicks((long)((party.ArrivesAt.Value - party.DepartedAt.Value).Ticks * share));
            if (strikes <= now)
            {
                party.State = CompanyState.Ambushed;
                party.HaltedAt = strikes;
                party.RegionId = RegionAlong(party, strikes) ?? party.RegionId;
                party.UpdatedAt = now;
                return true;
            }
        }

        if (party.ArrivesAt > now)
        {
            // Still walking: it is in whichever region it has reached, which the view lists it under.
            var along = RegionAlong(party, now);
            if (along == null || along == party.RegionId) return false;
            party.RegionId = along;
            party.UpdatedAt = now;
            return true;
        }

        party.State = CompanyState.Idle;
        party.SiteId = party.ToSiteId;
        if (!string.IsNullOrEmpty(party.ToSiteId)) party.RegionId = SiteSpec.RegionIdOf(party.ToSiteId);
        party.AmbushAt = null;
        party.HaltedAt = null;
        party.AmbushRunId = null;
        party.AmbushRunStartedAt = null;
        party.FromSiteId = null;
        party.ToSiteId = null;
        party.DepartedAt = null;
        party.ArrivesAt = null;
        party.RouteJson = null;
        party.UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// A journey's route as stored: its legs, region by region (<see cref="RouteLeg"/>). A route written
    /// before 1.35.0 (one region's cells, no legs) reads as none; the company still lands on time.
    /// </summary>
    internal static List<RouteLeg> LegsOf(PlayerParty p)
    {
        if (string.IsNullOrEmpty(p.RouteJson) || !p.RouteJson.TrimStart().StartsWith("[")) return new List<RouteLeg>();
        try { return JsonSerializer.Deserialize<List<RouteLeg>>(p.RouteJson) ?? new List<RouteLeg>(); }
        catch (JsonException) { return new List<RouteLeg>(); }
    }

    /// <summary>The region a company on the road is in at <paramref name="at"/>, or null if its route is unknown.</summary>
    internal static string? RegionAlong(PlayerParty p, DateTime at)
    {
        if (p.DepartedAt == null) return null;
        var legs = LegsOf(p);
        int leg = RoutePlan.LegAt(legs, (at - p.DepartedAt.Value).TotalSeconds);
        return leg < 0 ? null : legs[leg].RegionId;
    }

    private static JourneyDto? JourneyOf(PlayerParty p)
    {
        if (p.State is not (CompanyState.Travelling or CompanyState.Returning or CompanyState.Ambushed)
            || p.DepartedAt == null || p.ArrivesAt == null || p.RouteJson == null)
            return null;
        return new JourneyDto(p.FromSiteId ?? "", p.ToSiteId ?? "",
            DateTime.SpecifyKind(p.DepartedAt.Value, DateTimeKind.Utc), DateTime.SpecifyKind(p.ArrivesAt.Value, DateTimeKind.Utc),
            LegsOf(p),
            p.HaltedAt is DateTime halted ? DateTime.SpecifyKind(halted, DateTimeKind.Utc) : null);
    }

    // -----------------------------------------------------------------
    // Ambushes (§4, phase 3)
    // -----------------------------------------------------------------

    /// <summary>
    /// Opens a run against the warband that has halted a company. The company stays halted - if the
    /// player never finishes the fight they can open it again, or flee.
    /// </summary>
    public async Task<(PartyOutcome Outcome, AmbushFightResponse? Response)> FightAmbushAsync(
        Guid gameInstanceId, string userId, Guid partyId, AmbushOrderRequest request)
    {
        if (request.SharedContractVersion != SharedContract.Version) return (Mismatch, null);

        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null) return (new PartyOutcome(PartyError.WorldNotFound, "This realm has no world yet."), null);

        var parties = await EnsureFirstAsync(gameInstanceId, userId, world);
        var party = parties.FirstOrDefault(p => p.Id == partyId);
        if (party == null) return (new PartyOutcome(PartyError.PartyNotFound, "No such company."), null);
        if (party.State != CompanyState.Ambushed)
            return (new PartyOutcome(PartyError.NotAmbushed, $"Nobody has {party.Name} halted."), null);

        var ambush = AmbushOf(party, world);
        if (ambush == null) return (new PartyOutcome(PartyError.SiteNotFound, "The road it was on is no longer in this world."), null);

        var fighters = MarchingArmy.ReadIds(party.CharacterIdsJson);
        var why = await WhyCannotFightAsync(_context, _logger, world, gameInstanceId, userId, fighters);
        if (why != null) return (new PartyOutcome(PartyError.CharacterCommitted, why), null);

        var now = DateTime.UtcNow;
        var run = new PveRun
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            // Not a site: nothing is conquered, and a PvE claim naming it finds no site and closes it.
            LocationId = $"ambush:{party.Id}",
            LocationType = -1,
            FighterIdsJson = MarchingArmy.WriteIds(fighters),
            StartedAt = now,
        };
        _context.PveRuns.Add(run);

        party.AmbushRunId = run.Id;
        party.AmbushRunStartedAt = now;
        party.UpdatedAt = now;
        await _context.SaveChangesAsync();

        _sessionLog.Log("AMBUSH-FIGHT", $"user={userId} party={party.Id} run={run.Id} level={ambush.Level} waves={ambush.Waves}");
        return (new PartyOutcome(PartyError.None),
            new AmbushFightResponse(run.Id, ambush, await ResponseAsync(gameInstanceId, userId, world)));
    }

    /// <summary>Turns a halted company back the way it came (Mike: fight or flee).</summary>
    public async Task<(PartyOutcome Outcome, PartiesResponse? Response)> FleeAmbushAsync(
        Guid gameInstanceId, string userId, Guid partyId, AmbushOrderRequest request)
    {
        if (request.SharedContractVersion != SharedContract.Version) return (Mismatch, null);

        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null) return (new PartyOutcome(PartyError.WorldNotFound, "This realm has no world yet."), null);

        var parties = await EnsureFirstAsync(gameInstanceId, userId, world);
        var party = parties.FirstOrDefault(p => p.Id == partyId);
        if (party == null) return (new PartyOutcome(PartyError.PartyNotFound, "No such company."), null);
        if (party.State != CompanyState.Ambushed)
            return (new PartyOutcome(PartyError.NotAmbushed, $"Nobody has {party.Name} halted."), null);

        TurnBack(party, DateTime.UtcNow);
        await _context.SaveChangesAsync();

        _sessionLog.Log("AMBUSH-FLEE", $"user={userId} party={party.Id} back-to={party.ToSiteId}");
        return (new PartyOutcome(PartyError.None), await ResponseAsync(gameInstanceId, userId, world));
    }

    /// <summary>
    /// Settles an ambush fight. Won: half of a tier-1 run at the land's level, and the company marches
    /// on. Lost: nothing, and the company turns back. Neither conquers, recruits or restores anything.
    /// </summary>
    public async Task<(PartyOutcome Outcome, AmbushClaimResponse? Response)> ClaimAmbushAsync(
        Guid gameInstanceId, string userId, Guid partyId, AmbushClaimRequest request)
    {
        if (request.SharedContractVersion != SharedContract.Version) return (Mismatch, null);

        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null) return (new PartyOutcome(PartyError.WorldNotFound, "This realm has no world yet."), null);

        var parties = await EnsureFirstAsync(gameInstanceId, userId, world);
        var party = parties.FirstOrDefault(p => p.Id == partyId);
        if (party == null) return (new PartyOutcome(PartyError.PartyNotFound, "No such company."), null);
        if (party.State != CompanyState.Ambushed || party.AmbushRunId != request.RunId)
            return (new PartyOutcome(PartyError.RunNotFound, "That fight is not this company's."), null);

        var run = await _context.PveRuns.FirstOrDefaultAsync(r =>
            r.Id == request.RunId && r.GameInstanceId == gameInstanceId && r.UserId == userId && r.ClaimedAt == null);
        if (run == null) return (new PartyOutcome(PartyError.RunNotFound, "That fight has already been settled."), null);

        var now = DateTime.UtcNow;
        var elapsed = now - run.StartedAt;
        if (request.Won && elapsed < WorldPveService.MinimumRunDuration)
            return (new PartyOutcome(PartyError.RunTooFast, "That fight ended implausibly fast."), null);

        var ambush = AmbushOf(party, world);
        run.ClaimedAt = now;
        party.AmbushRunId = null;
        party.AmbushRunStartedAt = null;

        long experience = 0, gold = 0, balance = 0;
        var items = new List<StateManagement.Models.ItemSaveData>();
        var materials = new List<MaterialGrant>();
        bool won = request.Won && elapsed <= WorldPveService.RunExpiry && ambush != null;

        if (won)
        {
            // Priced as the smallest run there is, at the land's level, and paid at a share: a road
            // skirmish is not a dungeon.
            var full = RunRewardCalculator.Calculate(ambush!.Level, AmbushRules.SkirmishTier,
                _content.RunTuning, _content.DroppableItems, Dice);
            experience = (long)Math.Round(full.Experience * AmbushRules.RewardShare);
            gold = GoldRules.GoldForClear(experience);
            items = full.Items.Where(_ => Dice.NextDouble() < AmbushRules.RewardShare).ToList();
            materials = MaterialRewardCalculator.Calculate(ambush.Level, AmbushRules.SkirmishTier,
                    _content.RunTuning, _content.Materials, Dice)
                .Select(m => new MaterialGrant { MaterialName = m.MaterialName, Quantity = AmbushRules.Share(m.Quantity, Dice) })
                .Where(m => m.Quantity > 0)
                .ToList();

            await _wallet.GrantAsync(gameInstanceId, userId, materials, $"ambush run={run.Id}");
            await _items.GrantAsync(gameInstanceId, userId, items, $"ambush run={run.Id}");
            balance = await _gold.GrantAsync(gameInstanceId, userId, gold, $"ambush run={run.Id}");

            // On it goes, from where it stood. It will not be stopped twice on one road.
            var shift = now - (party.HaltedAt ?? now);
            party.DepartedAt += shift;
            party.ArrivesAt += shift;
            party.AmbushAt = null;
            party.HaltedAt = null;
            party.State = CompanyState.Travelling;
        }
        else
        {
            TurnBack(party, now);
        }

        party.UpdatedAt = now;
        await _context.SaveChangesAsync();

        _sessionLog.Log("AMBUSH-CLAIM",
            $"user={userId} party={party.Id} run={run.Id} won={won} xp={experience} gold={gold} items={items.Count} materials={materials.Sum(m => m.Quantity)}");
        return (new PartyOutcome(PartyError.None), new AmbushClaimResponse(
            won, experience, items, materials, gold, balance, await ResponseAsync(gameInstanceId, userId, world)));
    }

    /// <summary>
    /// Sends a halted company back where it set out, over every cell it walked - across each border it
    /// crossed - as fast as it came.
    /// </summary>
    internal static void TurnBack(PlayerParty party, DateTime now)
    {
        double halted = party.DepartedAt == null ? 0 : ((party.HaltedAt ?? now) - party.DepartedAt.Value).TotalSeconds;
        var back = AmbushRules.RouteBack(LegsOf(party), halted);
        var lastLeg = back.Count > 0 ? back[^1] : null;
        double walk = lastLeg != null && lastLeg.Seconds.Count > 0 ? lastLeg.Seconds[^1] : 0;

        var origin = party.FromSiteId;
        party.State = CompanyState.Returning;
        party.FromSiteId = party.ToSiteId;   // turned back from the road to here
        party.ToSiteId = origin;
        party.DepartedAt = now;
        party.ArrivesAt = now + TimeSpan.FromSeconds(Math.Max(5, walk));
        party.RouteJson = JsonSerializer.Serialize(back);
        if (back.Count > 0) party.RegionId = back[0].RegionId;
        party.AmbushAt = null;
        party.HaltedAt = null;
        party.AmbushRunId = null;
        party.AmbushRunStartedAt = null;
    }

    /// <summary>
    /// The warband that has a company halted, or null when it is not ambushed. Fought at the level of
    /// the land it happened in, as the smallest run there is: a tier-1 site's waves, no boss. In the
    /// region it was bound for, that is its destination's level; in a region it was passing through,
    /// the level of that region's sites. The site named is one in that region, which is what gives
    /// the fight its biome and brings the player back there after.
    /// </summary>
    private static AmbushDto? AmbushOf(PlayerParty p, JsonNode world)
    {
        if (p.State != CompanyState.Ambushed || string.IsNullOrEmpty(p.ToSiteId)) return null;
        var destination = WorldRegionBlob.ResolveSite(world, p.ToSiteId);
        if (destination == null) return null;

        var regionId = p.HaltedAt is DateTime halted ? RegionAlong(p, halted) : null;
        if (regionId == null || regionId == destination.Region.RegionId)
            return new AmbushDto(p.ToSiteId, Math.Max(1, destination.Site.Level), AmbushRules.SkirmishTier, SkirmishWaves);

        var region = WorldRegionBlob.ReadAllRegions(world).FirstOrDefault(r => r.RegionId == regionId);
        var sites = region == null ? null : RegionGenerator.Generate(region).Sites;
        if (sites == null || sites.Count == 0)
            return new AmbushDto(p.ToSiteId, Math.Max(1, destination.Site.Level), AmbushRules.SkirmishTier, SkirmishWaves);

        var keep = sites.FirstOrDefault(s => s.Type == LocationType.Castle) ?? sites[0];
        int level = (int)Math.Round(sites.Average(s => Math.Max(1, s.Level)));
        return new AmbushDto(keep.SiteId, Math.Max(1, level), AmbushRules.SkirmishTier, SkirmishWaves);
    }

    /// <summary>The waves a tier-1 site is fought with (SurvivalData's WavesRequiredTier1).</summary>
    private const int SkirmishWaves = 3;

    private static PartyOutcome Mismatch => new(PartyError.ContractMismatch, "Your game is running different rules from the server.");

    /// <summary>Validates and applies a name, banner and member list to <paramref name="party"/>; nothing is changed on a refusal.</summary>
    private async Task<PartyOutcome> ApplyAsync(PlayerParty party, string? name, string? banner, List<string>? characterIds,
        List<PlayerParty> parties, JsonNode world, Guid gameInstanceId, string userId)
    {
        string? newName = null;
        if (name != null)
        {
            newName = name.Trim();
            if (newName.Length == 0 || newName.Length > CompanyRules.MaxNameLength)
                return new PartyOutcome(PartyError.BadName, $"A company's name is 1 to {CompanyRules.MaxNameLength} characters.");
        }

        if (banner != null && !CompanyRules.IsBanner(banner))
            return new PartyOutcome(PartyError.BadBanner, "A banner is a colour, #rrggbb.");

        List<string>? members = null;
        if (characterIds != null)
        {
            members = characterIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
            if (members.Count > CompanyRules.MaxSize)
                return new PartyOutcome(PartyError.TooManyMembers, $"A company is at most {CompanyRules.MaxSize}.");

            var refusal = await CheckMembersAsync(members, party, parties, world, gameInstanceId, userId);
            if (!refusal.Succeeded) return refusal;
        }

        if (newName != null) party.Name = newName;
        if (banner != null) party.Banner = banner.ToLowerInvariant();
        if (members != null) party.CharacterIdsJson = MarchingArmy.WriteIds(members);
        return new PartyOutcome(PartyError.None);
    }

    private async Task<PartyOutcome> CheckMembersAsync(List<string> members, PlayerParty party,
        List<PlayerParty> parties, JsonNode world, Guid gameInstanceId, string userId)
    {
        if (members.Count == 0) return new PartyOutcome(PartyError.None);

        var save = await MarchingArmy.LoadPlayerSaveAsync(_context, _logger, gameInstanceId, userId);
        if (save == null) return new PartyOutcome(PartyError.NoRoster, "You have no roster in this realm yet.");

        var owned = save.Characters.Where(c => c?.Id != null).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var stranger = members.FirstOrDefault(id => !owned.Contains(id));
        if (stranger != null) return new PartyOutcome(PartyError.NotYourCharacter, $"{stranger} is not one of your characters.");

        var commitments = await CommitmentsAsync(_context, world, gameInstanceId, userId);
        foreach (var id in members)
            if (commitments.TryGetValue(id, out var why))
                return new PartyOutcome(PartyError.CharacterCommitted, $"{NameOf(save, id)} is {Describe(why)}.");

        foreach (var other in parties)
        {
            if (other.Id == party.Id) continue;
            var theirs = MarchingArmy.ReadIds(other.CharacterIdsJson);
            var shared = members.FirstOrDefault(theirs.Contains);
            if (shared != null)
                return new PartyOutcome(PartyError.InAnotherCompany, $"{NameOf(save, shared)} already marches with {other.Name}.");
        }
        return new PartyOutcome(PartyError.None);
    }

    /// <summary>
    /// Everything that ties a player's characters up, and why: stationed in a garrison, held prisoner,
    /// or locked into a siege army. <b>The</b> question every path that takes characters asks - company
    /// membership, and the fighters a run opens with.
    /// </summary>
    public static async Task<Dictionary<string, (string Reason, string? SiteId)>> CommitmentsAsync(
        ApplicationDbContext context, JsonNode? world, Guid gameInstanceId, string userId)
    {
        var all = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        foreach (var pair in WorldRegionBlob.CollectCommitments(world, userId))
            all[pair.Key] = (pair.Value.Why == WorldRegionBlob.Commitment.Garrisoned ? "garrisoned" : "held", pair.Value.SiteId);
        foreach (var id in await MarchingArmy.SiegeLockedIdsAsync(context, gameInstanceId, userId))
            all.TryAdd(id, ("siege", null));
        return all;
    }

    /// <summary>
    /// Why the characters asked to fight cannot, or null if they all can: each must be the player's own
    /// and not garrisoned, held or in a siege. PvE begin asks this; before 1.32.0 it asked nothing, and a
    /// sieging army could slip off and run dungeons.
    /// </summary>
    public static async Task<string?> WhyCannotFightAsync(ApplicationDbContext context, ILogger logger,
        JsonNode? world, Guid gameInstanceId, string userId, IReadOnlyCollection<string> fighters)
    {
        if (fighters.Count == 0) return "Nobody is going in.";
        if (fighters.Count > CompanyRules.MaxSize) return $"A company is at most {CompanyRules.MaxSize}.";

        var save = await MarchingArmy.LoadPlayerSaveAsync(context, logger, gameInstanceId, userId);
        var owned = save?.Characters?.Where(c => c?.Id != null).Select(c => c.Id).ToHashSet(StringComparer.Ordinal)
                    ?? new HashSet<string>(StringComparer.Ordinal);

        var commitments = await CommitmentsAsync(context, world, gameInstanceId, userId);
        var bloodied = await AutoFightService.BloodiedAsync(context, gameInstanceId, userId, DateTime.UtcNow);
        foreach (var id in fighters)
        {
            if (!owned.Contains(id)) return $"{id} is not one of your characters.";
            if (commitments.TryGetValue(id, out var why)) return $"{NameOf(save, id)} is {Describe(why)}.";
            if (bloodied.TryGetValue(id, out var recovers))
                return $"{NameOf(save, id)} is Bloodied and cannot fight for another {Minutes(recovers - DateTime.UtcNow)}.";
        }
        return null;
    }

    /// <summary>
    /// Takes characters out of whichever of this player's companies they are in: they have been
    /// stationed in a garrison, which wins. Saves.
    /// </summary>
    public static async Task ReleaseAsync(ApplicationDbContext context, Guid gameInstanceId, string userId, IEnumerable<string> characterIds)
    {
        var ids = characterIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return;

        var parties = await context.PlayerParties.Where(p => p.GameInstanceId == gameInstanceId && p.UserId == userId).ToListAsync();
        bool changed = false;
        foreach (var party in parties)
        {
            var members = MarchingArmy.ReadIds(party.CharacterIdsJson);
            if (members.RemoveAll(ids.Contains) == 0) continue;
            party.CharacterIdsJson = MarchingArmy.WriteIds(members);
            party.UpdatedAt = DateTime.UtcNow;
            changed = true;
        }
        if (changed) await context.SaveChangesAsync();
    }

    /// <summary>A recovery's time left, as the refusal says it: "12 min".</summary>
    private static string Minutes(TimeSpan left) => $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";

    private static string Describe((string Reason, string? SiteId) why) => why.Reason switch
    {
        "garrisoned" => "stationed in a garrison",
        "held" => "held prisoner",
        "siege" => "marching with a siege army",
        _ => "committed elsewhere",
    };

    private static string NameOf(StateManagement.Models.UserSaveData? save, string id)
    {
        // A starter nobody named is called what the client calls it, so a refusal names a person.
        var character = save?.Characters?.FirstOrDefault(c => c?.Id == id);
        return MuggaLuggaTD.Shared.World.Naming.ForCharacter(character?.CharacterName, character?.LinkName, id) ?? id;
    }

    /// <summary>
    /// This player's companies in order, forming the first if there are none: the save's active party,
    /// less anyone committed elsewhere, standing at their capital's keep.
    /// </summary>
    private async Task<List<PlayerParty>> EnsureFirstAsync(Guid gameInstanceId, string userId, JsonNode world)
    {
        var parties = await _context.PlayerParties
            .Where(p => p.GameInstanceId == gameInstanceId && p.UserId == userId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.CreatedAt)
            .ToListAsync();
        if (parties.Count > 0)
        {
            // Arrival is noticed by the first read after it, like a season's end.
            var now = DateTime.UtcNow;
            bool landed = false;
            foreach (var p in parties) landed |= SettleArrival(p, now);
            if (landed) await _context.SaveChangesAsync();
            return parties;
        }

        var save = await MarchingArmy.LoadPlayerSaveAsync(_context, _logger, gameInstanceId, userId);
        var owned = save?.Characters?.Where(c => c?.Id != null).Select(c => c.Id).ToHashSet(StringComparer.Ordinal)
                    ?? new HashSet<string>(StringComparer.Ordinal);
        var commitments = await CommitmentsAsync(_context, world, gameInstanceId, userId);
        var members = (save?.ActiveCharacterIds ?? new List<string>())
            .Where(id => !string.IsNullOrEmpty(id) && owned.Contains(id) && !commitments.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .Take(CompanyRules.MaxSize)
            .ToList();

        var first = new PlayerParty
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            Name = CompanyRules.DefaultName(1),
            Banner = CompanyRules.DefaultBanner(1),
            CharacterIdsJson = MarchingArmy.WriteIds(members),
            SortOrder = 0,
        };
        StandAtCapital(first, world, userId);

        _context.PlayerParties.Add(first);
        await _context.SaveChangesAsync();
        _sessionLog.Log("PARTY-FORM", $"user={userId} party={first.Id} first-company members={first.CharacterIdsJson}");
        return new List<PlayerParty> { first };
    }

    /// <summary>Stands a company at the keep of its player's capital, if they have one.</summary>
    private static void StandAtCapital(PlayerParty party, JsonNode world, string userId)
    {
        var capital = WorldRegionBlob.ReadAllRegions(world).FirstOrDefault(r => r.IsCapital && r.IsOwnedByPlayer(userId));
        if (capital == null) return;

        party.RegionId = capital.RegionId;
        party.SiteId = RegionGenerator.Generate(capital).Sites.FirstOrDefault(s => s.Type == LocationType.Castle)?.SiteId;
    }

    private async Task<PartiesResponse> ResponseAsync(Guid gameInstanceId, string userId, JsonNode world)
    {
        var parties = await _context.PlayerParties.AsNoTracking()
            .Where(p => p.GameInstanceId == gameInstanceId && p.UserId == userId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.CreatedAt)
            .ToListAsync();
        var standing = await _tavern.RosterStandingAsync(gameInstanceId, userId);
        var commitments = await CommitmentsAsync(_context, world, gameInstanceId, userId);
        var now = DateTime.UtcNow;
        var bloodied = await AutoFightService.BloodiedAsync(_context, gameInstanceId, userId, now);

        return new PartiesResponse(
            parties.Select(p => new PartyDto(p.Id, p.Name, p.Banner, MarchingArmy.ReadIds(p.CharacterIdsJson),
                p.SortOrder, p.State, p.RegionId, p.SiteId, JourneyOf(p), AmbushOf(p, world), AutoOf(p))).ToList(),
            CompanyRules.MaxCompanies(standing.Cap),
            CompanyRules.MaxSize,
            standing.Cap,
            commitments.Select(c => new CharacterCommitmentDto(c.Key, c.Value.Reason, c.Value.SiteId)).ToList(),
            now,
            bloodied.Select(b => new BloodiedDto(b.Key, b.Value)).ToList());
    }

    private static AutoStateDto? AutoOf(PlayerParty p)
    {
        if (!p.AutoMode) return null;
        return new AutoStateDto(p.AutoOrder, p.AutoRegionId, p.AutoStatus, p.AutoTargetSiteId,
            p.AutoStepEndsAt is DateTime ends ? DateTime.SpecifyKind(ends, DateTimeKind.Utc) : null);
    }

    private async Task<JsonNode?> LoadWorldAsync(Guid gameInstanceId)
    {
        var row = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        return row == null ? null : JsonNode.Parse(row.GameData);
    }
}
