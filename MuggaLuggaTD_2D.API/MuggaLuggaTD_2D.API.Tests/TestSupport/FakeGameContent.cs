using System.Text.Json.Nodes;
using Abilities.Models;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Tests.TestSupport;

/// <summary>
/// Game content under a test's control.
///
/// The real provider reads GameContent/*.json at startup and hashes it. Tests want to say "this
/// ability offers exactly these two upgrades" without editing shipped content, so they supply their
/// own. <see cref="IGameContentProvider"/> being an interface is what makes that possible.
/// </summary>
public class FakeGameContent : IGameContentProvider
{
    public string Version { get; set; } = "test-content";

    public IReadOnlyDictionary<string, JsonNode> Documents { get; set; } = new Dictionary<string, JsonNode>();

    public IReadOnlyCollection<GameAbility> AbilityTemplates { get; set; } = Array.Empty<GameAbility>();

    /// <summary>Defaults to the shipped tuning's own defaults, so reward maths is exercised as tuned.</summary>
    public RunTuning RunTuning { get; set; } = new();

    /// <summary>Empty by default: most tests care about the XP budget, not the drop roll.</summary>
    public IReadOnlyList<ItemTemplate> DroppableItems { get; set; } = Array.Empty<ItemTemplate>();

    public IReadOnlyDictionary<string, List<AbilityUpgrade>> AbilityUpgradePools { get; set; }
        = new Dictionary<string, List<AbilityUpgrade>>();

    /// <summary>Empty by default: a run then pays no materials, which most tests do not care about.</summary>
    public IReadOnlyList<MaterialTemplate> Materials { get; set; } = Array.Empty<MaterialTemplate>();

    /// <summary>Empty by default: only the signature tests care what rolls exist.</summary>
    public IReadOnlyList<SignatureDefinition> Signatures { get; set; } = Array.Empty<SignatureDefinition>();

    /// <summary>Content offering <paramref name="upgrades"/> for the named ability and nothing else.</summary>
    public static FakeGameContent WithUpgradePool(string abilityLinkName, params AbilityUpgrade[] upgrades)
    {
        return new FakeGameContent
        {
            AbilityUpgradePools = new Dictionary<string, List<AbilityUpgrade>>
            {
                [abilityLinkName] = upgrades.ToList()
            }
        };
    }
}
