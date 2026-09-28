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
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<PartyService> _logger;

    public PartyService(ApplicationDbContext context, TavernService tavern, ISessionLog sessionLog, ILogger<PartyService> logger)
    {
        _context = context;
        _tavern = tavern;
        _sessionLog = sessionLog;
        _logger = logger;
    }

    /// <summary>This player's companies, forming the first from their party if they have none yet.</summary>
    public async Task<(PartyOutcome Outcome, PartiesResponse? Response)> ListAsync(Guid gameInstanceId, string userId)
    {
        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null) return (new PartyOutcome(PartyError.WorldNotFound, "This realm has no world yet."), null);

        await EnsureFirstAsync(gameInstanceId, userId, world);
        return (new PartyOutcome(PartyError.None), await ResponseAsync(gameInstanceId, userId, world));
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
        if (party.State != CompanyState.Idle)
            return (new PartyOutcome(PartyError.Busy, "A company can only be disbanded when it is at rest."), null);

        _context.PlayerParties.Remove(party);
        await _context.SaveChangesAsync();
        _sessionLog.Log("PARTY-DISBAND", $"user={userId} party={party.Id} name=\"{party.Name}\"");
        return (new PartyOutcome(PartyError.None), await ResponseAsync(gameInstanceId, userId, world));
    }

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
        foreach (var id in fighters)
        {
            if (!owned.Contains(id)) return $"{id} is not one of your characters.";
            if (commitments.TryGetValue(id, out var why)) return $"{NameOf(save, id)} is {Describe(why)}.";
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

    private static string Describe((string Reason, string? SiteId) why) => why.Reason switch
    {
        "garrisoned" => "stationed in a garrison",
        "held" => "held prisoner",
        "siege" => "marching with a siege army",
        _ => "committed elsewhere",
    };

    private static string NameOf(StateManagement.Models.UserSaveData? save, string id)
    {
        var name = save?.Characters?.FirstOrDefault(c => c?.Id == id)?.CharacterName;
        return string.IsNullOrWhiteSpace(name) ? id : name!;
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
        if (parties.Count > 0) return parties;

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

        return new PartiesResponse(
            parties.Select(p => new PartyDto(p.Id, p.Name, p.Banner, MarchingArmy.ReadIds(p.CharacterIdsJson),
                p.SortOrder, p.State, p.RegionId, p.SiteId)).ToList(),
            CompanyRules.MaxCompanies(standing.Cap),
            CompanyRules.MaxSize,
            standing.Cap,
            commitments.Select(c => new CharacterCommitmentDto(c.Key, c.Value.Reason, c.Value.SiteId)).ToList());
    }

    private async Task<JsonNode?> LoadWorldAsync(Guid gameInstanceId)
    {
        var row = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        return row == null ? null : JsonNode.Parse(row.GameData);
    }
}
