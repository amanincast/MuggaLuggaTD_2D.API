using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// Authoritative game content (character/ability/item/... definitions). Clients sync this after
/// authenticating so every player runs identical data, and cache it locally for offline solo play.
/// </summary>
[ApiController]
[Route("api/content")]
[Authorize]
public class GameContentController : ControllerBase
{
    private readonly IGameContentProvider _content;

    public GameContentController(IGameContentProvider content)
    {
        _content = content;
    }

    /// <summary>
    /// Returns the full content set. Clients that send a matching <c>If-None-Match</c> get a 304 and
    /// keep their cache — the payload is ~230 KB, so skipping it on an unchanged version matters.
    /// </summary>
    [HttpGet]
    public ActionResult<GameContentResponse> GetContent()
    {
        var version = _content.Version;
        var etag = $"\"{version}\"";

        if (Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var incoming)
            && incoming.Any(tag => tag == etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers[HeaderNames.ETag] = etag;
        return Ok(new GameContentResponse(version, _content.Documents));
    }

    /// <summary>
    /// Returns just the current content version. Clients check this before joining a shared game
    /// instance: a mismatch against their cached content means they must re-sync first.
    /// </summary>
    [HttpGet("version")]
    public ActionResult<GameContentVersionResponse> GetVersion()
    {
        return Ok(new GameContentVersionResponse(_content.Version));
    }
}
