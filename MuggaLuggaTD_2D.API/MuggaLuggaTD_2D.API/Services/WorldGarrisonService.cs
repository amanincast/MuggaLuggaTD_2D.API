using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>Why a garrison or ransom was refused, so the controller can pick a status code.</summary>
public enum GarrisonError
{
    None = 0,
    WorldNotFound,
    SiteNotFound,
    NotYours,
    NotGarrisonable,
    NobodyHeld,
    NotYourPrisoners,
    CannotAfford,
    ContractMismatch
}

public record GarrisonOutcome(GarrisonError Error, string? Message = null)
{
    public bool Succeeded => Error == GarrisonError.None;
}

/// <summary>
/// Stationing characters to hold a region, and getting them back once it is lost.
///
/// <para><b>This exists because the client was writing the world.</b> Garrisoning used to be applied
/// entirely in Unity and persisted by POSTing the old flat <c>{ Locations: [...] }</c> save over the
/// shared blob. On a region world that blob is unreadable, and <see cref="WorldProvisioningService"/>
/// regenerates anything it cannot read — so one player stationing a garrison silently wiped the whole
/// realm's map, every region, for everybody, on the next map open.</para>
///
/// <para><b>And the power was the client's.</b> <c>GarrisonPower</c> was computed in Unity and written
/// straight into the blob, while the server derives hold, the raid bar and the siege encounter from it.
/// A player could set their own region's defensive strength to whatever they liked. It is priced here
/// from the persisted roster now, by the same <see cref="MarchingArmy"/> muster that prices an attack.
/// </para>
///
/// <para><b>Prisoners come home</b> (<see cref="CaptivityRules"/>). A lost siege takes the defenders,
/// which is what makes winning one worth the muster; holding them forever would take the army needed
/// to win the region back, on top of the land and the roster slots already lost. So captivity expires,
/// retaking frees them, and a ransom buys them back now.</para>
/// </summary>
public class WorldGarrisonService
{
    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly GoldService _gold;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<WorldGarrisonService> _logger;

    public WorldGarrisonService(
        ApplicationDbContext context,
        IGameContentProvider content,
        GoldService gold,
        ISessionLog sessionLog,
        ILogger<WorldGarrisonService> logger)
    {
        _context = context;
        _content = content;
        _gold = gold;
        _sessionLog = sessionLog;
        _logger = logger;
    }

    /// <summary>
    /// Sets a site's garrison to exactly the characters the player may actually station there.
    ///
    /// <para>The requested list is filtered, never trusted: ids must belong to this player's persisted
    /// roster and must not already be committed elsewhere — another garrison, a live siege, or a cell.
    /// The site's own current garrison is cleared <i>before</i> the muster, so re-submitting a list that
    /// includes who is already standing there is an ordinary edit rather than a conflict.</para>
    /// </summary>
    public async Task<(GarrisonOutcome Outcome, GarrisonResponse? Response, JsonNode? UpdatedWorld)> SetAsync(
        Guid gameInstanceId, string userId, GarrisonRequest request)
    {
        if (request.SharedContractVersion != MuggaLuggaTD.Shared.SharedContract.Version)
        {
            return (new GarrisonOutcome(GarrisonError.ContractMismatch,
                "Your game is running different rules from the server."), null, null);
        }

        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null)
            return (new GarrisonOutcome(GarrisonError.WorldNotFound, "This realm has no world yet."), null, null);

        var resolved = WorldRegionBlob.ResolveSite(world, request.SiteId);
        if (resolved == null)
            return (new GarrisonOutcome(GarrisonError.SiteNotFound, "No such site."), null, null);

        var region = WorldRegionBlob.ReadRegion(resolved.RegionNode);
        if (!region.IsOwnedByPlayer(userId))
        {
            return (new GarrisonOutcome(GarrisonError.NotYours,
                "You can only garrison a region you hold."), null, null);
        }

        // A garrison defends a holding, so it belongs at one — not at a dungeon that happens to sit
        // inside your borders.
        if (resolved.Site.Type != LocationType.Castle && resolved.Site.Type != LocationType.Outpost)
        {
            return (new GarrisonOutcome(GarrisonError.NotGarrisonable,
                "Only a keep or an outpost can be garrisoned."), null, null);
        }

        // Clear it first, so this site's own defenders do not read as "committed elsewhere" and get
        // filtered out of the very list that is resetting them.
        WorldRegionBlob.SetGarrison(resolved.RegionNode, request.SiteId, Array.Empty<string>(), 0);

        var muster = await MarchingArmy.MusterAsync(
            _context, _content, _logger, gameInstanceId, userId, world, request.CharacterIds);

        WorldRegionBlob.SetGarrison(resolved.RegionNode, request.SiteId, muster.CharacterIds, muster.Power);

        // Re-read, so the hold reported back is the hold that was just written rather than the one
        // before it. Marching power of zero: this is a defence being assessed, not an attack.
        var assessment = RegionHoldCalculator.AssessRegion(
            WorldRegionBlob.ReadRegion(resolved.RegionNode),
            WorldRegionBlob.ReadAllRegions(world),
            marchingPower: 0);

        _sessionLog.Log("GARRISON",
            $"user={userId} site={request.SiteId} asked={request.CharacterIds?.Count ?? 0} " +
            $"stationed={muster.CharacterIds.Count} power={muster.Power:F0} hold={assessment.Hold}");

        return (new GarrisonOutcome(GarrisonError.None),
            new GarrisonResponse(request.SiteId, muster.CharacterIds, muster.Power, assessment.Hold),
            world);
    }

    /// <summary>
    /// Buys prisoners back out of a site that is holding them.
    ///
    /// <para>Only this player's own characters are freed, and only ones still held — a ransom paid a
    /// minute after they walked home on their own would be a charge for nothing. The gold is taken
    /// after the prisoners are known and before the world is written, so a payment that fails leaves
    /// nobody freed and a free that fails leaves nobody charged.</para>
    /// </summary>
    public async Task<(GarrisonOutcome Outcome, RansomResponse? Response, JsonNode? UpdatedWorld)> RansomAsync(
        Guid gameInstanceId, string userId, RansomRequest request)
    {
        if (request.SharedContractVersion != MuggaLuggaTD.Shared.SharedContract.Version)
        {
            return (new GarrisonOutcome(GarrisonError.ContractMismatch,
                "Your game is running different rules from the server."), null, null);
        }

        var world = await LoadWorldAsync(gameInstanceId);
        if (world == null)
            return (new GarrisonOutcome(GarrisonError.WorldNotFound, "This realm has no world yet."), null, null);

        var resolved = WorldRegionBlob.ResolveSite(world, request.SiteId);
        if (resolved == null)
            return (new GarrisonOutcome(GarrisonError.SiteNotFound, "No such site."), null, null);

        var (ids, stamp) = WorldRegionBlob.PrisonersAt(resolved.RegionNode, request.SiteId);

        if (!CaptivityRules.IsHeld(stamp, DateTime.UtcNow) || ids.Count == 0)
        {
            return (new GarrisonOutcome(GarrisonError.NobodyHeld,
                "Nobody is being held here any more."), null, null);
        }

        var mine = await OwnedByAsync(gameInstanceId, userId, ids);
        if (mine.Count == 0)
        {
            return (new GarrisonOutcome(GarrisonError.NotYourPrisoners,
                "None of the prisoners here are yours."), null, null);
        }

        long cost = CaptivityRules.RansomCostGold(mine.Count);
        var payment = await _gold.SpendAsync(gameInstanceId, userId, cost, $"ransom site={request.SiteId}");
        if (!payment.Succeeded)
            return (new GarrisonOutcome(GarrisonError.CannotAfford, payment.Message), null, null);

        int freed = WorldRegionBlob.ReleasePrisoners(resolved.RegionNode, request.SiteId, mine);

        if (freed == 0)
        {
            // Nothing was removed, so nothing should have been charged.
            await _gold.GrantAsync(gameInstanceId, userId, cost, "ransom-refund");
            return (new GarrisonOutcome(GarrisonError.NobodyHeld,
                "Nobody is being held here any more."), null, null);
        }

        long balance = await _gold.BalanceAsync(gameInstanceId, userId);

        _sessionLog.Log("RANSOM",
            $"user={userId} site={request.SiteId} freed={freed} cost={cost} gold={balance}");

        return (new GarrisonOutcome(GarrisonError.None),
            new RansomResponse(request.SiteId, mine, cost, balance),
            world);
    }

    /// <summary>
    /// Which of <paramref name="ids"/> are in this player's persisted roster.
    ///
    /// <para>Read from the save rather than taken on trust, because this is what decides who a player
    /// is allowed to pay to release — otherwise a ransom request could name a rival's prisoners and
    /// hand them back for them.</para>
    /// </summary>
    private async Task<List<string>> OwnedByAsync(Guid gameInstanceId, string userId, IEnumerable<string> ids)
    {
        var save = await MarchingArmy.LoadPlayerSaveAsync(_context, _logger, gameInstanceId, userId);

        var owned = save?.Characters?
            .Where(c => c?.Id != null).Select(c => c!.Id).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

        return ids.Where(id => !string.IsNullOrEmpty(id) && owned.Contains(id))
                  .Distinct(StringComparer.Ordinal)
                  .ToList();
    }

    private async Task<JsonNode?> LoadWorldAsync(Guid gameInstanceId)
    {
        var row = await _context.WorldViewGameData
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);

        return row == null ? null : JsonNode.Parse(row.GameData);
    }
}
