using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Controllers;

[ApiController]
[Route("api/friendships")]
[Authorize]
public class FriendshipController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public FriendshipController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpPost("requests")]
    public async Task<ActionResult<FriendRequestResponse>> SendFriendRequest([FromBody] SendFriendRequestRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (request.AddresseeId == userId)
            return BadRequest(new { message = "You cannot send a friend request to yourself." });

        var addressee = await _context.Users.FindAsync(request.AddresseeId);
        if (addressee == null)
            return NotFound(new { message = "User not found." });

        // Check if either user has blocked the other
        var blockExists = await _context.UserBlocks
            .AnyAsync(b =>
                (b.BlockerId == userId && b.BlockedUserId == request.AddresseeId) ||
                (b.BlockerId == request.AddresseeId && b.BlockedUserId == userId));
        if (blockExists)
            return BadRequest(new { message = "Cannot send friend request to this user." });

        // Check for existing friendship/request in either direction
        var existing = await _context.Friendships
            .FirstOrDefaultAsync(f =>
                (f.RequesterId == userId && f.AddresseeId == request.AddresseeId) ||
                (f.RequesterId == request.AddresseeId && f.AddresseeId == userId));

        if (existing != null)
        {
            if (existing.Status == FriendshipStatus.Accepted)
                return BadRequest(new { message = "You are already friends with this user." });
            if (existing.Status == FriendshipStatus.Pending)
                return BadRequest(new { message = "A pending friend request already exists between you and this user." });
            // If declined, allow re-sending by removing the old record
            _context.Friendships.Remove(existing);
        }

        var requester = await _context.Users.FindAsync(userId);

        var friendship = new Friendship
        {
            RequesterId = userId,
            AddresseeId = request.AddresseeId,
            Status = FriendshipStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Friendships.Add(friendship);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetPendingRequests), new FriendRequestResponse(
            friendship.Id,
            friendship.RequesterId,
            requester?.DisplayName,
            friendship.AddresseeId,
            addressee.DisplayName,
            friendship.Status,
            friendship.CreatedAt,
            friendship.UpdatedAt));
    }

    [HttpGet("requests/pending")]
    public async Task<ActionResult<FriendRequestListResponse>> GetPendingRequests()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var requests = await _context.Friendships
            .Include(f => f.Requester)
            .Include(f => f.Addressee)
            .Where(f => f.AddresseeId == userId && f.Status == FriendshipStatus.Pending)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new FriendRequestResponse(
                f.Id,
                f.RequesterId,
                f.Requester.DisplayName,
                f.AddresseeId,
                f.Addressee.DisplayName,
                f.Status,
                f.CreatedAt,
                f.UpdatedAt))
            .ToListAsync();

        return Ok(new FriendRequestListResponse(requests));
    }

    [HttpGet("requests/sent")]
    public async Task<ActionResult<FriendRequestListResponse>> GetSentRequests()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var requests = await _context.Friendships
            .Include(f => f.Requester)
            .Include(f => f.Addressee)
            .Where(f => f.RequesterId == userId && f.Status == FriendshipStatus.Pending)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new FriendRequestResponse(
                f.Id,
                f.RequesterId,
                f.Requester.DisplayName,
                f.AddresseeId,
                f.Addressee.DisplayName,
                f.Status,
                f.CreatedAt,
                f.UpdatedAt))
            .ToListAsync();

        return Ok(new FriendRequestListResponse(requests));
    }

    [HttpPost("requests/{id:guid}/accept")]
    public async Task<ActionResult<FriendRequestResponse>> AcceptFriendRequest(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var friendship = await _context.Friendships
            .Include(f => f.Requester)
            .Include(f => f.Addressee)
            .FirstOrDefaultAsync(f => f.Id == id);

        if (friendship == null)
            return NotFound(new { message = "Friend request not found." });

        if (friendship.AddresseeId != userId)
            return Forbid();

        if (friendship.Status != FriendshipStatus.Pending)
            return BadRequest(new { message = "This friend request is no longer pending." });

        friendship.Status = FriendshipStatus.Accepted;
        friendship.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new FriendRequestResponse(
            friendship.Id,
            friendship.RequesterId,
            friendship.Requester.DisplayName,
            friendship.AddresseeId,
            friendship.Addressee.DisplayName,
            friendship.Status,
            friendship.CreatedAt,
            friendship.UpdatedAt));
    }

    [HttpPost("requests/{id:guid}/decline")]
    public async Task<ActionResult<FriendRequestResponse>> DeclineFriendRequest(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var friendship = await _context.Friendships
            .Include(f => f.Requester)
            .Include(f => f.Addressee)
            .FirstOrDefaultAsync(f => f.Id == id);

        if (friendship == null)
            return NotFound(new { message = "Friend request not found." });

        if (friendship.AddresseeId != userId)
            return Forbid();

        if (friendship.Status != FriendshipStatus.Pending)
            return BadRequest(new { message = "This friend request is no longer pending." });

        friendship.Status = FriendshipStatus.Declined;
        friendship.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new FriendRequestResponse(
            friendship.Id,
            friendship.RequesterId,
            friendship.Requester.DisplayName,
            friendship.AddresseeId,
            friendship.Addressee.DisplayName,
            friendship.Status,
            friendship.CreatedAt,
            friendship.UpdatedAt));
    }

    [HttpDelete("requests/{id:guid}")]
    public async Task<IActionResult> CancelFriendRequest(Guid id)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var friendship = await _context.Friendships
            .FirstOrDefaultAsync(f => f.Id == id);

        if (friendship == null)
            return NotFound(new { message = "Friend request not found." });

        if (friendship.RequesterId != userId)
            return Forbid();

        if (friendship.Status != FriendshipStatus.Pending)
            return BadRequest(new { message = "This friend request is no longer pending." });

        _context.Friendships.Remove(friendship);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    [HttpGet("friends")]
    public async Task<ActionResult<FriendListResponse>> GetFriends()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var friends = await _context.Friendships
            .Include(f => f.Requester)
            .Include(f => f.Addressee)
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == userId || f.AddresseeId == userId))
            .Select(f => new FriendResponse(
                f.Id,
                f.RequesterId == userId ? f.AddresseeId : f.RequesterId,
                f.RequesterId == userId ? f.Addressee.DisplayName : f.Requester.DisplayName,
                f.UpdatedAt))
            .ToListAsync();

        return Ok(new FriendListResponse(friends));
    }

    [HttpDelete("friends/{friendUserId}")]
    public async Task<IActionResult> RemoveFriend(string friendUserId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var friendship = await _context.Friendships
            .FirstOrDefaultAsync(f => f.Status == FriendshipStatus.Accepted &&
                ((f.RequesterId == userId && f.AddresseeId == friendUserId) ||
                 (f.RequesterId == friendUserId && f.AddresseeId == userId)));

        if (friendship == null)
            return NotFound(new { message = "Friendship not found." });

        _context.Friendships.Remove(friendship);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    private string? GetUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
