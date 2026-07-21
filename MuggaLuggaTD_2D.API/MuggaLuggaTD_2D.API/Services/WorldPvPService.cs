using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>Why an attack was refused, so the controller can pick the right status code.</summary>
public enum PvPAttackError
{
    None = 0,
    WorldNotFound,
    LocationNotFound,
    NotAttackable,
    OwnCharactersUnavailable,
    NoAttackers,
    ContractMismatch
}

public record PvPAttackOutcome(PvPAttackError Error, PvPAttackResponse? Response, string? Message = null)
{
    public bool Succeeded => Error == PvPAttackError.None;
}

/// <summary>
/// Resolves passive PvP server-side.
///
/// v1 was client-trusted: the client computed both powers, rolled its own dice, applied the capture
/// to its local world, and pushed the whole world blob up. Anyone could simply write themselves a
/// won fight. Here the server owns every input that decides the result — target validation, both
/// power values (recomputed from persisted rosters via the shared rules), and the dice — then
/// mutates the world blob itself.
///
/// The blob is edited as a JsonNode rather than round-tripped through a typed model on purpose: the
/// client owns the world schema, and the server should touch only the ownership and garrison fields
/// it is authoritative for, leaving every other field byte-for-byte intact.
/// </summary>
public class WorldPvPService
{



    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly ILogger<WorldPvPService> _logger;

    public WorldPvPService(ApplicationDbContext context, IGameContentProvider content, ILogger<WorldPvPService> logger)
    {
        _context = context;
        _content = content;
        _logger = logger;
    }

    /// <summary>
    /// Resolves an attack and, on success, returns the mutated world blob for the caller to persist
    /// and broadcast. Returns the outcome and the updated world so persistence stays in one place.
    /// </summary>
    public async Task<(PvPAttackOutcome Outcome, JsonNode? UpdatedWorld)> ResolveAsync(
        Guid gameInstanceId,
        string attackerUserId,
        string? attackerDisplayName,
        PvPAttackRequest request)
    {
        if (!string.Equals(request.SharedContractVersion, MuggaLuggaTD.Shared.SharedContract.Version, StringComparison.Ordinal))
        {
            return (new PvPAttackOutcome(PvPAttackError.ContractMismatch, null,
                $"Client gameplay rules v{request.SharedContractVersion} do not match the server's " +
                $"v{MuggaLuggaTD.Shared.SharedContract.Version}. Update the game to attack."), null);
        }

        var worldRow = await _context.WorldViewGameData
            .FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);

        if (worldRow == null)
            return (new PvPAttackOutcome(PvPAttackError.WorldNotFound, null, "World view data not found."), null);

        var world = JsonNode.Parse(worldRow.GameData);
        var location = WorldBlobEditor.FindLocation(world, request.LocationId);
        if (location == null)
            return (new PvPAttackOutcome(PvPAttackError.LocationNotFound, null, "Location not found in this world."), null);

        // Only another player's location can be attacked this way. Neutral/enemy locations are PvE.
        var ownership = WorldBlobEditor.GetOwnership(location);
        var ownerUserId = WorldBlobEditor.GetOwnerUserId(location);
        if (ownership != LocationOwnership.Player || string.IsNullOrEmpty(ownerUserId))
            return (new PvPAttackOutcome(PvPAttackError.NotAttackable, null, "That location is not held by another player."), null);

        if (string.Equals(ownerUserId, attackerUserId, StringComparison.Ordinal))
            return (new PvPAttackOutcome(PvPAttackError.NotAttackable, null, "You already own that location."), null);

        // The attacking party is validated against the attacker's *persisted* roster, so a client
        // cannot attack with characters it does not own, or with ones already committed elsewhere.
        var attackerSave = await LoadPlayerSaveAsync(gameInstanceId, attackerUserId);
        var ownedIds = attackerSave?.Characters?
            .Where(c => c?.Id != null).Select(c => c.Id).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

        var unavailable = WorldBlobEditor.CollectCommittedCharacterIds(world, attackerUserId);
        var attackers = (request.AttackerCharacterIds ?? new List<string>())
            .Where(id => !string.IsNullOrEmpty(id) && ownedIds.Contains(id) && !unavailable.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (attackers.Count == 0)
        {
            return (new PvPAttackOutcome(PvPAttackError.NoAttackers, null,
                "None of the attacking characters are available — they may be garrisoned or captured."), null);
        }

        var attackerPower = PartyPowerCalculator.CalculatePartyPower(attackerSave, attackers, _content.AbilityTemplates);
        var defenderPower = location["GarrisonPower"]?.GetValue<float>() ?? 0f;

        // Server-side dice. The client no longer rolls; it renders what comes back.
        var roll = Random.Shared.Next(1, 21);
        var result = PassivePvPResolver.Resolve(attackerPower, defenderPower, roll);

        string? conquestOutcome = null;
        string? defeatOutcome = null;
        var affected = new List<string>();

        if (result.AttackerWins)
        {
            WorldBlobEditor.Capture(location, attackerUserId, attackerDisplayName);
            conquestOutcome = "Captured";
        }
        else
        {
            defeatOutcome = ApplyDefeat(location, attackers, affected);
        }

        _logger.LogInformation(
            "PvP {Result}: user {Attacker} vs location {Location} (atk {AtkPower:F0} vs def {DefPower:F0}, roll {Roll}{Mod:+#;-#;+0} = {Total}).",
            result.AttackerWins ? "WIN" : "LOSS", attackerUserId, request.LocationId,
            attackerPower, defenderPower, roll, result.Modifier, result.Total);

        var response = new PvPAttackResponse(
            result.AttackerWins,
            result.D20Roll,
            result.Modifier,
            result.Total,
            attackerPower,
            defenderPower,
            request.LocationId,
            conquestOutcome,
            defeatOutcome,
            affected);

        return (new PvPAttackOutcome(PvPAttackError.None, response), world);
    }

    /// <summary>
    /// Rolls the D4 defeat consequence for a failed attack. Captured attackers are recorded on the
    /// location server-side, so a client that ignores its own defeat still finds them imprisoned:
    /// the world blob is authoritative and CapturedCharacterReconciler re-applies it on next load.
    /// </summary>
    private static string ApplyDefeat(JsonNode location, List<string> attackers, List<string> affected)
    {
        // Matches the client's DefeatOutcomeType ordering.
        var outcome = Random.Shared.Next(0, 4) switch
        {
            0 => "PartyMemberKilled",
            1 => "PartyMemberCaptured",
            2 => "PartyCaptured",
            _ => "Escaped"
        };

        switch (outcome)
        {
            case "PartyMemberKilled":
                affected.Add(attackers[Random.Shared.Next(attackers.Count)]);
                break;
            case "PartyMemberCaptured":
                affected.Add(attackers[Random.Shared.Next(attackers.Count)]);
                WorldBlobEditor.AddCaptured(location, affected);
                break;
            case "PartyCaptured":
                affected.AddRange(attackers);
                WorldBlobEditor.AddCaptured(location, affected);
                break;
        }

        return outcome;
    }

    private async Task<UserSaveData?> LoadPlayerSaveAsync(Guid gameInstanceId, string userId)
    {
        var row = await _context.PlayerGameData
            .FirstOrDefaultAsync(p => p.GameInstanceId == gameInstanceId && p.UserId == userId);

        if (row == null)
            return null;

        try
        {
            // Parsed with Newtonsoft because the client wrote it with Newtonsoft: System.Text.Json
            // disagrees about values this format legitimately contains (e.g. `50.0` for a long field).
            return Newtonsoft.Json.JsonConvert.DeserializeObject<UserSaveData>(row.GameData);
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "Could not read player save for {UserId} in instance {Instance}.", userId, gameInstanceId);
            return null;
        }
    }

}

