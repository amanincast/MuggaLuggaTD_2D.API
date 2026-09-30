using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;

namespace MuggaLuggaTD_2D.API.Services;

public enum TalentError
{
    None,
    NoSave,
    NoSuchCharacter,
    Refused,
    NothingToUnlearn,
    InsufficientGold,
}

/// <summary>What a learn or a respec left the character with.</summary>
public record TalentOutcome(
    TalentError Error,
    string? Message = null,
    string CharacterId = "",
    Dictionary<string, int>? Talents = null,
    int Points = 0,
    int Unspent = 0,
    long RespecCost = 0,
    long? GoldBalance = null)
{
    public bool Succeeded => Error == TalentError.None;
}

/// <summary>
/// The Trainer (design 6c): learning a talent and unlearning them all for gold.
///
/// <para><b>Talents live on the character in the player's save, but only this writes them.</b> Every
/// save is put back to what was stored before it (<see cref="ReconcileTalents"/>), so a client that
/// writes its own ranks loses them at once. That keeps the ranks where every power calculation
/// already reads the roster from — garrisons, raids, sieges — without a second table to join, and
/// needed no migration. Points come from the stored level, the one the save validator clamps.</para>
/// </summary>
public class TalentService
{
    private readonly ApplicationDbContext _context;
    private readonly GoldService _gold;
    private readonly ISessionLog _sessionLog;

    public TalentService(ApplicationDbContext context, GoldService gold, ISessionLog sessionLog)
    {
        _context = context;
        _gold = gold;
        _sessionLog = sessionLog;
    }

    /// <summary>One rank of <paramref name="nodeId"/> for <paramref name="characterId"/>.</summary>
    public async Task<TalentOutcome> LearnAsync(Guid gameInstanceId, string userId, string characterId, string nodeId)
    {
        var found = await FindAsync(gameInstanceId, userId, characterId);
        if (found.Error != TalentError.None) return new TalentOutcome(found.Error, found.Message);

        var (row, root, character) = (found.Row!, found.Root!, found.Character!);
        long level = LevelOf(character);
        var ranks = ReadTalents(character);

        var why = TalentRules.WhyNot(ranks, level, nodeId);
        if (why != null)
        {
            _sessionLog.Log("TALENT-REFUSE", $"user={userId} char={characterId} node={nodeId} level={level} why={why}");
            return new TalentOutcome(TalentError.Refused, why);
        }

        ranks[nodeId] = TalentRules.RankOf(ranks, nodeId) + 1;
        WriteTalents(character, ranks);
        row.GameData = root.ToJsonString();
        row.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _sessionLog.Log("TALENT-LEARN", $"user={userId} char={characterId} node={nodeId} rank={ranks[nodeId]} spent={TalentRules.Spent(ranks)}/{TalentRules.PointsFor(level)}");
        return Describe(characterId, ranks, level, null);
    }

    /// <summary>Unlearns every talent, for <see cref="TalentRules.RespecCost"/> gold. All or nothing.</summary>
    public async Task<TalentOutcome> RespecAsync(Guid gameInstanceId, string userId, string characterId)
    {
        var found = await FindAsync(gameInstanceId, userId, characterId);
        if (found.Error != TalentError.None) return new TalentOutcome(found.Error, found.Message);

        var (row, root, character) = (found.Row!, found.Root!, found.Character!);
        long level = LevelOf(character);
        var ranks = ReadTalents(character);
        if (TalentRules.Spent(ranks) == 0)
            return new TalentOutcome(TalentError.NothingToUnlearn, "There is nothing to unlearn.");

        // The save is changed first and the gold spent second: the spend is what saves both, in one
        // write, and a refused spend returns before anything is saved.
        WriteTalents(character, new Dictionary<string, int>());
        row.GameData = root.ToJsonString();
        row.UpdatedAt = DateTime.UtcNow;

        long cost = TalentRules.RespecCost(level);
        var spent = await _gold.SpendAsync(gameInstanceId, userId, cost, $"respec:{characterId}");
        if (!spent.Succeeded)
            return new TalentOutcome(TalentError.InsufficientGold, spent.Message);

        _sessionLog.Log("TALENT-RESPEC", $"user={userId} char={characterId} refunded={TalentRules.Spent(ranks)} gold={cost}");
        return Describe(characterId, new Dictionary<string, int>(), level, spent.Balance);
    }

    /// <summary>
    /// Puts every character's talents in <paramref name="save"/> back to what <paramref name="storedJson"/>
    /// (the save before this one) held. A character the stored save does not have learns nothing.
    /// Returns how many characters' talents had to be corrected.
    /// </summary>
    public static int ReconcileTalents(JsonNode? save, string? storedJson)
    {
        if (save is not JsonObject root || root["Characters"] is not JsonArray characters) return 0;

        var stored = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        try
        {
            if (!string.IsNullOrWhiteSpace(storedJson)
                && JsonNode.Parse(storedJson) is JsonObject old
                && old["Characters"] is JsonArray oldCharacters)
            {
                foreach (var c in oldCharacters)
                    if (c is JsonObject o && ReadString(o["Id"]) is string id)
                        stored[id] = ReadTalents(o);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // An unreadable stored save holds no talents to keep.
        }

        int corrected = 0;
        foreach (var c in characters)
        {
            if (c is not JsonObject character) continue;
            var id = ReadString(character["Id"]) ?? string.Empty;
            var keep = stored.TryGetValue(id, out var ranks) ? ranks : new Dictionary<string, int>();
            if (!SameRanks(ReadTalents(character), keep)) corrected++;
            WriteTalents(character, keep);
        }
        return corrected;
    }

    // -----------------------------------------------------------------

    private record Found(TalentError Error, string? Message,
        Models.PlayerGameData? Row = null, JsonObject? Root = null, JsonObject? Character = null);

    private async Task<Found> FindAsync(Guid gameInstanceId, string userId, string characterId)
    {
        var row = await _context.PlayerGameData
            .FirstOrDefaultAsync(p => p.GameInstanceId == gameInstanceId && p.UserId == userId);
        if (row == null) return new Found(TalentError.NoSave, "No saved roster in this realm yet.");

        JsonObject? root;
        try { root = JsonNode.Parse(row.GameData) as JsonObject; }
        catch (System.Text.Json.JsonException) { root = null; }

        if (root?["Characters"] is not JsonArray characters)
            return new Found(TalentError.NoSave, "No saved roster in this realm yet.");

        var character = characters.OfType<JsonObject>()
            .FirstOrDefault(c => string.Equals(ReadString(c["Id"]), characterId, StringComparison.Ordinal));
        if (character == null)
            return new Found(TalentError.NoSuchCharacter, "That character is not in your roster.");

        return new Found(TalentError.None, null, row, root, character);
    }

    private static TalentOutcome Describe(string characterId, Dictionary<string, int> ranks, long level, long? gold)
        => new TalentOutcome(TalentError.None, null, characterId, ranks,
            TalentRules.PointsFor(level), TalentRules.Unspent(ranks, level), TalentRules.RespecCost(level), gold);

    public static Dictionary<string, int> ReadTalents(JsonObject character)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        if (character["Talents"] is not JsonObject talents) return ranks;
        foreach (var entry in talents)
        {
            if (entry.Value is JsonValue v && v.TryGetValue<int>(out var rank) && rank > 0)
                ranks[entry.Key] = rank;
            else if (entry.Value is JsonValue d && d.TryGetValue<double>(out var real) && real >= 1)
                ranks[entry.Key] = (int)real;
        }
        return ranks;
    }

    private static void WriteTalents(JsonObject character, Dictionary<string, int> ranks)
    {
        var node = new JsonObject();
        foreach (var entry in ranks.Where(e => e.Value > 0).OrderBy(e => e.Key, StringComparer.Ordinal))
            node[entry.Key] = entry.Value;
        character["Talents"] = node;
    }

    private static bool SameRanks(Dictionary<string, int> a, Dictionary<string, int> b)
        => a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var r) && r == e.Value);

    private static long LevelOf(JsonObject character)
    {
        if (character["Level"] is JsonValue v)
        {
            if (v.TryGetValue<long>(out var level)) return Math.Clamp(level, 1, CharacterProgression.MaxLevel);
            if (v.TryGetValue<double>(out var real)) return Math.Clamp((long)real, 1, CharacterProgression.MaxLevel);
        }
        return 1;
    }

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
