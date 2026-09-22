using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Hubs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>Why a siege action was refused, so the controller can pick the right status code.</summary>
public enum SiegeError
{
    None = 0,
    ContractMismatch,
    WorldNotFound,
    RegionNotFound,

    /// <summary>The shared rules refused it. <see cref="SiegeOutcome.Refusal"/> says which.</summary>
    Refused,

    /// <summary>Someone is already besieging that region. One siege per region (siege.md §7).</summary>
    RegionAlreadyBesieged,

    /// <summary>The attacker is already laying a siege. One per attacker - you have one army.</summary>
    AlreadyBesieging,

    /// <summary>The attacker's last siege on this region ended too recently.</summary>
    OnCooldown,

    NoArmy,
    SiegeNotFound,

    /// <summary>Only the defender may declare ready.</summary>
    NotDefender,

    /// <summary>The siege is past the point where that action means anything.</summary>
    WrongState,

    /// <summary>Only the besieger may assault.</summary>
    NotAttacker,

    /// <summary>The siege's one assault has already been fought.</summary>
    AssaultSpent,

    /// <summary>The claim does not name this siege's assault.</summary>
    RunMismatch,

    /// <summary>The assault was claimed faster than it could have been fought.</summary>
    TooFast
}

public record SiegeOutcome(
    SiegeError Error, SiegeResponse? Siege = null, string? Message = null, SiegeRefusal Refusal = SiegeRefusal.None)
{
    public bool Succeeded => Error == SiegeError.None;
}

/// <summary>
/// Declaring sieges and moving them through their windows (<c>docs/design/siege.md</c> §4).
///
/// <code>
/// DECLARE ──▶ MUSTER (≤ 8h) ──▶ ASSAULT (≤ 12h, attacker must show) ──▶ outcome
///             defender reinforces   hold frozen
///             may declare ready
/// </code>
///
/// <para>A siege is declared and its army locked; the defender musters, and may close the muster
/// early; the hold freezes. Then the attacker gets <b>one</b> assault: a fight the server specifies
/// from the frozen hold. Winning it takes the region wrecked, with its garrison captured; losing it,
/// or never reporting a win, repels the siege. An attacker who never comes lets it lapse.</para>
///
/// <para>Transitions are driven two ways, deliberately. Every read advances whatever is due, so a
/// player never sees a stale state; and <see cref="SiegeScheduler"/> sweeps once a minute, so a
/// region's hold is frozen at the moment muster actually closes rather than whenever somebody next
/// happens to look. Neither depends on the other for correctness.</para>
/// </summary>
public class WorldSiegeService
{
    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly IHubContext<GameHub> _hubContext;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<WorldSiegeService> _logger;
    private readonly TimeProvider _clock;
    private readonly SeasonScoreService _seasons;

    public WorldSiegeService(
        ApplicationDbContext context,
        IGameContentProvider content,
        IHubContext<GameHub> hubContext,
        ISessionLog sessionLog,
        ILogger<WorldSiegeService> logger,
        TimeProvider clock,
        SeasonScoreService seasons)
    {
        _context = context;
        _content = content;
        _hubContext = hubContext;
        _sessionLog = sessionLog;
        _logger = logger;
        _clock = clock;
        _seasons = seasons;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // -----------------------------------------------------------------
    // Declaring
    // -----------------------------------------------------------------

    public async Task<SiegeOutcome> DeclareAsync(Guid gameInstanceId, string attackerUserId, SiegeDeclareRequest request)
    {
        if (!string.Equals(request.SharedContractVersion, MuggaLuggaTD.Shared.SharedContract.Version, StringComparison.Ordinal))
        {
            return new SiegeOutcome(SiegeError.ContractMismatch, Message:
                $"Client gameplay rules v{request.SharedContractVersion} do not match the server's " +
                $"v{MuggaLuggaTD.Shared.SharedContract.Version}. Update the game to lay a siege.");
        }

        var instance = await _context.GameInstances.FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        var worldRow = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (instance == null || worldRow == null)
            return new SiegeOutcome(SiegeError.WorldNotFound, Message: "World view data not found.");

        // Bring everything up to date first, so a siege that has just lapsed no longer counts
        // against "one per region" or "one per attacker".
        await AdvanceAsync(gameInstanceId);

        var world = JsonNode.Parse(worldRow.GameData);
        var regionNode = WorldRegionBlob.FindRegion(world, request.RegionId);
        if (regionNode == null)
            return new SiegeOutcome(SiegeError.RegionNotFound, Message: "Region not found in this world.");

        var region = WorldRegionBlob.ReadRegion(regionNode);
        var allRegions = WorldRegionBlob.ReadAllRegions(world);
        var now = Now;

        // The table-backed rules the shared check cannot see, cheapest first.
        bool regionBesieged = await _context.Sieges.AnyAsync(s =>
            s.GameInstanceId == gameInstanceId && s.RegionId == request.RegionId
            && (s.State == SiegeState.Mustering || s.State == SiegeState.Assault));
        if (regionBesieged)
        {
            return new SiegeOutcome(SiegeError.RegionAlreadyBesieged, Message:
                "That region is already under siege.");
        }

        bool alreadyBesieging = await _context.Sieges.AnyAsync(s =>
            s.GameInstanceId == gameInstanceId && s.AttackerUserId == attackerUserId
            && (s.State == SiegeState.Mustering || s.State == SiegeState.Assault));
        if (alreadyBesieging)
        {
            return new SiegeOutcome(SiegeError.AlreadyBesieging, Message:
                "You are already laying a siege. Your army is committed until it ends.");
        }

        var cooldownEndsAt = await RedeclareAllowedAtAsync(gameInstanceId, attackerUserId, request.RegionId);
        if (cooldownEndsAt > now)
        {
            return new SiegeOutcome(SiegeError.OnCooldown, Message:
                $"Your last siege here ended too recently. You may declare again at {cooldownEndsAt:HH:mm} UTC.");
        }

        var army = await MarchingArmy.MusterAsync(
            _context, _content, _logger, gameInstanceId, attackerUserId, world, request.ArmyCharacterIds);
        if (army.CharacterIds.Count == 0)
        {
            return new SiegeOutcome(SiegeError.NoArmy, Message:
                "None of those champions can march — they may be garrisoned, captured or already committed.");
        }

        var assessment = RegionHoldCalculator.AssessRegion(region, allRegions, army.Power);
        var refusal = SiegeRules.CheckDeclare(
            region, attackerUserId, army.Power, assessment.Hold, now, instance.SeasonEndsAt);

        if (refusal != SiegeRefusal.None)
        {
            string message = refusal == SiegeRefusal.BelowGate
                ? $"Your army of {army.Power:F0} falls short of the siege gate of {assessment.Gate:N0}."
                : SiegeRules.Explain(refusal);
            return new SiegeOutcome(SiegeError.Refused, Message: message, Refusal: refusal);
        }

        var siege = new Siege
        {
            GameInstanceId = gameInstanceId,
            SeasonNumber = instance.SeasonNumber,
            AttackerUserId = attackerUserId,
            DefenderUserId = region.OwnerUserId!,
            RegionId = request.RegionId,
            ArmyCharacterIdsJson = MarchingArmy.WriteIds(army.CharacterIds),
            MarchingPower = army.Power,
            State = SiegeState.Mustering,
            DeclaredAt = now,
            MusterEndsAt = now + SiegeRules.Muster,
            AssaultEndsAt = now + SiegeRules.Muster + SiegeRules.AssaultWindow
        };

        _context.Sieges.Add(siege);
        await _context.SaveChangesAsync();

        _sessionLog.Log("SIEGE-DECLARE",
            $"instance={gameInstanceId} siege={siege.Id} attacker={attackerUserId} defender={siege.DefenderUserId} " +
            $"region={siege.RegionId} army={army.CharacterIds.Count} power={army.Power:F0} " +
            $"hold={assessment.Hold} gate={assessment.Gate} resolve={region.Resolve}");

        var response = await ToResponseAsync(siege, attackerUserId);
        await BroadcastAsync(siege);
        return new SiegeOutcome(SiegeError.None, response);
    }

    /// <summary>
    /// The defender closes the muster early.
    ///
    /// <para>Not self-harm: it pulls the fight forward to a time the defender is awake for, so they
    /// can see how it goes. The hold is frozen at this moment, with whatever they have mustered.</para>
    /// </summary>
    public async Task<SiegeOutcome> DeclareReadyAsync(Guid gameInstanceId, string userId, Guid siegeId)
    {
        await AdvanceAsync(gameInstanceId);

        var siege = await _context.Sieges.FirstOrDefaultAsync(s => s.Id == siegeId && s.GameInstanceId == gameInstanceId);
        if (siege == null)
            return new SiegeOutcome(SiegeError.SiegeNotFound, Message: "No such siege in this realm.");

        if (!string.Equals(siege.DefenderUserId, userId, StringComparison.Ordinal))
            return new SiegeOutcome(SiegeError.NotDefender, Message: "Only the defender may declare ready.");

        if (siege.State != SiegeState.Mustering)
            return new SiegeOutcome(SiegeError.WrongState, Message: "The muster has already closed.");

        var now = Now;
        siege.MusterEndsAt = now;
        siege.AssaultEndsAt = now + SiegeRules.AssaultWindow;
        await _context.SaveChangesAsync();

        _sessionLog.Log("SIEGE-READY", $"instance={gameInstanceId} siege={siege.Id} defender={userId}");

        // Close the muster now rather than waiting for the sweep: the defender asked for it now.
        await AdvanceAsync(gameInstanceId);

        return new SiegeOutcome(SiegeError.None, await ToResponseAsync(siege, userId));
    }

    // -----------------------------------------------------------------
    // Reading
    // -----------------------------------------------------------------

    /// <summary>Every live siege in the realm, as <paramref name="requesterUserId"/> is allowed to see it.</summary>
    public async Task<List<SiegeResponse>> LiveSiegesAsync(Guid gameInstanceId, string requesterUserId)
    {
        await AdvanceAsync(gameInstanceId);

        var sieges = await _context.Sieges
            .Where(s => s.GameInstanceId == gameInstanceId
                        && (s.State == SiegeState.Mustering || s.State == SiegeState.Assault))
            .OrderBy(s => s.DeclaredAt)
            .ToListAsync();

        var result = new List<SiegeResponse>();
        foreach (var siege in sieges)
            result.Add(await ToResponseAsync(siege, requesterUserId));

        return result;
    }

    /// <summary>When this attacker may next declare on this region. In the past when they may now.</summary>
    public async Task<DateTime> RedeclareAllowedAtAsync(Guid gameInstanceId, string attackerUserId, string regionId)
    {
        // Only a siege that ended on the attacker's account counts. A cancelled one ended because the
        // region changed hands under it - not something the attacker did, so it costs them nothing.
        var last = await _context.Sieges
            .Where(s => s.GameInstanceId == gameInstanceId && s.AttackerUserId == attackerUserId
                        && s.RegionId == regionId && s.ResolvedAt != null
                        && (s.State == SiegeState.Lapsed || s.State == SiegeState.Repelled))
            .OrderByDescending(s => s.ResolvedAt)
            .FirstOrDefaultAsync();

        return last?.ResolvedAt == null ? DateTime.MinValue : last.ResolvedAt.Value + SiegeRules.RedeclareCooldown;
    }

    // -----------------------------------------------------------------
    // The assault
    // -----------------------------------------------------------------

    /// <summary>
    /// The attacker marches on the region: the server opens the siege's one assault and hands back
    /// the fight, specified from the hold frozen at muster close (siege.md §5).
    /// </summary>
    public async Task<(SiegeOutcome Outcome, SiegeAssaultResponse? Assault)> BeginAssaultAsync(
        Guid gameInstanceId, string userId, Guid siegeId, string? sharedContractVersion)
    {
        if (!ContractMatches(sharedContractVersion))
            return (ContractMismatch(sharedContractVersion), null);

        await AdvanceAsync(gameInstanceId);

        var siege = await _context.Sieges.FirstOrDefaultAsync(s => s.Id == siegeId && s.GameInstanceId == gameInstanceId);
        if (siege == null)
            return (new SiegeOutcome(SiegeError.SiegeNotFound, Message: "No such siege in this realm."), null);

        if (!string.Equals(siege.AttackerUserId, userId, StringComparison.Ordinal))
            return (new SiegeOutcome(SiegeError.NotAttacker, Message: "Only the besieger may assault."), null);

        if (siege.State == SiegeState.Mustering)
            return (new SiegeOutcome(SiegeError.WrongState, Message: "The defender is still mustering. Wait for the gates."), null);

        if (siege.State != SiegeState.Assault)
            return (new SiegeOutcome(SiegeError.WrongState, Message: "This siege is over."), null);

        if (siege.AssaultRunId != null)
        {
            return (new SiegeOutcome(SiegeError.AssaultSpent, Message:
                "This siege's assault has already been fought. A siege gets one."), null);
        }

        var world = await LoadWorldAsync(gameInstanceId, tracked: false);
        var regionNode = WorldRegionBlob.FindRegion(world, siege.RegionId);
        if (regionNode == null)
            return (new SiegeOutcome(SiegeError.RegionNotFound, Message: "Region not found in this world."), null);

        var region = WorldRegionBlob.ReadRegion(regionNode);
        var army = MarchingArmy.ReadIds(siege.ArmyCharacterIdsJson);
        var encounter = SiegeAssaultRules.EncounterFor(
            siege.MarchingPower, siege.FrozenHold ?? 0, army.Count, GarrisonCountOf(region));

        var now = Now;
        siege.AssaultRunId = Guid.NewGuid();
        siege.AssaultStartedAt = now;
        siege.EncounterEnemyLevel = encounter.EnemyLevel;
        siege.EncounterWaves = encounter.Waves;
        await _context.SaveChangesAsync();

        _sessionLog.Log("SIEGE-ASSAULT",
            $"instance={gameInstanceId} siege={siege.Id} attacker={userId} region={siege.RegionId} " +
            $"run={siege.AssaultRunId} power={siege.MarchingPower:F0} frozenHold={siege.FrozenHold} " +
            $"enemyLevel={encounter.EnemyLevel} waves={encounter.Waves} elites={encounter.EliteCount}");

        await BroadcastAsync(siege);

        var response = new SiegeAssaultResponse(
            siege.Id,
            siege.AssaultRunId.Value,
            siege.RegionId,
            encounter.EnemyLevel,
            encounter.Waves,
            encounter.EliteCount,
            siege.FrozenHold ?? 0,
            Math.Round(siege.MarchingPower),
            siege.AssaultEndsAt + SiegeAssaultRules.ClaimGrace,
            army);

        return (new SiegeOutcome(SiegeError.None, await ToResponseAsync(siege, userId)), response);
    }

    /// <summary>
    /// The attacker reports how the assault went. A win takes the region wrecked, its garrison
    /// captured; a loss repels the siege and pays the defender for holding.
    ///
    /// <para>The server cannot referee the fight, so a win is accepted only for the run it opened,
    /// inside the window, and not implausibly fast - the same proof-of-attempt a dungeon claim needs.
    /// What stops a forged win being worth a region is everything before it.</para>
    /// </summary>
    public async Task<(SiegeOutcome Outcome, SiegeAssaultResult? Result)> ClaimAssaultAsync(
        Guid gameInstanceId, string userId, Guid siegeId, SiegeAssaultClaimRequest request)
    {
        if (!ContractMatches(request.SharedContractVersion))
            return (ContractMismatch(request.SharedContractVersion), null);

        var siege = await _context.Sieges.FirstOrDefaultAsync(s => s.Id == siegeId && s.GameInstanceId == gameInstanceId);
        if (siege == null)
            return (new SiegeOutcome(SiegeError.SiegeNotFound, Message: "No such siege in this realm."), null);

        if (!string.Equals(siege.AttackerUserId, userId, StringComparison.Ordinal))
            return (new SiegeOutcome(SiegeError.NotAttacker, Message: "Only the besieger may report the assault."), null);

        if (siege.AssaultRunId == null || siege.AssaultRunId != request.RunId)
            return (new SiegeOutcome(SiegeError.RunMismatch, Message: "That is not this siege's assault."), null);

        if (siege.State != SiegeState.Assault)
            return (new SiegeOutcome(SiegeError.WrongState, Message: "This siege is already over."), null);

        var now = Now;
        if (now > siege.AssaultEndsAt + SiegeAssaultRules.ClaimGrace)
        {
            // Too late to count as a win. Let the sweep's rule apply: a fight never reported won held.
            await AdvanceAsync(gameInstanceId);
            return (new SiegeOutcome(SiegeError.WrongState, Message: "The assault window has closed."), null);
        }

        if (request.Won && siege.AssaultStartedAt != null
            && now - siege.AssaultStartedAt.Value < WorldPveService.MinimumRunDuration)
        {
            return (new SiegeOutcome(SiegeError.TooFast, Message: "That assault ended faster than it could be fought."), null);
        }

        var row = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        var world = row == null ? null : JsonNode.Parse(row.GameData);
        var regionNode = WorldRegionBlob.FindRegion(world, siege.RegionId);

        // Re-validated against the live world: the region must still be the defender's to take.
        if (row == null || regionNode == null
            || !WorldRegionBlob.ReadRegion(regionNode).IsOwnedByPlayer(siege.DefenderUserId))
        {
            siege.State = SiegeState.Cancelled;
            siege.ResolvedAt = now;
            await _context.SaveChangesAsync();
            _sessionLog.Log("SIEGE-ADVANCE",
                $"instance={gameInstanceId} siege={siege.Id} region={siege.RegionId} state={siege.State} reason=region-changed-hands");
            await BroadcastAsync(siege);
            return (new SiegeOutcome(SiegeError.WrongState, Message: "The region changed hands before the assault landed."), null);
        }

        if (!request.Won)
        {
            await RepelAsync(siege, row, world!, regionNode, now);
            return (new SiegeOutcome(SiegeError.None, await ToResponseAsync(siege, userId)),
                new SiegeAssaultResult(siege.Id, siege.RegionId, siege.State.ToString(),
                    SeasonScoreRules.PointsFor(SeasonDeed.SiegeRepelled), 0));
        }

        var attacker = await _context.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.DisplayName, u.UserName })
            .FirstOrDefaultAsync();

        int captured = WorldRegionBlob.CaptureWrecked(
            regionNode, userId, attacker?.DisplayName ?? attacker?.UserName, now);

        siege.State = SiegeState.Won;
        siege.ResolvedAt = now;
        await PersistWorldAsync(row, world!);

        _sessionLog.Log("SIEGE-WON",
            $"instance={gameInstanceId} siege={siege.Id} attacker={userId} defender={siege.DefenderUserId} " +
            $"region={siege.RegionId} captured={captured} elapsed={(now - (siege.AssaultStartedAt ?? now)).TotalSeconds:F0}s");

        await BroadcastAsync(siege);

        // After the world is written, so the settle this triggers re-rates both lords against the new map.
        await _seasons.AwardAsync(gameInstanceId, userId, SeasonDeed.SiegeWon);

        return (new SiegeOutcome(SiegeError.None, await ToResponseAsync(siege, userId)),
            new SiegeAssaultResult(siege.Id, siege.RegionId, siege.State.ToString(),
                SeasonScoreRules.PointsFor(SeasonDeed.SiegeWon), captured));
    }

    /// <summary>The defence held: the region's resolve rises and the defender is paid for it.</summary>
    private async Task RepelAsync(Siege siege, WorldViewGameData row, JsonNode world, JsonNode regionNode, DateTime at)
    {
        var region = WorldRegionBlob.ReadRegion(regionNode);
        int resolve = WorldRegionBlob.SetResolve(regionNode, region.Resolve + SiegeAssaultRules.RepelResolveBonus);

        siege.State = SiegeState.Repelled;
        siege.ResolvedAt = at;
        await PersistWorldAsync(row, world);

        _sessionLog.Log("SIEGE-REPELLED",
            $"instance={siege.GameInstanceId} siege={siege.Id} attacker={siege.AttackerUserId} " +
            $"defender={siege.DefenderUserId} region={siege.RegionId} resolve={resolve}");

        await BroadcastAsync(siege);
        await _seasons.AwardAsync(siege.GameInstanceId, siege.DefenderUserId, SeasonDeed.SiegeRepelled);
    }

    /// <summary>Writes the world back and tells the realm, the same way every server-applied change does.</summary>
    private async Task PersistWorldAsync(WorldViewGameData row, JsonNode world)
    {
        row.GameData = world.ToJsonString();
        row.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var payload = JsonSerializer.Deserialize<object>(row.GameData) ?? new { };
        await _hubContext.Clients.Group(row.GameInstanceId.ToString())
            .SendAsync("WorldViewGameDataUpdated", new WorldViewGameDataUpdated(row.GameInstanceId, payload, row.UpdatedAt));
    }

    private async Task<JsonNode?> LoadWorldAsync(Guid gameInstanceId, bool tracked)
    {
        var query = tracked ? _context.WorldViewGameData : _context.WorldViewGameData.AsNoTracking();
        var row = await query.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        return row == null ? null : JsonNode.Parse(row.GameData);
    }

    /// <summary>Champions stationed anywhere in the region. They stand with the defence as elites.</summary>
    private static int GarrisonCountOf(WorldRegionData region)
        => region.SiteOverrides?.Values.Sum(o => o?.GarrisonCharacterIds?.Count ?? 0) ?? 0;

    private static bool ContractMatches(string? version)
        => string.Equals(version, MuggaLuggaTD.Shared.SharedContract.Version, StringComparison.Ordinal);

    private static SiegeOutcome ContractMismatch(string? version)
        => new(SiegeError.ContractMismatch, Message:
            $"Client gameplay rules v{version} do not match the server's " +
            $"v{MuggaLuggaTD.Shared.SharedContract.Version}. Update the game to fight a siege.");

    // -----------------------------------------------------------------
    // Moving through the windows
    // -----------------------------------------------------------------

    /// <summary>
    /// Advances every live siege in one realm whose window has closed, or whose region has changed
    /// hands under it. Returns how many changed. Safe to call as often as anyone likes.
    /// </summary>
    public async Task<int> AdvanceAsync(Guid gameInstanceId)
    {
        var live = await _context.Sieges
            .Where(s => s.GameInstanceId == gameInstanceId
                        && (s.State == SiegeState.Mustering || s.State == SiegeState.Assault))
            .ToListAsync();

        if (live.Count == 0) return 0;

        var worldRow = await _context.WorldViewGameData.AsNoTracking()
            .FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        var world = worldRow == null ? null : JsonNode.Parse(worldRow.GameData);
        var allRegions = WorldRegionBlob.ReadAllRegions(world);
        var now = Now;

        var changed = new List<Siege>();
        var repelled = new List<Siege>();

        foreach (var siege in live)
        {
            var region = allRegions.FirstOrDefault(r => r.RegionId == siege.RegionId);

            // The region is no longer the defender's - somebody else took it, or it vanished with a
            // regenerated world. The siege was against a holding that no longer exists.
            if (region == null || !region.IsOwnedByPlayer(siege.DefenderUserId))
            {
                siege.State = SiegeState.Cancelled;
                siege.ResolvedAt = now;
                changed.Add(siege);
                continue;
            }

            if (siege.State == SiegeState.Mustering && now >= siege.MusterEndsAt)
            {
                // The defender's window has shut. Whatever they mustered is what the attacker faces,
                // however long the attacker then takes to come.
                siege.FrozenHold = RegionHoldCalculator.AssessRegion(region, allRegions, siege.MarchingPower).Hold;
                siege.State = SiegeState.Assault;
                changed.Add(siege);
            }

            if (siege.State == SiegeState.Assault && siege.AssaultRunId == null && now >= siege.AssaultEndsAt)
            {
                // The attacker never came. The defender holds; the attacker's army unlocks.
                siege.State = SiegeState.Lapsed;
                siege.ResolvedAt = siege.AssaultEndsAt;
                if (!changed.Contains(siege)) changed.Add(siege);
            }
            else if (siege.State == SiegeState.Assault && siege.AssaultRunId != null
                     && now >= siege.AssaultEndsAt + SiegeAssaultRules.ClaimGrace)
            {
                // The attacker came and fought, and never reported a win. That is a defence that held.
                repelled.Add(siege);
            }
        }

        if (changed.Count == 0 && repelled.Count == 0) return 0;

        await _context.SaveChangesAsync();

        foreach (var siege in changed)
        {
            _sessionLog.Log("SIEGE-ADVANCE",
                $"instance={gameInstanceId} siege={siege.Id} region={siege.RegionId} state={siege.State} " +
                $"frozenHold={siege.FrozenHold?.ToString() ?? "-"}");
            await BroadcastAsync(siege);
        }

        foreach (var siege in repelled)
        {
            // Read tracked: a repel writes the region's resolve back to the world.
            var row = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
            var liveWorld = row == null ? null : JsonNode.Parse(row.GameData);
            var node = WorldRegionBlob.FindRegion(liveWorld, siege.RegionId);
            if (row == null || node == null) continue;

            await RepelAsync(siege, row, liveWorld!, node, siege.AssaultEndsAt + SiegeAssaultRules.ClaimGrace);
        }

        return changed.Count + repelled.Count;
    }

    /// <summary>The scheduler's sweep: advance every realm with a siege whose window has closed.</summary>
    public async Task<int> AdvanceAllDueAsync()
    {
        var now = Now;
        var graceCutoff = now - SiegeAssaultRules.ClaimGrace;

        var instances = await _context.Sieges
            .Where(s => (s.State == SiegeState.Mustering && s.MusterEndsAt <= now)
                        || (s.State == SiegeState.Assault && s.AssaultRunId == null && s.AssaultEndsAt <= now)
                        || (s.State == SiegeState.Assault && s.AssaultRunId != null && s.AssaultEndsAt <= graceCutoff))
            .Select(s => s.GameInstanceId)
            .Distinct()
            .ToListAsync();

        int total = 0;
        foreach (var instanceId in instances)
            total += await AdvanceAsync(instanceId);

        return total;
    }

    // -----------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------

    private async Task<SiegeResponse> ToResponseAsync(Siege siege, string requesterUserId)
    {
        var attacker = await _context.Users.AsNoTracking()
            .Where(u => u.Id == siege.AttackerUserId)
            .Select(u => new { u.DisplayName, u.UserName })
            .FirstOrDefaultAsync();

        bool isAttacker = string.Equals(siege.AttackerUserId, requesterUserId, StringComparison.Ordinal);

        return new SiegeResponse(
            siege.Id,
            siege.RegionId,
            siege.AttackerUserId,
            attacker?.DisplayName ?? attacker?.UserName ?? "A rival lord",
            siege.DefenderUserId,
            siege.State.ToString(),
            siege.DeclaredAt,
            siege.MusterEndsAt,
            siege.AssaultEndsAt,
            Math.Round(siege.MarchingPower),
            siege.FrozenHold,
            siege.ResolvedAt,
            // The roster is the attacker's business; the defender is told what the army is worth.
            isAttacker ? MarchingArmy.ReadIds(siege.ArmyCharacterIdsJson) : new List<string>(),
            siege.AssaultRunId != null);
    }

    /// <summary>
    /// Tells the realm a siege changed. Sent without the army roster, since it goes to everyone -
    /// the attacker's own client already has it from the declare response and the list endpoint.
    /// </summary>
    private async Task BroadcastAsync(Siege siege)
    {
        var response = await ToResponseAsync(siege, requesterUserId: string.Empty);

        await _hubContext.Clients.Group(siege.GameInstanceId.ToString())
            .SendAsync("SiegeUpdated", response);
    }
}
