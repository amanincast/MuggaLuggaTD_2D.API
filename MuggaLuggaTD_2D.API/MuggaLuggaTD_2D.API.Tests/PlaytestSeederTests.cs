using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The playtest seed, run against the content that ships.
///
/// <para>The thing worth pinning is that the realm it builds is one the game would accept: the save
/// it writes directly must come through <see cref="PlayerSaveValidator.ReconcileRoster"/> untouched.
/// If it did not, the first save the client made would quietly strip the seeded rolls, and the
/// playtest would be testing something other than what the seed printed.</para>
/// </summary>
public class PlaytestSeederTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeSessionLog _log = new();
    private readonly GameContentProvider _content = new(new ShippedContent(), NullLogger<GameContentProvider>.Instance);

    public void Dispose() => _db.Dispose();

    private PlaytestSeeder Seeder()
    {
        var gold = new GoldService(_db, _log, NullLogger<GoldService>.Instance);
        var provisioning = new WorldProvisioningService(_db, NullLogger<WorldProvisioningService>.Instance);

        return new PlaytestSeeder(
            _db, _content, provisioning,
            new SeasonScoreService(_db, gold, provisioning, new FakeHubContext(), _log, NullLogger<SeasonScoreService>.Instance),
            new MaterialWalletService(_db, _log, NullLogger<MaterialWalletService>.Instance),
            gold,
            NullLogger<PlaytestSeeder>.Instance);
    }

    private async Task<ApplicationUser> AddUserAsync()
    {
        var user = new ApplicationUser
        {
            Id = "tester", UserName = "tester", NormalizedUserName = "TESTER",
            Email = "tester@test.local", NormalizedEmail = "TESTER@TEST.LOCAL", DisplayName = "tester"
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task TheSeededSaveIsOneTheValidatorLeavesAlone()
    {
        await AddUserAsync();
        var result = await Seeder().SeedAsync("tester", "Playtest");

        var row = await _db.PlayerGameData.SingleAsync(p => p.GameInstanceId == result.RealmId);
        var hired = await _db.HiredCharacters.Where(h => h.GameInstanceId == result.RealmId).ToListAsync();
        var save = JsonNode.Parse(row.GameData);

        var reconciliation = new PlayerSaveValidator(_content).ReconcileRoster(save, hired);

        Assert.False(reconciliation.Changed, string.Join(", ", reconciliation.Details));
        Assert.Equal(PlaytestSeeder.Hires.Count, hired.Count);
        Assert.Equal(
            _content.RecruitSheets.Count(s => s.IsStarter()) + PlaytestSeeder.Hires.Count,
            save!["Characters"]!.AsArray().Count);
    }

    [Fact]
    public async Task ThePlayerIsSeatedWithAFullPurse()
    {
        var user = await AddUserAsync();
        var result = await Seeder().SeedAsync("tester@test.local", "Playtest");

        var world = await _db.WorldViewGameData.SingleAsync(w => w.GameInstanceId == result.RealmId);
        Assert.Contains(user.Id, world.GameData);

        var materials = await new MaterialWalletService(_db, _log, NullLogger<MaterialWalletService>.Instance)
            .ReadAsync(result.RealmId, user.Id);
        Assert.Equal(_content.Materials.Count, materials.Count);
        Assert.Contains(materials, m => m.MaterialName.EndsWith(" Crystal") && m.Quantity == PlaytestSeeder.CrystalsEach);

        var gold = await new GoldService(_db, _log, NullLogger<GoldService>.Instance).BalanceAsync(result.RealmId, user.Id);
        Assert.True(gold >= PlaytestSeeder.GoldGranted);
    }

    [Fact]
    public async Task ReseedingReplacesTheRealmRatherThanAddingOne()
    {
        await AddUserAsync();
        var first = await Seeder().SeedAsync("tester", "Playtest");
        var second = await Seeder().SeedAsync("tester", "Playtest");

        Assert.Equal(1, second.RealmsReplaced);
        Assert.NotEqual(first.RealmId, second.RealmId);
        Assert.Equal(1, await _db.GameInstances.CountAsync(g => g.Name == "Playtest"));
    }

    [Fact]
    public async Task AnUnknownUserIsRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Seeder().SeedAsync("nobody", "Playtest"));
    }

    /// <summary>Points the real content provider at the API project's GameContent folder.</summary>
    private sealed class ShippedContent : IWebHostEnvironment
    {
        public ShippedContent()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "MuggaLuggaTD_2D.API", "GameContent")))
                dir = dir.Parent;

            ContentRootPath = Path.Combine(dir!.FullName, "MuggaLuggaTD_2D.API");
        }

        public string ContentRootPath { get; set; }
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "MuggaLuggaTD_2D.API";
        public string EnvironmentName { get; set; } = "Development";
    }
}
