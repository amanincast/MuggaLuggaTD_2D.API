using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Which of a player's champions may actually march, and what they are worth.
///
/// <para>Raids and sieges both take a list of character ids from the client, and both must refuse to
/// believe it: a champion the player does not own, one stationed in a garrison, one held prisoner, or
/// one already committed to a siege cannot march. One implementation, so the two actions cannot come
/// to disagree about who is free.</para>
/// </summary>
public static class MarchingArmy
{
    public record Muster(List<string> CharacterIds, double Power, UserSaveData? Save);

    /// <summary>
    /// Filters <paramref name="requestedIds"/> down to the champions this player may field, and prices
    /// what is left from the persisted roster.
    /// </summary>
    public static async Task<Muster> MusterAsync(
        ApplicationDbContext context,
        IGameContentProvider content,
        ILogger logger,
        Guid gameInstanceId,
        string userId,
        JsonNode? world,
        IEnumerable<string>? requestedIds)
    {
        var save = await LoadPlayerSaveAsync(context, logger, gameInstanceId, userId);

        var owned = save?.Characters?
            .Where(c => c?.Id != null).Select(c => c.Id).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

        var committed = WorldRegionBlob.CollectCommittedCharacterIds(world, userId);
        committed.UnionWith(await SiegeLockedIdsAsync(context, gameInstanceId, userId));

        var marching = (requestedIds ?? Enumerable.Empty<string>())
            .Where(id => !string.IsNullOrEmpty(id) && owned.Contains(id) && !committed.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        double power = marching.Count == 0
            ? 0
            : PartyPowerCalculator.CalculatePartyPower(save, marching, content.AbilityTemplates);

        return new Muster(marching, power, save);
    }

    /// <summary>
    /// Champions locked into a live siege this player has declared. Declaring commits the army for
    /// as long as the siege runs (siege.md §3): they cannot raid, garrison or run dungeons.
    /// </summary>
    public static async Task<HashSet<string>> SiegeLockedIdsAsync(
        ApplicationDbContext context, Guid gameInstanceId, string userId)
    {
        var armies = await context.Sieges
            .Where(s => s.GameInstanceId == gameInstanceId && s.AttackerUserId == userId
                        && (s.State == SiegeState.Mustering || s.State == SiegeState.Assault))
            .Select(s => s.ArmyCharacterIdsJson)
            .ToListAsync();

        var locked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var json in armies)
            locked.UnionWith(ReadIds(json));

        return locked;
    }

    public static List<string> ReadIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return new List<string>();
        }
    }

    public static string WriteIds(IEnumerable<string> ids)
        => System.Text.Json.JsonSerializer.Serialize(ids.ToList());

    private static async Task<UserSaveData?> LoadPlayerSaveAsync(
        ApplicationDbContext context, ILogger logger, Guid gameInstanceId, string userId)
    {
        var row = await context.PlayerGameData
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
            logger.LogWarning(ex, "Could not read player save for {UserId} in instance {Instance}.", userId, gameInstanceId);
            return null;
        }
    }
}
