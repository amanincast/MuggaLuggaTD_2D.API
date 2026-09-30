using Microsoft.AspNetCore.Mvc;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Controllers;

/// <summary>
/// What a game build needs to know before it signs in: whether it is recent enough
/// (<see cref="ClientVersionGate"/>), and whether it needs an invite code to register.
/// Anonymous, and never turned away by the version gate itself.
/// </summary>
[ApiController]
[Route("api/client")]
public class ClientController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public ClientController(IConfiguration configuration) => _configuration = configuration;

    [HttpGet("version")]
    public ActionResult<object> Version([FromQuery] string? current = null)
    {
        string minimum = _configuration["Client:MinimumVersion"] ?? "0.0.0";
        return Ok(new
        {
            minimumVersion = minimum,
            upToDate = current == null || !ClientVersionGate.IsOlder(current, minimum),
            inviteCodeRequired = _configuration.GetValue("Registration:RequireInviteCode", true),
        });
    }
}
