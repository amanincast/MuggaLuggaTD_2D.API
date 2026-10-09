using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The inbox (the Inbox spec, Mike 2026-10-08): one letter per event per player, raids on a region folded
/// within the hour, at most 200 kept, and the war log's lines turned into the letters of the players in them.
/// </summary>
public class LetterTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeHubContext _hub = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc));
    private Guid _realm;

    public void Dispose() => _db.Dispose();

    private LetterService Letters => new(_db, _hub, NullLogger<LetterService>.Instance, _clock);
    private WarLogService WarLog => new(_db, _hub, NullLogger<WarLogService>.Instance, _clock, Letters);

    private async Task RealmAsync()
    {
        var instance = await _db.AddInstanceAsync();
        _db.Users.Add(new ApplicationUser { Id = TestIds.Player, UserName = "Mira", DisplayName = "Mira" });
        _db.Users.Add(new ApplicationUser { Id = TestIds.Rival, UserName = "Brakka", DisplayName = "Brakka" });
        await _db.SaveChangesAsync();
        _realm = instance.Id;
    }

    private Task<List<Letter>> MineAsync(string user = TestIds.Player) =>
        _db.Letters.AsNoTracking().Where(l => l.UserId == user).OrderBy(l => l.OccurredAt).ToListAsync();

    // -----------------------------------------------------------------
    // Sending
    // -----------------------------------------------------------------

    [Fact]
    public async Task TheSameEventSentTwice_IsOneLetter()
    {
        await RealmAsync();
        await Letters.SendAsync(_realm, TestIds.Player, LetterKind.CompanyArrived, _clock.UtcNow, "party:1:arrived", "r1");
        await Letters.SendAsync(_realm, TestIds.Player, LetterKind.CompanyArrived, _clock.UtcNow, "party:1:arrived", "r1");

        Assert.Single(await MineAsync());
    }

    [Fact]
    public async Task RaidsOnOneRegionWithinTheHour_FoldIntoOneLetter_AndAreUnreadAgain()
    {
        await RealmAsync();
        await Letters.SendAsync(_realm, TestIds.Player, LetterKind.RaidOnYou, _clock.UtcNow, "a", "r1", detail: "80 → 70");
        await Letters.MarkReadAsync(_realm, TestIds.Player, null, all: true);
        await Letters.SendAsync(_realm, TestIds.Player, LetterKind.RaidOnYou, _clock.UtcNow.AddMinutes(40), "b", "r1", detail: "70 → 60");
        await Letters.SendAsync(_realm, TestIds.Player, LetterKind.RaidOnYou, _clock.UtcNow.AddMinutes(30), "c", "r2");
        await Letters.SendAsync(_realm, TestIds.Player, LetterKind.RaidOnYou, _clock.UtcNow.AddMinutes(90), "d", "r1");

        var letters = await MineAsync();
        Assert.Equal(3, letters.Count);
        var first = letters[0];
        Assert.Equal(2, first.Count);
        Assert.Equal("70 → 60", first.Detail);
        Assert.Null(first.ReadAt);
        Assert.Equal(3, (await Letters.SummaryAsync(_realm, TestIds.Player)).Unread);
    }

    [Fact]
    public async Task ALetterIsPushedToItsPlayerOnly()
    {
        await RealmAsync();
        await Letters.SendAsync(_realm, TestIds.Player, LetterKind.QuestReady, _clock.UtcNow, "q1");

        var sent = Assert.Single(_hub.Sent);
        Assert.Equal(TestIds.Player, sent.Group);
        Assert.Equal("LetterAdded", sent.Method);
    }

    [Fact]
    public async Task AFactionNeverGetsALetter()
    {
        await RealmAsync();
        await Letters.SendAsync(_realm, "faction:Grimjaw", LetterKind.RaidRepelled, _clock.UtcNow, "x");
        Assert.Empty(await _db.Letters.ToListAsync());
    }

    // -----------------------------------------------------------------
    // Keeping
    // -----------------------------------------------------------------

    [Fact]
    public void TheCap_DropsOldReadLettersFirst_ThenTheOldestRead_ThenTheOldestUnread()
    {
        var now = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
        var letters = new List<(string Id, DateTime At, bool Read)>
        {
            ("unread-old", now.AddDays(-20), false),
            ("read-recent", now.AddDays(-1), true),
            ("read-old", now.AddDays(-10), true),
            ("unread-new", now, false),
            ("ancient", now.AddDays(-31), false),
        };

        var drop = LetterRules.ToDrop(letters, l => l.At, l => l.Read, now, cap: 2).Select(l => l.Id).ToList();

        Assert.Equal(new[] { "ancient", "read-old", "read-recent" }, drop);
        Assert.Equal(new[] { "ancient", "read-old", "read-recent", "unread-old" },
            LetterRules.ToDrop(letters, l => l.At, l => l.Read, now, cap: 1).Select(l => l.Id));
    }

    [Fact]
    public async Task APlayerKeepsAtMostTheCap()
    {
        await RealmAsync();
        for (int i = 0; i < LetterRules.Cap + 5; i++)
            await Letters.SendAsync(_realm, TestIds.Player, LetterKind.CompanyArrived, _clock.UtcNow.AddMinutes(i), $"k{i}");

        var letters = await MineAsync();
        Assert.Equal(LetterRules.Cap, letters.Count);
        Assert.Equal("k5", letters[0].DedupKey);
    }

    [Fact]
    public async Task MarkingRead_ChangesOnlyThatPlayersLettersInThatRealm()
    {
        await RealmAsync();
        await Letters.SendAsync(_realm, TestIds.Player, LetterKind.CompanyArrived, _clock.UtcNow, "a");
        await Letters.SendAsync(_realm, TestIds.Player, LetterKind.CompanyArrived, _clock.UtcNow, "b");
        await Letters.SendAsync(_realm, TestIds.Rival, LetterKind.CompanyArrived, _clock.UtcNow, "a");
        var a = (await MineAsync()).First(l => l.DedupKey == "a");

        Assert.Equal(1, await Letters.MarkReadAsync(_realm, TestIds.Player, new[] { a.Id }, all: false));
        Assert.Equal(1, (await Letters.SummaryAsync(_realm, TestIds.Player)).Unread);
        Assert.Equal(1, await Letters.MarkReadAsync(_realm, TestIds.Player, null, all: true));
        Assert.Equal(0, (await Letters.SummaryAsync(_realm, TestIds.Player)).Unread);
        Assert.Equal(1, (await Letters.SummaryAsync(_realm, TestIds.Rival)).Unread);
    }

    // -----------------------------------------------------------------
    // From the war log
    // -----------------------------------------------------------------

    [Fact]
    public async Task ALandedRaid_IsTheDefendersLetter_WithTheRaidersName()
    {
        await RealmAsync();
        await WarLog.RecordAsync(_realm, WarLogKind.RaidLanded, TestIds.Rival, TestIds.Player, "r1", "resolve 80 → 70");

        var letter = Assert.Single(await _db.Letters.ToListAsync());
        Assert.Equal(TestIds.Player, letter.UserId);
        Assert.Equal(nameof(LetterKind.RaidOnYou), letter.Kind);
        Assert.Equal("Brakka", letter.ActorName);
        Assert.Equal("resolve 80 → 70", letter.Detail);
    }

    [Fact]
    public async Task AFactionRaid_WritesTheDefendersLetter_WithTheFactionsName()
    {
        await RealmAsync();
        await WarLog.RecordAsync(_realm, WarLogKind.RaidRepelled, "faction:Grimjaw", TestIds.Player, "r1",
            actorName: "The Grimjaw");

        var letter = Assert.Single(await _db.Letters.ToListAsync());
        Assert.Equal(nameof(LetterKind.RaidRepelled), letter.Kind);
        Assert.Equal("The Grimjaw", letter.ActorName);
    }

    [Fact]
    public async Task AWonSiege_WritesBothSides_AndARansom_WritesTheCaptor()
    {
        await RealmAsync();
        await WarLog.RecordAsync(_realm, WarLogKind.SiegeWon, TestIds.Player, TestIds.Rival, "r1", "2 champions taken prisoner");
        await WarLog.RecordAsync(_realm, WarLogKind.RansomPaid, TestIds.Rival, TestIds.Player, "r1", "2:1000");

        var player = await MineAsync(TestIds.Player);
        Assert.Equal(new[] { nameof(LetterKind.YourSiegeResult), nameof(LetterKind.PrisonersRansomed) }, player.Select(l => l.Kind));
        Assert.Equal("won|2 champions taken prisoner", player[0].Detail);
        Assert.Equal("Brakka", player[0].ActorName);
        var rival = Assert.Single(await MineAsync(TestIds.Rival));
        Assert.Equal(nameof(LetterKind.SiegeResultOnYou), rival.Kind);
        Assert.StartsWith("lost", rival.Detail);
    }

    [Fact]
    public async Task RealmNews_WithNoPlayerInIt_IsNobodysLetter()
    {
        await RealmAsync();
        await WarLog.RecordAsync(_realm, WarLogKind.Expanded, "faction:Grimjaw", null, "r9", actorName: "The Grimjaw");
        await WarLog.RecordAsync(_realm, WarLogKind.Fortified, TestIds.Player, null, "r1", "2");

        Assert.Empty(await _db.Letters.ToListAsync());
    }
}
