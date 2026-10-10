using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using MuggaLuggaTD_2D.API.Controllers;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Hardening 1 and 2: nobody may write the shared world from outside the game, and nobody becomes a
/// member of a realm except through a join its access type and capacity allow.
/// </summary>
public class RealmMembershipTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestDb.Create();

    public void Dispose() => _db.Dispose();

    private RealmMembershipService Membership() => new(_db);

    private async Task<GameInstance> RealmAsync(GameInstanceAccessType access, int capacity = 10)
    {
        var realm = await _db.AddInstanceAsync();
        realm.AccessType = access;
        realm.Capacity = capacity;
        _db.Users.Add(new ApplicationUser { Id = TestIds.Player, UserName = "Player" });
        await _db.SaveChangesAsync();
        return realm;
    }

    // -----------------------------------------------------------------
    // Joining
    // -----------------------------------------------------------------

    [Fact]
    public async Task APublicRealm_LetsAnyoneJoin_AndMakesThemAMember()
    {
        var realm = await RealmAsync(GameInstanceAccessType.Public);

        Assert.Equal(JoinOutcome.Joined, await Membership().JoinAsync(realm.Id, TestIds.Player));
        Assert.True(await Membership().IsMemberAsync(realm.Id, TestIds.Player));
    }

    [Fact]
    public async Task JoiningTwice_ChangesNothing()
    {
        var realm = await RealmAsync(GameInstanceAccessType.Public);
        await Membership().JoinAsync(realm.Id, TestIds.Player);

        Assert.Equal(JoinOutcome.AlreadyMember, await Membership().JoinAsync(realm.Id, TestIds.Player));
        Assert.Equal(1, await _db.PlayerGameData.CountAsync(p => p.UserId == TestIds.Player));
    }

    [Fact]
    public async Task AnExistingMember_IsUnaffected_WhateverTheRealmBecame()
    {
        var realm = await RealmAsync(GameInstanceAccessType.InviteOnly, capacity: 1);
        await _db.AddPlayerSaveAsync(realm.Id, TestIds.Player, """{"Characters":[]}""");

        Assert.Equal(JoinOutcome.AlreadyMember, await Membership().JoinAsync(realm.Id, TestIds.Player));
        Assert.True(await Membership().IsMemberAsync(realm.Id, TestIds.Player));
    }

    [Fact]
    public async Task ASoloRealm_AdmitsOnlyItsOwner()
    {
        var realm = await RealmAsync(GameInstanceAccessType.InviteOnly);

        Assert.Equal(JoinOutcome.NotAllowed, await Membership().JoinAsync(realm.Id, TestIds.Player));
        Assert.Equal(JoinOutcome.Joined, await Membership().JoinAsync(realm.Id, TestIds.Owner));
        Assert.False(await Membership().IsMemberAsync(realm.Id, TestIds.Player));
    }

    [Fact]
    public async Task AFriendsRealm_AdmitsTheOwnersFriends_AndNoOneElse()
    {
        var realm = await RealmAsync(GameInstanceAccessType.FriendsAndInviteOnly);
        Assert.Equal(JoinOutcome.NotAllowed, await Membership().JoinAsync(realm.Id, TestIds.Player));

        _db.Friendships.Add(new Friendship
        {
            RequesterId = TestIds.Player, AddresseeId = TestIds.Owner, Status = FriendshipStatus.Pending
        });
        await _db.SaveChangesAsync();
        Assert.Equal(JoinOutcome.NotAllowed, await Membership().JoinAsync(realm.Id, TestIds.Player));

        (await _db.Friendships.SingleAsync()).Status = FriendshipStatus.Accepted;
        await _db.SaveChangesAsync();
        Assert.Equal(JoinOutcome.Joined, await Membership().JoinAsync(realm.Id, TestIds.Player));
    }

    [Fact]
    public async Task ABlock_EitherWay_KeepsAPlayerOutOfAnOpenRealm()
    {
        var realm = await RealmAsync(GameInstanceAccessType.Public);
        _db.UserBlocks.Add(new UserBlock { BlockerId = TestIds.Owner, BlockedUserId = TestIds.Player });
        await _db.SaveChangesAsync();

        Assert.Equal(JoinOutcome.NotAllowed, await Membership().JoinAsync(realm.Id, TestIds.Player));
    }

    [Fact]
    public async Task AFullRealm_RefusesANewcomer_CountingAnOwnerWhoHasNotSaved()
    {
        // Capacity 2: the owner (no save yet) and one other.
        var realm = await RealmAsync(GameInstanceAccessType.Public, capacity: 2);
        _db.Users.Add(new ApplicationUser { Id = TestIds.Rival, UserName = "Rival" });
        await _db.SaveChangesAsync();

        Assert.Equal(JoinOutcome.Joined, await Membership().JoinAsync(realm.Id, TestIds.Player));
        Assert.Equal(JoinOutcome.Full, await Membership().JoinAsync(realm.Id, TestIds.Rival));
        Assert.Equal(JoinOutcome.Joined, await Membership().JoinAsync(realm.Id, TestIds.Owner));
    }

    [Fact]
    public async Task ARealmThatDoesNotExist_CannotBeJoined() =>
        Assert.Equal(JoinOutcome.NotFound, await Membership().JoinAsync(Guid.NewGuid(), TestIds.Player));

    // -----------------------------------------------------------------
    // A save is not a join
    // -----------------------------------------------------------------

    [Fact]
    public async Task ANonMembersSave_IsRefused_AndMakesNoRow()
    {
        var realm = await RealmAsync(GameInstanceAccessType.Public);
        var log = new FakeSessionLog();

        var result = await SaveController(TestIds.Player, log).SaveMyPlayerData(
            realm.Id, new SavePlayerGameDataRequest(new { Characters = Array.Empty<object>() }));

        Assert.IsType<ForbidResult>(result.Result);
        Assert.False(await _db.PlayerGameData.AnyAsync());
        Assert.Single(log.Of("SAVE-DENY"));
    }

    [Fact]
    public async Task AMemberWhoHasNotSaved_HasNoSave_SoTheyStartAsANewPlayer()
    {
        var realm = await RealmAsync(GameInstanceAccessType.Public);
        await Membership().JoinAsync(realm.Id, TestIds.Player);

        var result = await SaveController(TestIds.Player, new FakeSessionLog()).GetMyPlayerData(realm.Id);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    // -----------------------------------------------------------------
    // The world is the server's
    // -----------------------------------------------------------------

    [Fact]
    public async Task AClientWorldWrite_IsRefusedOutsideDevelopment_AndLogged()
    {
        var realm = await RealmAsync(GameInstanceAccessType.Public);
        await _db.AddPlayerSaveAsync(realm.Id, TestIds.Player, "{}");
        var log = new FakeSessionLog();

        var controller = new WorldViewGameDataController(_db, new FakeHubContext(), null!, null!,
            new Env("Production"), log) { ControllerContext = As(TestIds.Player) };

        var result = await controller.SaveWorldViewGameData(realm.Id, new SaveWorldViewGameDataRequest(new { }));

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.False(await _db.WorldViewGameData.AnyAsync());
        Assert.Single(log.Of("WORLD-WRITE-DENY"));
    }

    // -----------------------------------------------------------------

    private PlayerGameDataController SaveController(string userId, ISessionLog log) =>
        // A refused save returns before validation, the Tavern or the ledger are asked.
        new(_db, null!, null!, log, null!) { ControllerContext = As(userId) };

    private static ControllerContext As(string userId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "test"))
        }
    };

    private sealed class Env : IWebHostEnvironment
    {
        public Env(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "MuggaLuggaTD_2D.API";
        public string ContentRootPath { get; set; } = string.Empty;
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
