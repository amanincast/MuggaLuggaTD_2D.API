using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;
using static MuggaLuggaTD_2D.API.Services.InviteCodeService;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The two gates in front of testers: an invite code to register, and a minimum game version.
/// </summary>
public class TesterAccessTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();
    private readonly FakeClock _clock = new();

    public void Dispose() => _db.Dispose();

    private InviteCodeService Invites() => new(_db, _clock);

    [Fact]
    public async Task AMintedCode_IsReadableAndUnique()
    {
        var made = await Invites().MintAsync(20, 1, "batch", null);

        Assert.Equal(20, made.Select(c => c.Code).Distinct().Count());
        Assert.All(made, c => Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", c.Code));
        Assert.All(made, c => Assert.DoesNotMatch("[01OILU]", c.Code));
    }

    [Theory]
    [InlineData("abcd-efgh", "ABCD-EFGH")]
    [InlineData("ABCDEFGH", "ABCD-EFGH")]
    [InlineData(" abcd efgh ", "ABCD-EFGH")]
    [InlineData(null, "")]
    public void ACode_IsReadHoweverItIsTyped(string? typed, string expected) =>
        Assert.Equal(expected, Normalize(typed));

    [Fact]
    public async Task ASingleUseCode_AdmitsOnePerson()
    {
        var code = (await Invites().MintAsync(1, 1, null, null))[0].Code;

        Assert.Equal(RedeemResult.Redeemed, await Invites().RedeemAsync(code.ToLowerInvariant()));
        Assert.Equal(RedeemResult.UsedUp, await Invites().RedeemAsync(code));
    }

    [Fact]
    public async Task ARefund_GivesTheUseBack()
    {
        var code = (await Invites().MintAsync(1, 1, null, null))[0].Code;
        await Invites().RedeemAsync(code);

        await Invites().RefundAsync(code);

        Assert.Equal(RedeemResult.Redeemed, await Invites().RedeemAsync(code));
    }

    [Fact]
    public async Task ACodeIsRefused_WhenMissingUnknownRevokedOrExpired()
    {
        var invites = Invites();
        var revoked = (await invites.MintAsync(1, 5, null, null))[0].Code;
        var expiring = (await invites.MintAsync(1, 5, null, days: 1))[0].Code;
        await invites.RevokeAsync(revoked);
        _clock.Advance(TimeSpan.FromDays(2));

        Assert.Equal(RedeemResult.Missing, await invites.RedeemAsync("  "));
        Assert.Equal(RedeemResult.Unknown, await invites.RedeemAsync("ZZZZ-ZZZZ"));
        Assert.Equal(RedeemResult.Revoked, await invites.RedeemAsync(revoked));
        Assert.Equal(RedeemResult.Expired, await invites.RedeemAsync(expiring));
    }

    [Theory]
    [InlineData("0.1.99", "0.1.100", true)]
    [InlineData("0.1.314", "0.1.300", false)]
    [InlineData("0.1.300", "0.1.300", false)]
    [InlineData("0.1.2-dev", "0.1.3", true)]
    [InlineData("1.0", "0.1.300", false)]      // the Editor's own version
    [InlineData("garbage", "0.1.300", false)]  // never locks anyone out
    public void AnOlderBuild_IsTurnedAway(string version, string minimum, bool older) =>
        Assert.Equal(older, ClientVersionGate.IsOlder(version, minimum));
}
