using Enums;
using Items.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// A new player's starting heroes (Mike, playtest 2026-10-08): four Commons, one of each class, rolled
/// by the server so no two players start alike, and granted once.
/// </summary>
public class StarterRosterTests : IDisposable
{
    private static readonly string[] Classes = { "Archer", "Cleric", "Mage", "Warrior" };

    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly Guid _realm = Guid.NewGuid();
    private const string Player = "player-1";

    private static readonly FakeGameContent Content = new()
    {
        RecruitSheets = Classes.SelectMany(c => new[]
        {
            new RecruitSheet { Sheet = $"Ally_{c}_1", Class = c },
            new RecruitSheet { Sheet = $"Ally_Elf_{c}_1", Class = c },
        }).ToList(),
        Signatures = Classes.SelectMany(c => new[] { "a", "b" }.Select(k => new SignatureDefinition
        {
            SignatureId = $"{c.ToLowerInvariant()}_{k}", Class = c, BaseAbilityLinkName = $"{c}_{k}_1",
            AllowedAffinities = new List<AffinityTypes> { AffinityTypes.Physical, AffinityTypes.Fire, AffinityTypes.Water },
        })).ToList(),
        DroppableItems = new[] { new ItemTemplate { ItemName = "Cinder Blade", ItemType = ItemTypes.Weapon } },
    };

    public void Dispose() => _db.Dispose();

    private static TavernService ServiceOn(ApplicationDbContext db) =>
        new(db, Content, new MaterialWalletService(db, new FakeSessionLog(), NullLogger<MaterialWalletService>.Instance),
            new GoldService(db, new FakeSessionLog(), NullLogger<GoldService>.Instance), new FakeSessionLog(),
            NullLogger<TavernService>.Instance, items: new ItemLedgerService(db, new FakeSessionLog(), NullLogger<ItemLedgerService>.Instance));

    private TavernService Service => ServiceOn(_db);

    [Fact]
    public async Task ANewPlayer_GetsOneCommonOfEachClass_AsHires()
    {
        var (outcome, starters, _) = await Service.ClaimStartersAsync(_realm, Player);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(TavernService.StarterCount, starters.Count);
        Assert.Equal(Classes, starters.Select(s => s.CharacterClass).OrderBy(c => c, StringComparer.Ordinal));
        Assert.All(starters, s =>
        {
            Assert.Equal(CharacterRarity.Common, s.Rarity);
            Assert.StartsWith(s.CharacterClass.ToLowerInvariant() + "_", s.SignatureId);
            Assert.Contains(Content.RecruitSheets, sheet => sheet.Sheet == s.Sheet && sheet.Class == s.CharacterClass);
            Assert.False(string.IsNullOrEmpty(s.Name));
        });
        Assert.Equal(starters.Select(s => s.CharacterId).OrderBy(i => i),
            (await _db.HiredCharacters.Select(h => h.CharacterId).ToListAsync()).OrderBy(i => i));
    }

    [Fact]
    public async Task AskedAgainBeforeTheSave_TheSameHeroesComeBack()
    {
        var (_, first, gear) = await Service.ClaimStartersAsync(_realm, Player);
        var (outcome, again, gearAgain) = await Service.ClaimStartersAsync(_realm, Player);

        Assert.True(outcome.Succeeded);
        Assert.Equal(first.Select(s => s.CharacterId).OrderBy(i => i), again.Select(s => s.CharacterId).OrderBy(i => i));
        Assert.Equal(TavernService.StarterCount, await _db.HiredCharacters.CountAsync());
        Assert.Equal(gear!.Id, gearAgain!.Id);
        Assert.Single(await _db.ItemGrants.ToListAsync());
    }

    [Fact]
    public async Task ANewPlayer_GetsOnePieceOfCommonGear_OnTheLedger()
    {
        // First Steps asks for a piece of gear second; without this the first drop came only with the fifth.
        var (_, _, gear) = await Service.ClaimStartersAsync(_realm, Player);

        Assert.NotNull(gear);
        Assert.Equal("Cinder Blade", gear!.ItemName);
        Assert.Equal(ItemRarityTypes.Common, gear.Rarity);
        var grant = Assert.Single(await _db.ItemGrants.ToListAsync());
        Assert.Equal(gear.Id, grant.ItemId);
        Assert.Equal(TavernService.StarterGearSource, grant.Source);
    }

    [Fact]
    public async Task APlayerWithARoster_GetsNoStarters()
    {
        await _db.AddPlayerSaveAsync(_realm, Player, TestSave.ToJson(TestSave.Roster(TestSave.Character("hero-1"))));

        var (outcome, starters, _) = await Service.ClaimStartersAsync(_realm, Player);

        Assert.Equal(TavernError.AlreadyStarted, outcome.Error);
        Assert.Empty(starters);
        Assert.Empty(await _db.HiredCharacters.ToListAsync());
    }

    [Fact]
    public async Task FirstLoadsAtOnce_GrantOneRoster()
    {
        string store = $"starters-{Guid.NewGuid()}";
        var contexts = Enumerable.Range(0, 4).Select(_ => TestDb.Create(store)).ToList();
        try
        {
            await Task.WhenAll(contexts.Select(db => Task.Run(() => ServiceOn(db).ClaimStartersAsync(_realm, Player))));

            await using var check = TestDb.Create(store);
            Assert.Equal(TavernService.StarterCount, await check.HiredCharacters.CountAsync());
        }
        finally
        {
            foreach (var db in contexts) await db.DisposeAsync();
        }
    }

    [Fact]
    public void TwoPlayers_DoNotStartAlike()
    {
        // Four classes are fixed; everything else is rolled. Two rosters matching on every face,
        // signature and affinity would mean the roll is not reaching them.
        var a = TavernService.RollStarters(Content.RecruitSheets, Content.Signatures, new Random(1));
        var b = TavernService.RollStarters(Content.RecruitSheets, Content.Signatures, new Random(2));

        string Shape(List<RecruitRoll> rolls) =>
            string.Join(",", rolls.OrderBy(r => r.Class).Select(r => $"{r.Sheet}/{r.SignatureId}/{r.Affinity}"));
        Assert.NotEqual(Shape(a), Shape(b));
    }
}
