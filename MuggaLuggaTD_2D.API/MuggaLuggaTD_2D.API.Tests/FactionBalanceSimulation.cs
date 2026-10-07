using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;
using Xunit.Abstractions;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The balance pass for the NPC factions (<c>docs/design/npc-factions.md</c> phase 4): a week of their
/// turns on real generated worlds, with the shipped temperaments, against players who never log in.
/// That is the worst case the protections exist for, so it is the one pinned.
///
/// <para>Each player holds their seat and the wild land within two hexes of it, as after a few days of
/// play, with nobody garrisoned. The report it writes (run with <c>--logger "console;verbosity=detailed"</c>,
/// and <c>FACTION_SIM_DAYS=28</c> for a season) is what the tuning was read from.</para>
/// </summary>
public class FactionBalanceSimulation
{
    private readonly ITestOutputHelper _output;

    public FactionBalanceSimulation(ITestOutputHelper output) => _output = output;

    private sealed class ContentRoot : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "MuggaLuggaTD_2D.API";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
    }

    private static string ApiProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "MuggaLuggaTD_2D.API", "GameContent")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "MuggaLuggaTD_2D.API");
    }

    private static readonly string[] Players = { "p1", "p2", "p3" };

    /// <summary>A generated world, with each player holding the wild land within two hexes of their seat.</summary>
    private static List<WorldRegionData> World(int seed)
    {
        var regions = WorldMapGenerator.Generate(seed, Players.Select(p => new WorldMapGenerator.PlayerSeat(p, p)).ToList());
        foreach (var seat in regions.Where(r => r.IsCapital).ToList())
        {
            foreach (var region in regions.Where(r => r.Ownership == LocationOwnership.Neutral
                                                      && HexCoord.Distance(r.Hex, seat.Hex) <= 2))
            {
                region.Ownership = LocationOwnership.Player;
                region.OwnerUserId = seat.OwnerUserId;
                region.OwnerDisplayName = seat.OwnerDisplayName;
                region.Faction = FactionId.Player;
            }
        }
        return regions;
    }

    private sealed record Week(int PlayerStart, int PlayerEnd, int CapitalsLost, Dictionary<FactionId, int> FactionStart,
        Dictionary<FactionId, int> FactionEnd, Dictionary<string, int> Deeds);

    private async Task<Week> RunAsync(int seed, int days)
    {
        using var db = TestDb.Create();
        var content = new GameContentProvider(new ContentRoot { ContentRootPath = ApiProject() }, NullLogger<GameContentProvider>.Instance);
        var hub = new FakeHubContext();
        var factions = new FactionService(db, new FakeSessionLog(), content,
            new WarLogService(db, hub, NullLogger<WarLogService>.Instance, new FakeClock()), null, hub, new Random(seed));

        var regions = World(seed);
        var instance = await db.AddInstanceAsync();
        await db.AddWorldAsync(instance.Id, WorldRegionBlob.BuildWorld(seed, regions));

        var start = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
        int PlayerLand(List<WorldRegionData> rs) => rs.Count(r => r.Ownership == LocationOwnership.Player);
        Dictionary<FactionId, int> Held(List<WorldRegionData> rs) =>
            FactionStrengthRules.All.ToDictionary(f => f, f => FactionStrengthRules.RegionsHeld(f, rs));

        var deeds = new Dictionary<string, int>();
        void Count(string deed) => deeds[deed] = deeds.TryGetValue(deed, out var n) ? n + 1 : 1;

        _output.WriteLine($"--- world {seed}: {regions.Count} regions, players hold {PlayerLand(regions)}, " +
                          string.Join(", ", Held(regions).Select(kv => $"{kv.Key} {kv.Value}")));
        await factions.ReadAsync(instance.Id, start);

        for (var at = start; at <= start.AddDays(days); at += FactionDecisionRules.SweepInterval)
        {
            foreach (var line in await factions.ActAsync(instance.Id, at))
            {
                if (line.Contains(" raided ")) Count(line.Contains("landed") ? "raid landed" : "raid repelled");
                else if (line.Contains("laid siege")) Count("siege laid");
                else if (line.Contains(" took ")) Count("siege took");
                else if (line.Contains("failed")) Count("siege failed");
                else if (line.Contains("called off")) Count("siege called off");
                else if (line.Contains("claimed")) Count("expanded");
                else if (line.Contains("fortified")) Count("fortified");
            }

            if ((at - start).TotalHours % 24 == 0 && at > start)
            {
                var now = WorldRegionBlob.ReadAllRegions(await db.ReadWorldAsync(instance.Id));
                var read = (await factions.ReadAsync(instance.Id, at))!;
                _output.WriteLine($"day {(at - start).TotalDays:0}: players {PlayerLand(now)}; " + string.Join("; ", read.Factions.Select(f =>
                    $"{f.Faction} {f.RegionsHeld} regions, {f.Strength:N0}/{f.Cap:N0} {f.Word}")));
            }
        }

        var end = WorldRegionBlob.ReadAllRegions(await db.ReadWorldAsync(instance.Id));
        _output.WriteLine("between factions: " + string.Join(", ", db.WarLog.AsNoTracking()
            .Where(e => e.SubjectUserId != null && e.SubjectUserId.StartsWith(FactionService.WarLogPrefix)).AsEnumerable()
            .GroupBy(e => e.Kind).Select(g => $"{g.Key} {g.Count()}")));
        _output.WriteLine("deeds: " + string.Join(", ", deeds.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
        return new Week(PlayerLand(regions), PlayerLand(end), Players.Length - end.Count(r => r.IsCapital && r.Ownership == LocationOwnership.Player),
            Held(regions), Held(end), deeds);
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(2002)]
    [InlineData(3003)]
    public async Task AWeekOfFactions_AgainstPlayersWhoNeverLogIn(int seed)
    {
        int days = int.TryParse(System.Environment.GetEnvironmentVariable("FACTION_SIM_DAYS"), out var d) ? d : 7;
        var week = await RunAsync(seed, days);

        // The seats are never touched.
        Assert.Equal(0, week.CapitalsLost);

        // Both factions still stand.
        foreach (var faction in FactionStrengthRules.All)
            Assert.True(week.FactionEnd[faction] > 0, $"{faction} was wiped out");

        // Absent players lose border land slowly, not wholesale: the muster, the gate and the dice
        // between them leave most of it standing after a week.
        if (days <= 7)
            Assert.True(week.PlayerEnd >= week.PlayerStart * 0.85, $"players fell from {week.PlayerStart} to {week.PlayerEnd} regions");
    }
}
