using System.Text.Json;
using System.Text.Json.Nodes;
using MuggaLuggaTD.Shared.Gameplay;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>Result of validating a player save's applied ability upgrades.</summary>
public record UpgradeValidationResult(int Accepted, int Rejected, IReadOnlyList<string> RejectedDetails)
{
    public bool Changed => Rejected > 0;
}

/// <summary>
/// Validates a player save on the way in, stripping applied ability upgrades that aren't in the
/// game's content pool. Illegal upgrades would otherwise persist and inflate ability damage, and PvP
/// power is recomputed from this same roster — so an unchecked save is a PvP-power exploit.
///
/// The blob is edited as a JsonNode so only the offending upgrade nodes are removed and everything
/// else the client wrote survives untouched — the same surgical approach the world blob uses.
/// </summary>
public class PlayerSaveValidator
{
    private readonly IGameContentProvider _content;

    public PlayerSaveValidator(IGameContentProvider content)
    {
        _content = content;
    }

    /// <summary>
    /// Removes illegal applied upgrades from <paramref name="save"/> in place. Returns what was
    /// accepted and rejected. A save that fails to parse is left untouched (Accepted/Rejected 0).
    /// </summary>
    public UpgradeValidationResult StripIllegalUpgrades(JsonNode? save)
    {
        var rejectedDetails = new List<string>();
        int accepted = 0, rejected = 0;

        // Indexing a JsonNode by name throws unless it is an object, and the save arrives as whatever
        // the client sent — the endpoint binds it as `object` and the merger passes a non-object
        // straight through. So every step down the document is matched as an object first rather
        // than indexed on faith; a save shaped wrongly carries no upgrades to judge, not a 500.
        if (save is not JsonObject root || root["Characters"] is not JsonArray characters)
            return new UpgradeValidationResult(0, 0, rejectedDetails);

        foreach (var character in characters)
        {
            if (character is not JsonObject characterObject
                || characterObject["Abilities"] is not JsonArray abilities)
                continue;

            foreach (var ability in abilities)
            {
                if (ability is not JsonObject abilityObject) continue;

                var linkName = ReadString(abilityObject["AbilityLinkName"]);
                if (abilityObject["AppliedUpgrades"] is not JsonArray appliedUpgrades)
                    continue;

                _content.AbilityUpgradePools.TryGetValue(linkName ?? string.Empty, out var pool);

                // Walk backwards so removals don't shift the indices still to be checked.
                for (int i = appliedUpgrades.Count - 1; i >= 0; i--)
                {
                    var applied = Deserialize(appliedUpgrades[i]);
                    if (applied != null && pool != null && AbilityUpgradeValidator.IsLegal(applied, pool))
                    {
                        accepted++;
                        continue;
                    }

                    rejected++;
                    rejectedDetails.Add($"{linkName ?? "?"}:\"{applied?.Name ?? "?"}\"");
                    appliedUpgrades.RemoveAt(i);
                }
            }
        }

        return new UpgradeValidationResult(accepted, rejected, rejectedDetails);
    }

    /// <summary>A string field, or null when it is absent or is not a string.</summary>
    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static AbilityUpgradeSaveData? Deserialize(JsonNode? node)
    {
        try
        {
            return node?.Deserialize<AbilityUpgradeSaveData>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
