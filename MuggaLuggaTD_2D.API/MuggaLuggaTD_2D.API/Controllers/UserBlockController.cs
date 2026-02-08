using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Controllers;

[ApiController]
[Route("api/blocks")]
[Authorize]
public class UserBlockController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public UserBlockController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpPost]
    public async Task<ActionResult<UserBlockResponse>> BlockUser([FromBody] BlockUserRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        if (request.BlockedUserId == userId)
            return BadRequest(new { message = "You cannot block yourself." });

        var blockedUser = await _context.Users.FindAsync(request.BlockedUserId);
        if (blockedUser == null)
            return NotFound(new { message = "User not found." });

        var existingBlock = await _context.UserBlocks
            .AnyAsync(b => b.BlockerId == userId && b.BlockedUserId == request.BlockedUserId);
        if (existingBlock)
            return BadRequest(new { message = "You have already blocked this user." });

        // Remove any existing friendship between the two users
        var friendship = await _context.Friendships
            .FirstOrDefaultAsync(f =>
                (f.RequesterId == userId && f.AddresseeId == request.BlockedUserId) ||
                (f.RequesterId == request.BlockedUserId && f.AddresseeId == userId));
        if (friendship != null)
            _context.Friendships.Remove(friendship);

        var block = new UserBlock
        {
            BlockerId = userId,
            BlockedUserId = request.BlockedUserId,
            CreatedAt = DateTime.UtcNow
        };

        _context.UserBlocks.Add(block);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetBlockedUsers), new UserBlockResponse(
            block.Id,
            block.BlockedUserId,
            blockedUser.DisplayName,
            block.CreatedAt));
    }

    [HttpGet]
    public async Task<ActionResult<UserBlockListResponse>> GetBlockedUsers()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var blocks = await _context.UserBlocks
            .Include(b => b.BlockedUser)
            .Where(b => b.BlockerId == userId)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new UserBlockResponse(
                b.Id,
                b.BlockedUserId,
                b.BlockedUser.DisplayName,
                b.CreatedAt))
            .ToListAsync();

        return Ok(new UserBlockListResponse(blocks));
    }

    [HttpDelete("{blockedUserId}")]
    public async Task<IActionResult> UnblockUser(string blockedUserId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var block = await _context.UserBlocks
            .FirstOrDefaultAsync(b => b.BlockerId == userId && b.BlockedUserId == blockedUserId);

        if (block == null)
            return NotFound(new { message = "Block not found." });

        _context.UserBlocks.Remove(block);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    private string? GetUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
