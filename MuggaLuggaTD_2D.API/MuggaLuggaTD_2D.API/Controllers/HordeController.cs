using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Hubs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// Reclaiming's debug hooks (Development only; Combat Debug → Reclaiming). The fights themselves go through pve
/// begin/claim with a <c>horde:{siteId}</c> id; the horde lives in the world blob and arrives with it.
/// </summary>
[ApiController]
[Route("api/gameinstance/{gameInstanceId:guid}/horde")]
[Authorize]
public class HordeController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHubContext<GameHub> _hub;
    private readonly RealmMembershipService _membership;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<HordeController> _logger;

    public HordeController(ApplicationDbContext context, IHubContext<GameHub> hub, RealmMembershipService membership,
        IWebHostEnvironment environment, ILogger<HordeController> logger)
    {
        _context = context;
        _hub = hub;
        _membership = membership;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Changes my capital's horde: <c>overrun</c> (a fresh one, at my level), <c>lair</c> (every site freed but the
    /// lair), or <c>clear</c> (no horde, as a seat made before Reclaiming).
    /// </summary>
    [HttpPost("debug/{what}")]
    public async Task<IActionResult> Debug(Guid gameInstanceId, string what)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null || !await _membership.IsMemberAsync(gameInstanceId, userId)) return Forbid();

        var row = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (row == null) return NotFound(new { message = "This realm has no world yet." });
        var world = JsonNode.Parse(row.GameData);
        var regions = WorldRegionBlob.GetRegions(world);
        var node = regions?.FirstOrDefault(r => r?["OwnerUserId"]?.GetValue<string>() == userId && r?["IsCapital"]?.GetValue<bool>() == true);
        if (node == null) return BadRequest(new { message = "You have no seat in this realm." });

        switch (what)
        {
            case "overrun":
                WorldRegionBlob.SetHorde(node, HordeRules.NewHorde(await RealmGoalService.RosterLevelAsync(_context, _logger, gameInstanceId, userId)));
                break;
            case "lair":
            {
                var region = WorldRegionBlob.ReadRegion(node);
                if (!HordeRules.IsOverrun(region)) return BadRequest(new { message = "Your land is not overrun." });
                var layout = RegionGenerator.Generate(region);
                foreach (var site in HordeRules.HeldSites(layout).Where(s => !HordeRules.IsLair(layout, s.SiteId)))
                    WorldRegionBlob.FreeHordeSite(node, site.SiteId);
                break;
            }
            case "clear":
                (node as JsonObject)?.Remove("Horde");
                break;
            default:
                return BadRequest(new { message = "overrun, lair or clear." });
        }

        row.GameData = world!.ToJsonString();
        row.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        var payload = JsonSerializer.Deserialize<object>(row.GameData) ?? new { };
        await _hub.Clients.Group(gameInstanceId.ToString())
            .SendAsync("WorldViewGameDataUpdated", new WorldViewGameDataUpdated(gameInstanceId, payload, row.UpdatedAt));
        return Ok(new { message = $"Horde: {what}." });
    }
}
