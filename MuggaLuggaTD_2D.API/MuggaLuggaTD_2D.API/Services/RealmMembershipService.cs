using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

public enum JoinOutcome
{
    /// <summary>A new member: their membership row was made.</summary>
    Joined,

    /// <summary>Already a member (or the owner with a row): nothing changed.</summary>
    AlreadyMember,

    NotFound,

    /// <summary>The realm's access type does not let this player in.</summary>
    NotAllowed,

    /// <summary>The realm holds as many members as its capacity.</summary>
    Full
}

/// <summary>
/// Who may join a realm (Hardening 2). Before this, posting a first save was the join: it made the
/// <see cref="PlayerGameData"/> row every access check trusts, and asked only whether the realm
/// existed, so anyone could walk into a private or solo realm and the Bazaar treated them as one of
/// its own. Access type and capacity were stored and never read.
///
/// <list type="bullet">
/// <item><b>Public:</b> anyone, unless they and the owner have blocked one another.</item>
/// <item><b>FriendsAndInviteOnly:</b> the owner's accepted friends (realm invites do not exist yet).</item>
/// <item><b>InviteOnly</b> (shown as SOLO): the owner alone, until invites exist.</item>
/// </list>
///
/// <para>The same rules decide what <c>GET gameinstance/browse</c> lists, so a realm that is shown
/// as open can always be joined while it has room. Capacity counts members: every row, plus the
/// owner if they have not saved yet.</para>
///
/// <para>A membership row made here holds <see cref="NoSaveYet"/> until the first save, and
/// <c>GET playerdata/me</c> answers it as "no save", which is what starts a new player.</para>
/// </summary>
public class RealmMembershipService
{
    /// <summary>The blob of a member who has joined and not yet saved.</summary>
    public const string NoSaveYet = "{}";

    private readonly ApplicationDbContext _context;

    public RealmMembershipService(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<bool> IsMemberAsync(Guid gameInstanceId, string userId) =>
        _context.GameInstances.AnyAsync(g => g.Id == gameInstanceId &&
            (g.OwnerId == userId || g.PlayerGameData.Any(p => p.UserId == userId)));

    public async Task<JoinOutcome> JoinAsync(Guid gameInstanceId, string userId)
    {
        var realm = await _context.GameInstances.FirstOrDefaultAsync(g => g.Id == gameInstanceId);
        if (realm == null) return JoinOutcome.NotFound;

        if (await _context.PlayerGameData.AnyAsync(p => p.GameInstanceId == gameInstanceId && p.UserId == userId))
            return JoinOutcome.AlreadyMember;

        bool owner = realm.OwnerId == userId;
        if (!owner)
        {
            if (!await MayEnterAsync(realm, userId)) return JoinOutcome.NotAllowed;

            int rows = await _context.PlayerGameData.CountAsync(p => p.GameInstanceId == gameInstanceId);
            bool ownerHasRow = await _context.PlayerGameData.AnyAsync(p =>
                p.GameInstanceId == gameInstanceId && p.UserId == realm.OwnerId);
            int members = rows + (ownerHasRow ? 0 : 1);
            if (members >= realm.Capacity) return JoinOutcome.Full;
        }

        _context.PlayerGameData.Add(new PlayerGameData
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            GameData = NoSaveYet,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Two joins at once: the unique (realm, user) index let one through, and that one is ours.
            return JoinOutcome.AlreadyMember;
        }

        return JoinOutcome.Joined;
    }

    private async Task<bool> MayEnterAsync(GameInstance realm, string userId)
    {
        bool blocked = await _context.UserBlocks.AnyAsync(b =>
            (b.BlockerId == userId && b.BlockedUserId == realm.OwnerId) ||
            (b.BlockerId == realm.OwnerId && b.BlockedUserId == userId));
        if (blocked) return false;

        return realm.AccessType switch
        {
            GameInstanceAccessType.Public => true,
            GameInstanceAccessType.FriendsAndInviteOnly => await _context.Friendships.AnyAsync(f =>
                f.Status == FriendshipStatus.Accepted &&
                ((f.RequesterId == userId && f.AddresseeId == realm.OwnerId) ||
                 (f.RequesterId == realm.OwnerId && f.AddresseeId == userId))),
            _ => false,
        };
    }
}
