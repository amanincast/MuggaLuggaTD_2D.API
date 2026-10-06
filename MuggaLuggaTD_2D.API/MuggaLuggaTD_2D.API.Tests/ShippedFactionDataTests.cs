using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The shipped <c>GameContent/Server/FactionData.json</c> loads through the real provider and names
/// every faction a world can seat. A typo there would otherwise surface only as a server that will
/// not start.
/// </summary>
public class ShippedFactionDataTests
{
    private sealed class ContentRoot : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "MuggaLuggaTD_2D.API";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
    }

    /// <summary>The API project's folder, found by walking up from the test binaries.</summary>
    private static string ApiProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "MuggaLuggaTD_2D.API", "GameContent")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "MuggaLuggaTD_2D.API");
    }

    [Fact]
    public void EveryFactionHasATemperament_ThatLeansSomewhere()
    {
        var content = new GameContentProvider(new ContentRoot { ContentRootPath = ApiProject() },
            NullLogger<GameContentProvider>.Instance);

        foreach (var faction in FactionStrengthRules.All)
        {
            var temperament = Assert.Single(content.Factions, f => f.Id == faction);
            Assert.False(string.IsNullOrWhiteSpace(temperament.Name));
            Assert.False(string.IsNullOrWhiteSpace(temperament.Lean));
            Assert.True(temperament.Aggression > 0);
            Assert.True(temperament.WeightOf(FactionAction.Raid) > 0);
        }

        // Grimjaw raid, the Ashkin entrench: a lean, not a limit (Mike, 2026-10-06).
        var grimjaw = content.Factions.Single(f => f.Id == FactionId.Grimjaw);
        var ashkin = content.Factions.Single(f => f.Id == FactionId.Ashkin);
        Assert.True(grimjaw.Raid > ashkin.Raid);
        Assert.True(ashkin.Fortify > grimjaw.Fortify);
    }
}
