using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

public enum PveError
{
    None = 0,
    WorldNotFound,
    LocationNotFound,
    NotPveTarget,
    RunNotFound,
    RunAlreadyClaimed,
    RunTooFast,
    ContractMismatch,
    NoConquestEffect
}

public record PveOutcome(PveError Error, string? Message = null)
{
    public bool Succeeded => Error == PveError.None;
}

/// <summary>
/// Server-side PvE conquest.
///
/// Unlike PvP, the server cannot recompute the result: PvE is real-time bullet-hell combat that the
/// server does not simulate, so it has no way to know whether the player actually won. What it can
/// own — and now does — is everything around that:
///
///   - eligibility: the location exists, is a legitimate PvE target, and is not another player's
///   - proof of attempt: a claim must reference a run this player opened against this location
///   - single use: a run can be claimed once, and the claim re-validates against the live world
///   - the state transition: the server applies the capture or removal and writes the world itself
///
/// Previously the client applied the conquest locally and pushed the whole world blob, so a
/// conquest could be fabricated outright. It no longer can. What remains possible is cheating
/// *within* the combat scene to produce a genuine-looking win; that is inherent to client-side
/// real-time combat and is not addressed here.
/// </summary>
public class WorldPveService
{
    /// <summary>
    /// A run claimed faster than this never happened — the combat scene cannot be completed in less.
    /// Deliberately generous: this is a floor against instant scripted claims, not a balance knob.
    /// </summary>
    public static readonly TimeSpan MinimumRunDuration = TimeSpan.FromSeconds(10);

    /// <summary>Open runs older than this are treated as abandoned.</summary>
    public static readonly TimeSpan RunExpiry = TimeSpan.FromHours(6);

    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly ILogger<WorldPveService> _logger;

    public WorldPveService(ApplicationDbContext context, IGameContentProvider content, ILogger<WorldPveService> logger)
    {
        _context = context;
        _content = content;
        _logger = logger;
    }

    /// <summary>
    /// Opens a run against a PvE location. Called as the player enters combat, so a later claim has
    /// something to prove itself against.
    /// </summary>
    public async Task<(PveOutcome Outcome, Guid RunId)> BeginAsync(
        Guid gameInstanceId, string userId, PveBeginRequest request)
    {
        if (!ContractMatches(request.SharedContractVersion, out var mismatch))
            return (mismatch, Guid.Empty);

        var worldRow = await _context.WorldViewGameData
            .FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);

        if (worldRow == null)
            return (new PveOutcome(PveError.WorldNotFound, "World view data not found."), Guid.Empty);

        var world = JsonNode.Parse(worldRow.GameData);
        var location = WorldBlobEditor.FindLocation(world, request.LocationId);
        if (location == null)
            return (new PveOutcome(PveError.LocationNotFound, "Location not found in this world."), Guid.Empty);

        var check = ValidatePveTarget(location, userId);
        if (!check.Succeeded)
            return (check, Guid.Empty);

        // One open run per player per location: re-entering replaces the previous attempt rather
        // than accumulating claimable runs.
        var existing = await _context.PveRuns
            .Where(r => r.GameInstanceId == gameInstanceId && r.UserId == userId
                        && r.LocationId == request.LocationId && r.ClaimedAt == null)
            .ToListAsync();
        if (existing.Count > 0)
            _context.PveRuns.RemoveRange(existing);

        var run = new PveRun
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            LocationId = request.LocationId,
            LocationType = (int)WorldBlobEditor.GetLocationType(location),
            StartedAt = DateTime.UtcNow
        };

        _context.PveRuns.Add(run);
        await _context.SaveChangesAsync();

        _logger.LogInformation("PvE run {RunId} opened by {User} at {Location}.", run.Id, userId, request.LocationId);
        return (new PveOutcome(PveError.None), run.Id);
    }

    /// <summary>
    /// Claims the conquest for a completed run. Returns the mutated world for the caller to persist
    /// and broadcast, so persistence stays in one place.
    /// </summary>
    public async Task<(PveOutcome Outcome, PveClaimResponse? Response, JsonNode? UpdatedWorld)> ClaimAsync(
        Guid gameInstanceId, string userId, string? displayName, PveClaimRequest request)
    {
        if (!ContractMatches(request.SharedContractVersion, out var mismatch))
            return (mismatch, null, null);

        var run = await _context.PveRuns.FirstOrDefaultAsync(r =>
            r.Id == request.RunId && r.GameInstanceId == gameInstanceId && r.UserId == userId);

        if (run == null)
            return (new PveOutcome(PveError.RunNotFound, "No such run for this player."), null, null);

        if (run.ClaimedAt != null)
            return (new PveOutcome(PveError.RunAlreadyClaimed, "That run has already been claimed."), null, null);

        var elapsed = DateTime.UtcNow - run.StartedAt;
        if (elapsed < MinimumRunDuration)
            return (new PveOutcome(PveError.RunTooFast, "That run completed implausibly fast."), null, null);

        if (elapsed > RunExpiry)
            return (new PveOutcome(PveError.RunNotFound, "That run has expired."), null, null);

        var worldRow = await _context.WorldViewGameData
            .FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (worldRow == null)
            return (new PveOutcome(PveError.WorldNotFound, "World view data not found."), null, null);

        var world = JsonNode.Parse(worldRow.GameData);
        var location = WorldBlobEditor.FindLocation(world, run.LocationId);
        if (location == null)
        {
            // Someone else cleared it while this player was fighting. Close the run so it cannot be
            // held open and replayed if the location ever returns.
            run.ClaimedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return (new PveOutcome(PveError.LocationNotFound, "That location no longer exists."), null, null);
        }

        // Re-validate against the live world, not against what was true when the run started —
        // another player may have taken the location in the meantime.
        var check = ValidatePveTarget(location, userId);
        if (!check.Succeeded)
            return (check, null, null);

        var type = WorldBlobEditor.GetLocationType(location);
        var outcome = ConquestResolver.ResolveOnPlayerVictory(type);

        if (outcome == ConquestOutcome.None)
            return (new PveOutcome(PveError.NoConquestEffect, "Winning here has no conquest effect."), null, null);

        switch (outcome)
        {
            case ConquestOutcome.CaptureForPlayer:
                WorldBlobEditor.Capture(location, userId, displayName);
                break;
            case ConquestOutcome.RemoveLocation:
                WorldBlobEditor.RemoveLocation(world, run.LocationId);
                break;
        }

        run.ClaimedAt = DateTime.UtcNow;

        // Rewards are rolled here, from the location, rather than accepted from the client. A
        // self-reported total is unbounded, and fabricated XP/gear inflates the same persisted roster
        // that PvP power is computed from.
        var rewards = RunRewardCalculator.Calculate(
            location["Level"]?.GetValue<int>() ?? 1,
            location["Tier"]?.GetValue<int>() ?? 1,
            _content.RunTuning,
            _content.DroppableItems,
            Random.Shared);

        _logger.LogInformation(
            "PvE conquest {Outcome} at {Location} by {User} (run {RunId}, {Seconds:F0}s) — {Xp} XP, {Items} item(s).",
            outcome, run.LocationId, userId, run.Id, elapsed.TotalSeconds, rewards.Experience, rewards.Items.Count);

        var response = new PveClaimResponse(
            run.LocationId, outcome.ToString(), rewards.Experience, rewards.Items);

        return (new PveOutcome(PveError.None), response, world);
    }

    /// <summary>
    /// A location is a legitimate PvE target when it is not held by a player. Another player's
    /// location is a PvP target and must go through the PvP endpoint, which resolves a contested
    /// fight rather than handing over a capture on the attacker's say-so.
    /// </summary>
    private static PveOutcome ValidatePveTarget(JsonNode location, string userId)
    {
        var ownership = WorldBlobEditor.GetOwnership(location);
        var ownerUserId = WorldBlobEditor.GetOwnerUserId(location);

        if (ownership == LocationOwnership.Player && !string.IsNullOrEmpty(ownerUserId))
        {
            return string.Equals(ownerUserId, userId, StringComparison.Ordinal)
                ? new PveOutcome(PveError.NotPveTarget, "You already own that location.")
                : new PveOutcome(PveError.NotPveTarget, "That location belongs to another player — attack it instead.");
        }

        var type = WorldBlobEditor.GetLocationType(location);
        if (ConquestResolver.ResolveOnPlayerVictory(type) == ConquestOutcome.None)
            return new PveOutcome(PveError.NotPveTarget, "That location has no combat to complete.");

        return new PveOutcome(PveError.None);
    }

    private static bool ContractMatches(string clientVersion, out PveOutcome mismatch)
    {
        if (string.Equals(clientVersion, MuggaLuggaTD.Shared.SharedContract.Version, StringComparison.Ordinal))
        {
            mismatch = new PveOutcome(PveError.None);
            return true;
        }

        mismatch = new PveOutcome(PveError.ContractMismatch,
            $"Client gameplay rules v{clientVersion} do not match the server's " +
            $"v{MuggaLuggaTD.Shared.SharedContract.Version}. Update the game to continue.");
        return false;
    }
}
