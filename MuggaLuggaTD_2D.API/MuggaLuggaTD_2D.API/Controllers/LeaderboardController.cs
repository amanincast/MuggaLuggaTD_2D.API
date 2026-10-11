using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>The cross-world boards (Leaderboards spec). Any signed-in player may read them; nothing here writes.</summary>
[ApiController]
[Route("api/leaderboards")]
[Authorize]
public class LeaderboardController : ControllerBase
{
    private readonly LeaderboardService _boards;
    private readonly IWebHostEnvironment _environment;

    public LeaderboardController(LeaderboardService boards, IWebHostEnvironment environment)
    {
        _boards = boards;
        _environment = environment;
    }

    private string Me => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    /// <summary>Every running season's rate so far.</summary>
    [HttpGet("now")]
    public async Task<ActionResult<LeaderboardPage>> Now() => Ok(await _boards.NowAsync(Me));

    /// <summary>The qualified seasons that ended in a month (yyyymm), with the months that have any.</summary>
    [HttpGet("month/{month:int}")]
    public async Task<ActionResult<LeaderboardPage>> Month(int month) => Ok(await _boards.MonthAsync(month, Me));

    /// <summary>Crowns, then the best rate.</summary>
    [HttpGet("alltime")]
    public async Task<ActionResult<LeaderboardPage>> AllTime() => Ok(await _boards.AllTimeAsync(Me));

    /// <summary>A living board: heroes, companies or conquerors by bracket (short / standard / long), slayers by week / alltime.</summary>
    [HttpGet("living/{board}/{bracket}")]
    public async Task<ActionResult<LeaderboardPage>> Living(string board, string bracket)
    {
        if (!Enum.TryParse<LeaderboardBoard>(board, true, out var which) || which == LeaderboardBoard.Rate)
            return NotFound(new { message = "No such board." });
        bool known = which == LeaderboardBoard.Slayers
            ? Enum.TryParse<SlayerWindow>(bracket, true, out _)
            : Enum.TryParse<SeasonBracket>(bracket, true, out _);
        if (!known) return NotFound(new { message = "No such bracket." });
        return Ok(await _boards.LivingAsync(which, Normal(which, bracket), Me));
    }

    /// <summary>Development only (Combat Debug): 30 made-up results across three months, to see the boards full.</summary>
    [HttpPost("debug/seed")]
    public async Task<IActionResult> DebugSeed()
    {
        if (!_environment.IsDevelopment()) return NotFound();
        int written = await _boards.DebugSeedAsync();
        return Ok(new { message = $"Seeded {written} results." });
    }

    /// <summary>The bracket as the snapshot is keyed: its enum name, whatever case was asked.</summary>
    private static string Normal(LeaderboardBoard board, string bracket) =>
        board == LeaderboardBoard.Slayers
            ? Enum.Parse<SlayerWindow>(bracket, true).ToString()
            : Enum.Parse<SeasonBracket>(bracket, true).ToString();
}
