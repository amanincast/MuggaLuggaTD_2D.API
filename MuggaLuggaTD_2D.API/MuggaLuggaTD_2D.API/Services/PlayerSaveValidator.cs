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

/// <summary>How many character levels were out of range and pulled back in.</summary>
public record LevelClampResult(int Clamped, long HighestSeen)
{
    public bool Changed => Clamped > 0;
}

/// <summary>How many character identity rolls a save claimed that content does not allow.</summary>
public record SignatureValidationResult(int Cleared, int RarityReset, IReadOnlyList<string> Details)
{
    public bool Changed => Cleared > 0 || RarityReset > 0;
}

/// <summary>How many client-written material stacks were dropped from a save.</summary>
public record MaterialStripResult(int Removed, int TotalQuantity)
{
    public bool Changed => Removed > 0;
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

    /// <summary>
    /// Removes every material from the save's inventory. Materials are server-owned now: they are
    /// granted into the wallet by a claimed run and spent through endpoints, because they buy
    /// characters at the Tavern (design doc 05). A client that keeps writing them into its save is
    /// either an old build or minting currency, and both are answered the same way - by dropping them.
    ///
    /// <para>Equipment is deliberately untouched: it is still client-written and unvalidated, which
    /// is a known gap awaiting a grant ledger. This closes the hole the wallet would otherwise open,
    /// it does not claim to close that one.</para>
    /// </summary>
    public MaterialStripResult StripMaterials(JsonNode? save)
    {
        int removed = 0, quantity = 0;

        if (save is not JsonObject root || root["ItemInventory"] is not JsonObject inventory
            || inventory["Items"] is not JsonArray items)
            return new MaterialStripResult(0, 0);

        // Backwards, so removing one does not shift the indices still to be checked.
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is not JsonObject item) continue;
            if (!IsMaterial(item)) continue;

            removed++;
            quantity += ReadInt(item["ItemCount"]) ?? 1;
            items.RemoveAt(i);
        }

        return new MaterialStripResult(removed, quantity);
    }

    /// <summary>
    /// Pulls every character level back inside 1..<see cref="CharacterProgression.MaxLevel"/>.
    ///
    /// <para>Level is client-written and it is worth money: it scales health and ability damage, and
    /// PvP power is recomputed from this roster, so a hand-edited level 9999 is a hand-edited power
    /// rating. There was no bound to check against until the experience curve gained a cap; now there
    /// is one, so the save is held to it.</para>
    ///
    /// <para>This bounds the <i>ceiling</i>, not the climb - a save can still claim level 30 without
    /// having earned it, because the server does not yet track how much experience it granted. That
    /// needs the same grant ledger the equipment gap is waiting on.</para>
    /// </summary>
    public LevelClampResult ClampLevels(JsonNode? save)
    {
        int clamped = 0;
        long highest = 0;

        if (save is not JsonObject root || root["Characters"] is not JsonArray characters)
            return new LevelClampResult(0, 0);

        foreach (var character in characters)
        {
            if (character is not JsonObject characterObject) continue;

            var level = ReadLong(characterObject["Level"]);
            if (level == null) continue;

            if (level > highest) highest = level.Value;

            var bounded = Math.Clamp(level.Value, 1, CharacterProgression.MaxLevel);
            if (bounded == level.Value) continue;

            characterObject["Level"] = bounded;
            clamped++;
        }

        return new LevelClampResult(clamped, highest);
    }

    /// <summary>
    /// Clears a character's identity roll when content does not allow it: an unknown signature, or
    /// an affinity that signature may not have. Rarity is pulled back to Common whatever the save
    /// says, because nothing grants a higher one yet - the Tavern's hire record is what will.
    ///
    /// <para>What this does <i>not</i> check is whether the player ever earned the roll it names.
    /// That needs the hire record of design doc 05 §5, which arrives with the Tavern. Until then a
    /// save may claim any legal combination; it just cannot claim an illegal one, and it cannot
    /// claim a rarity at all.</para>
    /// </summary>
    public SignatureValidationResult ValidateSignatures(JsonNode? save)
    {
        var details = new List<string>();
        int cleared = 0, rarityReset = 0;

        if (save is not JsonObject root || root["Characters"] is not JsonArray characters)
            return new SignatureValidationResult(0, 0, details);

        foreach (var character in characters)
        {
            if (character is not JsonObject characterObject) continue;

            var rarity = ReadInt(characterObject["Rarity"]);
            if (rarity is not null && rarity != (int)Enums.CharacterRarity.Common)
            {
                characterObject["Rarity"] = (int)Enums.CharacterRarity.Common;
                rarityReset++;
                details.Add($"rarity {rarity} on '{ReadString(characterObject["LinkName"]) ?? "?"}'");
            }

            var signatureId = ReadString(characterObject["SignatureId"]);
            if (string.IsNullOrEmpty(signatureId)) continue;

            var signature = SignatureRules.Find(_content.Signatures, signatureId);
            var affinity = ReadInt(characterObject["SignatureAffinity"]);

            bool unknown = signature == null;
            bool disallowed = signature != null && affinity is not null
                              && !SignatureRules.IsAffinityAllowed(signature, (Enums.AffinityTypes)affinity.Value);

            if (!unknown && !disallowed) continue;

            characterObject["SignatureId"] = null;
            characterObject["SignatureAffinity"] = null;
            cleared++;
            details.Add(unknown
                ? $"unknown signature '{signatureId}'"
                : $"affinity {affinity} not allowed for '{signatureId}'");
        }

        return new SignatureValidationResult(cleared, rarityReset, details);
    }

    /// <summary>Materials are ItemTypes.Material (14), whatever else the client wrote on them.</summary>
    private static bool IsMaterial(JsonObject item)
        => ReadInt(item["ItemType"]) == (int)Enums.ItemTypes.Material;

    private static long? ReadLong(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private static int? ReadInt(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

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
