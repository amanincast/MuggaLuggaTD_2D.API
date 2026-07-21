using System.Linq;
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

    /// <summary>Run length and enemy scaling, from SurvivalData. Drives the PvE reward budget.</summary>
    RunTuning RunTuning { get; }

    /// <summary>Droppable base items, from ItemData. The pool PvE rewards roll from.</summary>
    IReadOnlyList<ItemTemplate> DroppableItems { get; }

    /// <summary>
    /// Legal ability upgrades keyed by ability link name, from AbilityUpgradeData. Player saves are
    /// validated against these so an upgrade outside the pool can't be persisted.
    /// </summary>
    IReadOnlyDictionary<string, List<AbilityUpgrade>> AbilityUpgradePools { get; }
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
        "DialogueData",
        "SurvivalData"
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

    public RunTuning RunTuning => _snapshot.RunTuning;

    public IReadOnlyList<ItemTemplate> DroppableItems => _snapshot.DroppableItems;

    public IReadOnlyDictionary<string, List<AbilityUpgrade>> AbilityUpgradePools => _snapshot.AbilityUpgradePools;

    /// <summary>
    /// Reads AbilityUpgradeData into the per-ability legal upgrade pools used to validate saves.
    /// </summary>
    private IReadOnlyDictionary<string, List<AbilityUpgrade>> ParseUpgradePools(string rawUpgradeData)
    {
        try
        {
            var document = JsonConvert.DeserializeObject<UpgradeContentDocument>(rawUpgradeData);
            var pools = new Dictionary<string, List<AbilityUpgrade>>(StringComparer.Ordinal);

            foreach (var entry in document?.AbilityUpgrades ?? new List<UpgradesByAbilityEntry>())
            {
                if (entry?.AbilityLinkName == null) continue;

                // Accumulate rather than assign: the content can list the same ability more than once
                // (e.g. a second, empty Bow_Attack_1 entry), and a plain assignment let an empty
                // duplicate clobber the real pool — every legitimate upgrade for that ability was then
                // rejected on save. The client dodged this because it reads the first match only.
                if (!pools.TryGetValue(entry.AbilityLinkName, out var list))
                    pools[entry.AbilityLinkName] = list = new List<AbilityUpgrade>();

                if (entry.Upgrades != null)
                    list.AddRange(entry.Upgrades);
            }

            _logger.LogInformation("Parsed upgrade pools for {Count} abilities.", pools.Count);
            return pools;
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            throw new InvalidOperationException("AbilityUpgradeData.json could not be parsed into upgrade pools.", ex);
        }
    }

    /// <summary>Mirrors the client's AbilityUpgradeDefaultData layout.</summary>
    private sealed class UpgradeContentDocument
    {
        public List<UpgradesByAbilityEntry> AbilityUpgrades { get; set; } = new();
    }

    private sealed class UpgradesByAbilityEntry
    {
        public string AbilityLinkName { get; set; } = string.Empty;
        public List<AbilityUpgrade> Upgrades { get; set; } = new();
    }

    /// <summary>
    /// Reads the run tuning that both the combat scene's pacing and the reward budget come from.
    /// Missing or unreadable content is fatal rather than defaulted: silently paying out against
    /// different numbers than the fight used is worse than refusing to start.
    /// </summary>
    private RunTuning ParseRunTuning(string rawSurvivalData)
    {
        try
        {
            return JsonConvert.DeserializeObject<RunTuning>(rawSurvivalData)
                   ?? throw new InvalidOperationException("SurvivalData.json produced no tuning.");
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            throw new InvalidOperationException("SurvivalData.json could not be parsed into run tuning.", ex);
        }
    }

    /// <summary>
    /// Flattens the item content into the droppable pool PvE rewards roll from. Mirrors the client's
    /// ItemDefaultData categories; anything not marked droppable is excluded.
    /// </summary>
    private IReadOnlyList<ItemTemplate> ParseDroppableItems(string rawItemData)
    {
        try
        {
            var document = JsonConvert.DeserializeObject<ItemContentDocument>(rawItemData);
            if (document == null) return Array.Empty<ItemTemplate>();

            var templates = document.AllCategories()
                .Where(i => i != null && i.IsDroppable && !string.IsNullOrEmpty(i.ItemName))
                .Select(i => new ItemTemplate
                {
                    ItemName = i.ItemName,
                    ItemType = i.ItemType,
                    ImplicitPool = i.ImplicitPool,
                    ExplicitPool = i.ExplicitPool
                })
                .ToList();

            _logger.LogInformation("Parsed {Count} droppable item templates for PvE rewards.", templates.Count);
            return templates;
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            throw new InvalidOperationException("ItemData.json could not be parsed into item templates.", ex);
        }
    }

    /// <summary>Mirrors the client's GameApplication.Models.ItemDefaultData category layout.</summary>
    private sealed class ItemContentDocument
    {
        public List<ItemEntry> General { get; set; } = new();
        public List<ItemEntry> Helmets { get; set; } = new();
        public List<ItemEntry> BodyArmor { get; set; } = new();
        public List<ItemEntry> LegArmor { get; set; } = new();
        public List<ItemEntry> Boots { get; set; } = new();
        public List<ItemEntry> Gloves { get; set; } = new();
        public List<ItemEntry> Rings { get; set; } = new();
        public List<ItemEntry> Amulets { get; set; } = new();
        public List<ItemEntry> Bracelets { get; set; } = new();
        public List<ItemEntry> Capes { get; set; } = new();
        public List<ItemEntry> Weapons { get; set; } = new();
        public List<ItemEntry> Offhand { get; set; } = new();

        public IEnumerable<ItemEntry> AllCategories() =>
            General.Concat(Helmets).Concat(BodyArmor).Concat(LegArmor).Concat(Boots)
                   .Concat(Gloves).Concat(Rings).Concat(Amulets).Concat(Bracelets)
                   .Concat(Capes).Concat(Weapons).Concat(Offhand);
    }

    private sealed class ItemEntry
    {
        public string ItemName { get; set; } = string.Empty;
        public bool IsDroppable { get; set; }
        public Enums.ItemTypes ItemType { get; set; }
        public List<Enums.ItemImplicitTypes> ImplicitPool { get; set; } = new();
        public List<Enums.ItemExplicitTypes> ExplicitPool { get; set; } = new();
    }

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
        string rawSurvivalData = null!;
        string rawItemData = null!;
        string rawUpgradeData = null!;

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
            if (name == "SurvivalData") rawSurvivalData = raw;
            if (name == "ItemData") rawItemData = raw;
            if (name == "AbilityUpgradeData") rawUpgradeData = raw;

            var segment = Encoding.UTF8.GetBytes($"{name}:{raw}\n");
            hash.TransformBlock(segment, 0, segment.Length, null, 0);
        }

        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var version = Convert.ToHexString(hash.Hash!).ToLowerInvariant()[..16];

        _logger.LogInformation("Loaded {Count} game content documents (version {Version}).", documents.Count, version);
        return new Snapshot(version, documents, ParseAbilityTemplates(rawAbilityData),
            ParseRunTuning(rawSurvivalData), ParseDroppableItems(rawItemData),
            ParseUpgradePools(rawUpgradeData));
    }

    private sealed record Snapshot(
        string Version,
        IReadOnlyDictionary<string, JsonNode> Documents,
        IReadOnlyCollection<GameAbility> AbilityTemplates,
        RunTuning RunTuning,
        IReadOnlyList<ItemTemplate> DroppableItems,
        IReadOnlyDictionary<string, List<AbilityUpgrade>> AbilityUpgradePools);
}
