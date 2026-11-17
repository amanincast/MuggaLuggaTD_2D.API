using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Reclaiming phase 4 (future-work item 9; Mike 2026-11-16 "I would trust your initial numbers"): a steward gives the
/// land it oversees a bonus by its class, growing with its level. Warrior: hold. Archer: goods. Mage: gold. Cleric: resolve.
/// </summary>
public class StewardBonusTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeGameContent _content = new()
    {
        Signatures = new[]
        {
            new SignatureDefinition { SignatureId = "sig-mage", Class = "Mage" },
            new SignatureDefinition { SignatureId = "sig-warrior", Class = "Warrior" },
        },
        RecruitSheets = new[] { new RecruitSheet { Sheet = "TestHero", Class = "Cleric" } },
    };

    private GoldService Gold => new(_db, new FakeSessionLog(), NullLogger<GoldService>.Instance);
    private WorldGarrisonService Garrisons => new(_db, _content, Gold, new FakeSessionLog(), NullLogger<WorldGarrisonService>.Instance,
        new WarLogService(_db, new FakeHubContext(), NullLogger<WarLogService>.Instance, new FakeClock()));

    private const string Hero = "steward";

    public void Dispose() => _db.Dispose();

    /// <summary>Wild land of tier 2 (steward level 9) held by the player, its steward standing at the keep.</summary>
    private static WorldRegionData Stewarded(string? cls, int level, int tier = 2)
    {
        var region = TestWorld.Region("r2", 2, 0, LocationOwnership.Player, TestIds.Player, tier: tier);
        region.StewardNeeded = true;
        region.StewardId = Hero;
        region.StewardClass = cls;
        region.StewardLevel = level;
        region.SiteOverrides[TestWorld.KeepIn(region)] = new SiteOverride
        {
            GarrisonCharacterIds = new List<string> { Hero }, GarrisonPower = 400f,
        };
        return region;
    }

    // -----------------------------------------------------------------
    // The rule
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("Warrior", StewardCalling.Warden)]
    [InlineData("archer", StewardCalling.Huntmaster)]
    [InlineData("Mage", StewardCalling.Magister)]
    [InlineData("Cleric", StewardCalling.Chaplain)]
    [InlineData("Bard", StewardCalling.None)]
    [InlineData(null, StewardCalling.None)]
    public void EachClass_HasItsCalling(string? cls, StewardCalling calling) =>
        Assert.Equal(calling, StewardBonusRules.CallingOf(cls!));

    [Theory]
    [InlineData(StewardCalling.Warden, 9, 0.10)]
    [InlineData(StewardCalling.Warden, 14, 0.15)]
    [InlineData(StewardCalling.Warden, 19, 0.20)]
    [InlineData(StewardCalling.Warden, 40, 0.20)]
    [InlineData(StewardCalling.Huntmaster, 9, 0.15)]
    [InlineData(StewardCalling.Huntmaster, 19, 0.30)]
    [InlineData(StewardCalling.Magister, 12, 0.195)]
    [InlineData(StewardCalling.Magister, 10, 0.165)]
    [InlineData(StewardCalling.Chaplain, 9, 0.30)]
    [InlineData(StewardCalling.Chaplain, 19, 0.50)]
    public void TheBonus_GrowsFromTheLandsLevel_ToTenAbove(StewardCalling calling, int level, double bonus) =>
        Assert.Equal(bonus, StewardBonusRules.Bonus(calling, level, tier: 2), 6);

    [Fact]
    public void OnlyAStewardStandingOnHeldLand_GivesItsBonus()
    {
        var region = Stewarded("Mage", 9);
        Assert.Equal(StewardCalling.Magister, StewardBonusRules.CallingAt(region));

        region.SiteOverrides.Clear();   // appointed, but gone from the keep
        Assert.Equal(StewardCalling.None, StewardBonusRules.CallingAt(region));
        Assert.Equal(1.0, StewardBonusRules.GoldFactor(region));

        var seatRing = Stewarded("Mage", 9);
        seatRing.StewardNeeded = false;  // the seat's ring names no steward
        Assert.Equal(StewardCalling.None, StewardBonusRules.CallingAt(seatRing));

        var fallen = Stewarded("Mage", 9);
        fallen.Horde = HordeRules.NewReturnedHorde(2, DateTime.UtcNow);
        Assert.Equal(StewardCalling.None, StewardBonusRules.CallingAt(fallen));
    }

    [Fact]
    public void AWarden_LiftsTheHold_AgainstSiegesRaidsAndTheHorde()
    {
        var plain = Stewarded("Mage", 9);
        var warden = Stewarded("Warrior", 19);
        var all = new List<WorldRegionData> { warden };
        long hold = RegionHoldCalculator.AssessRegion(plain, new List<WorldRegionData> { plain }, 0).Hold;
        long lifted = RegionHoldCalculator.AssessRegion(warden, all, 0).Hold;
        Assert.InRange(lifted, (long)(hold * 1.20) - 1, (long)(hold * 1.20) + 1);
        Assert.True(HordeReturnRules.Hold(warden, all, 0) > HordeReturnRules.Hold(plain, new List<WorldRegionData> { plain }, 0));
    }

    [Fact]
    public void AMagister_LiftsTheGold_ButNotTheSeasonPoints()
    {
        var plain = Stewarded("Warrior", 9);
        var magister = Stewarded("Mage", 9);
        Assert.Equal(GoldRules.RateFor(plain) * 1.15, GoldRules.RateFor(magister), 6);
        Assert.Equal(SeasonScoreRules.RateFor(plain), SeasonScoreRules.RateFor(magister));
    }

    [Fact]
    public void AHuntmaster_LiftsTheGoods_OnlyOnItsLand()
    {
        Assert.Equal(1.15, StewardBonusRules.GoodsFactor(Stewarded("Archer", 9)), 6);
        Assert.Equal(1.0, StewardBonusRules.GoodsFactor(Stewarded("Cleric", 9)));
    }

    [Fact]
    public void AChaplain_SoftensRaidsAndTheTide_AndBuildsResolveBackFaster()
    {
        var chaplain = Stewarded("Cleric", 19);
        chaplain.Resolve = 60;

        var plain = RaidResolver.Resolve(5000, 1000, 60, 20);
        var softened = RaidResolver.Resolve(5000, 1000, 60, 20, StewardBonusRules.ResolveLossFactor(chaplain));
        Assert.True(plain.AttackerWins && softened.AttackerWins);
        Assert.Equal((int)Math.Round(plain.ResolveDamage * 0.5, MidpointRounding.AwayFromZero), softened.ResolveDamage);

        Assert.Equal(60 - FactionSignatureRules.TideResolveLoss / 2, FactionSignatureRules.Flooded(chaplain));
        Assert.Equal(15, StewardBonusRules.RaiseGain(chaplain, RegionResolveRules.RestoredPerClear));
        Assert.Equal(10, StewardBonusRules.RaiseGain(Stewarded("Mage", 19), RegionResolveRules.RestoredPerClear));
    }

    [Fact]
    public void ItsLine_SaysWhatItGives()
    {
        Assert.Equal("Warden · hold +15%", StewardBonusRules.DescribeAt(Stewarded("Warrior", 14)));
        Assert.Equal("Chaplain · resolve loss -30%, restored +30%", StewardBonusRules.DescribeAt(Stewarded("Cleric", 9)));
        Assert.Equal("", StewardBonusRules.DescribeAt(Stewarded(null, 9)));
        Assert.Equal("Magister · gold +17%", StewardBonusRules.DescribeAt(Stewarded("Mage", 10)));   // 16.5%, half up
    }

    // -----------------------------------------------------------------
    // The server
    // -----------------------------------------------------------------

    [Fact]
    public void TheBlob_KeepsTheStewardsClassAndLevel_AndACaptureClearsThem()
    {
        var node = WorldRegionBlob.FindRegion(WorldRegionBlob.BuildWorld(1, new[] { Stewarded("Archer", 12) }), "r2")!;
        var read = WorldRegionBlob.ReadRegion(node);
        Assert.Equal("Archer", read.StewardClass);
        Assert.Equal(12, read.StewardLevel);

        WorldRegionBlob.SetSteward(node, null);
        read = WorldRegionBlob.ReadRegion(node);
        Assert.Null(read.StewardId);
        Assert.Null(read.StewardClass);
        Assert.Equal(0, read.StewardLevel);
    }

    [Theory]
    [InlineData("sig-mage", "Mage")]
    [InlineData("sig-warrior", "Warrior")]
    [InlineData(null, "Cleric")]   // no signature: its recruit sheet's class
    public async Task StationingTheSteward_RecordsItsClassAndLevel(string? signature, string cls)
    {
        var region = TestWorld.Region("r2", 2, 0, LocationOwnership.Player, TestIds.Player, tier: 2);
        region.StewardNeeded = true;
        var seat = TestWorld.OwnedBy(TestIds.Player, "r0", tier: 1);
        seat.IsCapital = true;
        var instance = await _db.AddInstanceAsync(TestIds.Owner, TestIds.Owner);
        await _db.AddWorldAsync(instance.Id, TestWorld.Blob(seat, TestWorld.Region("r1", 1, 0, LocationOwnership.Player, TestIds.Player, tier: 1), region));
        var hero = TestSave.Character(Hero, 13);
        hero.SignatureId = signature;
        await _db.AddPlayerSaveAsync(instance.Id, TestIds.Player, TestSave.ToJson(TestSave.Roster(hero)));

        var (set, response, world) = await Garrisons.SetAsync(instance.Id, TestIds.Player,
            new GarrisonRequest(TestWorld.KeepIn(region), new List<string> { Hero }, SharedContract.Version));

        Assert.True(set.Succeeded, set.Message);
        Assert.Equal(Hero, response!.StewardId);
        Assert.Equal(cls, response.StewardClass);
        Assert.Equal(13, response.StewardLevel);
        var stored = WorldRegionBlob.ReadAllRegions(world!).Single(r => r.RegionId == "r2");
        Assert.Equal(cls, stored.StewardClass);
        Assert.Equal(13, stored.StewardLevel);
        Assert.NotEqual(StewardCalling.None, StewardBonusRules.CallingAt(stored));
    }
}
