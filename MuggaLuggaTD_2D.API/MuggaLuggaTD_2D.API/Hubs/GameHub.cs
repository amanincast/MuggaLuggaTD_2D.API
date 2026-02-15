using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Hubs;

[Authorize]
public class GameHub : Hub
{
    private readonly ApplicationDbContext _context;

    public GameHub(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task JoinGameInstance(Guid gameInstanceId)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new HubException("User not authenticated.");

        var gameInstance = await _context.GameInstances
            .FirstOrDefaultAsync(g => g.Id == gameInstanceId)
            ?? throw new HubException("Game instance not found.");

        if (!await HasAccessToGameInstance(gameInstance, userId))
        {
            throw new HubException("You do not have access to this game instance.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, gameInstanceId.ToString());
    }

    public async Task LeaveGameInstance(Guid gameInstanceId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, gameInstanceId.ToString());
    }

    private async Task<bool> HasAccessToGameInstance(GameInstance gameInstance, string userId)
    {
        // Block check: if a block exists in either direction, deny access
        var blockExists = await _context.UserBlocks.AnyAsync(b =>
            (b.BlockerId == userId && b.BlockedUserId == gameInstance.OwnerId) ||
            (b.BlockerId == gameInstance.OwnerId && b.BlockedUserId == userId));

        if (blockExists)
            return false;

        // Owner or existing player always has access
        var isOwnerOrPlayer = gameInstance.OwnerId == userId
            || await _context.PlayerGameData.AnyAsync(p => p.GameInstanceId == gameInstance.Id && p.UserId == userId);

        if (isOwnerOrPlayer)
            return true;

        // Access based on AccessType
        return gameInstance.AccessType switch
        {
            GameInstanceAccessType.Public => true,
            GameInstanceAccessType.FriendsAndInviteOnly => await _context.Friendships.AnyAsync(f =>
                f.Status == FriendshipStatus.Accepted
                && ((f.RequesterId == userId && f.AddresseeId == gameInstance.OwnerId)
                    || (f.RequesterId == gameInstance.OwnerId && f.AddresseeId == userId))),
            GameInstanceAccessType.InviteOnly => false,
            _ => false
        };
    }
}
