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
    /// lair), <c>guard</c> (the same, but the warlord's guard still stands), <c>lost</c> (one more lair try, as a lost
    /// fight leaves; the third eases the warlord), or <c>clear</c> (no horde, as a seat made before Reclaiming).
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
            case "guard":
            {
                var region = WorldRegionBlob.ReadRegion(node);
                if (!HordeRules.IsOverrun(region)) return BadRequest(new { message = "Your land is not overrun." });
                var layout = RegionGenerator.Generate(region);
                foreach (var site in HordeRules.HeldSites(layout).Where(s => !HordeRules.IsLair(layout, s.SiteId)))
                    WorldRegionBlob.FreeHordeSite(node, site.SiteId);
                foreach (var pack in HordeRules.Packs(region).Where(p => what == "lair" || !HordeRules.IsGuard(p.Id)))
                    WorldRegionBlob.BreakHordePack(node, pack.Id);
                break;
            }
            case "lost":
                WorldRegionBlob.TryLair(node);
                break;
            case "clear":
                (node as JsonObject)?.Remove("Horde");
                break;
            default:
                return BadRequest(new { message = "overrun, lair, guard, lost or clear." });
        }

        await SaveAsync(gameInstanceId, row, world!);
        return Ok(new { message = $"Horde: {what}." });
    }

    /// <summary>
    /// Wild land (phase 2): <c>all</c> overruns every neutral region of this realm that has no horde (worlds made
    /// before phase 2); on <paramref name="regionId"/>, <c>overrun</c> (a fresh wild horde), <c>lair</c> (every warband
    /// broken, by me), <c>guard</c> (the same, but the guard still stands), <c>lost</c> (one more lair try of mine), or
    /// <c>clear</c> (no horde).
    /// </summary>
    [HttpPost("debug/wild/{what}")]
    public async Task<IActionResult> DebugWild(Guid gameInstanceId, string what, [FromQuery] string? regionId)
    {
        if (!_environment.IsDevelopment()) return NotFound();
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null || !await _membership.IsMemberAsync(gameInstanceId, userId)) return Forbid();

        var row = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (row == null) return NotFound(new { message = "This realm has no world yet." });
        var world = JsonNode.Parse(row.GameData);
        var regions = WorldRegionBlob.GetRegions(world);
        if (regions == null) return NotFound(new { message = "This realm has no regions." });

        if (what == "all")
        {
            int overrun = 0;
            foreach (var each in regions)
            {
                if (each == null) continue;
                var region = WorldRegionBlob.ReadRegion(each);
                if (region.Horde != null || !HordeRules.IsWildLand(region)) continue;
                WorldRegionBlob.SetHorde(each, HordeRules.NewWildHorde(region.Tier));
                overrun++;
            }
            await SaveAsync(gameInstanceId, row, world!);
            return Ok(new { message = $"Wild land: {overrun} region(s) overrun." });
        }

        var node = string.IsNullOrEmpty(regionId) ? null : WorldRegionBlob.FindRegion(world, regionId);
        if (node == null) return BadRequest(new { message = "Name a region." });
        var target = WorldRegionBlob.ReadRegion(node);
        switch (what)
        {
            case "overrun":
                if (!HordeRules.IsWildLand(target) && !HordeRules.IsWild(target))
                    return BadRequest(new { message = "Only unowned, faction-free land can be wild." });
                WorldRegionBlob.SetHorde(node, HordeRules.NewWildHorde(target.Tier));
                break;
            case "lair":
            case "guard":
                if (!HordeRules.IsWild(target) || !HordeRules.IsOverrun(target)) return BadRequest(new { message = "That land is not overrun wild land." });
                foreach (var pack in HordeRules.Packs(target).Where(p => what == "lair" || !HordeRules.IsGuard(p.Id)))
                    WorldRegionBlob.BreakHordePack(node, pack.Id, userId);
                break;
            case "lost":
                WorldRegionBlob.TryLair(node, userId);
                break;
            case "clear":
                (node as JsonObject)?.Remove("Horde");
                break;
            case "cleanse":
                if (!HordeRules.IsWild(target)) return BadRequest(new { message = "That land is not wild." });
                WorldRegionBlob.Reclaim(node, DateTime.UtcNow);
                break;
            case "claim":
            {
                // As the keep fight's win would (phase 2a-ii): the rules hold, but no fight is needed.
                var all = WorldRegionBlob.ReadAllRegions(world);
                string? why = StewardRules.WhyNotClaim(target, userId, all);
                if (!StewardRules.IsWildClaim(target)) why = "Only cleansed, unclaimed wild land can be claimed.";
                if (why != null) return BadRequest(new { message = why });
                var name = (await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId))?.UserName;
                WorldRegionBlob.CaptureRegion(node, userId, name);
                if (StewardRules.WouldNeedSteward(target, userId, all)) WorldRegionBlob.NeedSteward(node, true);
                break;
            }
            default:
                return BadRequest(new { message = "all, overrun, lair, guard, lost, clear, cleanse or claim." });
        }
        await SaveAsync(gameInstanceId, row, world!);
        return Ok(new { message = $"Wild {target.RegionId}: {what}." });
    }

    private async Task SaveAsync(Guid gameInstanceId, Models.WorldViewGameData row, JsonNode world)
    {
        row.GameData = world.ToJsonString();
        row.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        var payload = JsonSerializer.Deserialize<object>(row.GameData) ?? new { };
        await _hub.Clients.Group(gameInstanceId.ToString())
            .SendAsync("WorldViewGameDataUpdated", new WorldViewGameDataUpdated(gameInstanceId, payload, row.UpdatedAt));
    }
}
