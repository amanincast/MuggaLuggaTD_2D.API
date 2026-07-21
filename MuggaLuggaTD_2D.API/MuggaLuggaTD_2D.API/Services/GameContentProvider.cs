using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abilities.Models;
using MuggaLuggaTD.Shared.Gameplay;
using Newtonsoft.Json;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Serves the game's authoritative content data (character/ability/item/... definitions) to clients.
///
/// The content ships as JSON files under <c>GameContent/</c> and is read once at startup into an
/// immutable snapshot with a version hash. Clients pull the snapshot after authenticating so every
/// player in an instance runs identical data, and so balance changes ship without a client rebuild.
/// </summary>
public interface IGameContentProvider
{
    /// <summary>Stable hash of the current content set. Changes whenever any document changes.</summary>
    string Version { get; }

    /// <summary>All content documents, keyed by document name (e.g. "CharacterData").</summary>
    IReadOnlyDictionary<string, JsonNode> Documents { get; }

    /// <summary>
    /// Ability definitions, parsed once from AbilityData. PvP power resolution needs these to
    /// re-derive each saved ability's damage, and content is immutable for the process lifetime.
    /// </summary>
    IReadOnlyCollection<GameAbility> AbilityTemplates { get; }
}

public class GameContentProvider : IGameContentProvider
{
    /// <summary>
    /// The documents the client expects. Kept explicit rather than globbing the directory so a
    /// stray file can't silently become game content, and so a missing file fails loudly at startup.
    /// </summary>
    private static readonly string[] DocumentNames =
    {
        "CharacterData",
        "AbilityData",
        "ItemData",
        "AbilityUpgradeData",
        "MaterialData",
        "VisualEffectData",
        "WorldLocationData",
        "DialogueData"
    };

    /// <summary>
    /// The content files are hand-authored and use <c>//</c> comments to park work-in-progress
    /// entries, which Newtonsoft (the Unity client's parser) accepts but System.Text.Json rejects by
    /// default. Parse leniently so designers keep their comments; they're dropped from the payload
    /// the client receives, which is fine — only the data matters there.
    /// </summary>
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _contentRoot;
    private readonly ILogger<GameContentProvider> _logger;
    private readonly Snapshot _snapshot;

    public GameContentProvider(IWebHostEnvironment environment, ILogger<GameContentProvider> logger)
    {
        _contentRoot = Path.Combine(environment.ContentRootPath, "GameContent");
        _logger = logger;
        _snapshot = Load();
    }

    public string Version => _snapshot.Version;

    public IReadOnlyDictionary<string, JsonNode> Documents => _snapshot.Documents;

    public IReadOnlyCollection<GameAbility> AbilityTemplates => _snapshot.AbilityTemplates;

    /// <summary>
    /// Parses AbilityData into typed templates with Newtonsoft — the same parser the Unity client
    /// uses to read these files. System.Text.Json rejects values the content legitimately contains
    /// (a `50.0` literal for a long damage field), which silently zeroed ability damage in PvP power.
    /// </summary>
    private IReadOnlyCollection<GameAbility> ParseAbilityTemplates(string rawAbilityData)
    {
        try
        {
            var document = JsonConvert.DeserializeObject<AbilityContentDocument>(rawAbilityData);
            var abilities = document?.Abilities ?? new List<GameAbility>();
            _logger.LogInformation("Parsed {Count} ability templates for PvP power resolution.", abilities.Count);
            return abilities;
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            // Fail loudly: without templates every party's ability damage silently drops to zero.
            throw new InvalidOperationException("AbilityData.json could not be parsed into ability templates.", ex);
        }
    }

    private Snapshot Load()
    {
        var documents = new Dictionary<string, JsonNode>(DocumentNames.Length, StringComparer.OrdinalIgnoreCase);
        string rawAbilityData = null!;

        // Hash the raw file bytes rather than the re-serialized nodes: the version must change when
        // a file changes, and must not change just because System.Text.Json reformats it.
        using var hash = SHA256.Create();

        foreach (var name in DocumentNames)
        {
            var path = Path.Combine(_contentRoot, $"{name}.json");
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"Game content document '{name}.json' not found at '{path}'. " +
                    "Content files must be deployed alongside the API.");
            }

            var raw = File.ReadAllText(path);

            JsonNode node;
            try
            {
                node = JsonNode.Parse(raw, nodeOptions: null, ParseOptions)
                       ?? throw new InvalidOperationException($"Game content document '{name}.json' is empty.");
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidOperationException($"Game content document '{name}.json' is not valid JSON.", ex);
            }

            documents[name] = node;
            if (name == "AbilityData") rawAbilityData = raw;

            var segment = Encoding.UTF8.GetBytes($"{name}:{raw}\n");
            hash.TransformBlock(segment, 0, segment.Length, null, 0);
        }

        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var version = Convert.ToHexString(hash.Hash!).ToLowerInvariant()[..16];

        _logger.LogInformation("Loaded {Count} game content documents (version {Version}).", documents.Count, version);
        return new Snapshot(version, documents, ParseAbilityTemplates(rawAbilityData));
    }

    private sealed record Snapshot(
        string Version,
        IReadOnlyDictionary<string, JsonNode> Documents,
        IReadOnlyCollection<GameAbility> AbilityTemplates);
}
