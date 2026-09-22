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
    WrongState
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
/// <para><b>This is the first half.</b> A siege can be declared, mustered against, and closed early
/// by the defender; its army is locked; and one the attacker never comes back for lapses. The
/// assault itself - the fight, and what winning or losing it does - is not built yet, so for now
/// every siege that reaches the assault window runs out of it.</para>
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

    public WorldSiegeService(
        ApplicationDbContext context,
        IGameContentProvider content,
        IHubContext<GameHub> hubContext,
        ISessionLog sessionLog,
        ILogger<WorldSiegeService> logger,
        TimeProvider clock)
    {
        _context = context;
        _content = content;
        _hubContext = hubContext;
        _sessionLog = sessionLog;
        _logger = logger;
        _clock = clock;
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

            if (siege.State == SiegeState.Assault && now >= siege.AssaultEndsAt)
            {
                // The attacker never came. The defender holds; the attacker's army unlocks.
                siege.State = SiegeState.Lapsed;
                siege.ResolvedAt = siege.AssaultEndsAt;
                if (!changed.Contains(siege)) changed.Add(siege);
            }
        }

        if (changed.Count == 0) return 0;

        await _context.SaveChangesAsync();

        foreach (var siege in changed)
        {
            _sessionLog.Log("SIEGE-ADVANCE",
                $"instance={gameInstanceId} siege={siege.Id} region={siege.RegionId} state={siege.State} " +
                $"frozenHold={siege.FrozenHold?.ToString() ?? "-"}");
            await BroadcastAsync(siege);
        }

        return changed.Count;
    }

    /// <summary>The scheduler's sweep: advance every realm with a siege whose window has closed.</summary>
    public async Task<int> AdvanceAllDueAsync()
    {
        var now = Now;

        var instances = await _context.Sieges
            .Where(s => (s.State == SiegeState.Mustering && s.MusterEndsAt <= now)
                        || (s.State == SiegeState.Assault && s.AssaultEndsAt <= now))
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
            isAttacker ? MarchingArmy.ReadIds(siege.ArmyCharacterIdsJson) : new List<string>());
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
