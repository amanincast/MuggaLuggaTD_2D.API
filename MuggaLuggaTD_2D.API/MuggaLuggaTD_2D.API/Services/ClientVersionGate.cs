namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Turns away game builds older than <c>Client:MinimumVersion</c>, so a tester on a stale build is
/// told to update rather than meeting rules the server no longer plays by.
///
/// <para>The game sends <see cref="Header"/> (<c>Application.version</c>, e.g. <c>0.1.314</c>) with
/// every request. A request without it passes: the gate is for honest stale builds, which the
/// launcher normally prevents, not a security boundary (<c>SharedContract.Version</c> still guards
/// the rules themselves). The answer is <b>426 Upgrade Required</b> in the shape of an
/// <c>AuthResponse</c>, so the login screen shows its message with no special handling.</para>
/// </summary>
public class ClientVersionGate
{
    public const string Header = "X-Client-Version";
    public const string Outdated = "This version of MuggaLugga is out of date. Close the game and open the launcher to update.";

    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;

    public ClientVersionGate(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string? sent = context.Request.Headers[Header];
        string minimum = _configuration["Client:MinimumVersion"] ?? "0.0.0";

        if (!string.IsNullOrEmpty(sent) && IsOlder(sent, minimum) && !context.Request.Path.StartsWithSegments("/api/client"))
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                errors = new[] { Outdated },
                minimumVersion = minimum,
            });
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Whether <paramref name="version"/> is older than <paramref name="minimum"/>, comparing the
    /// numeric parts (<c>0.1.99</c> &lt; <c>0.1.100</c>). Anything unparsable is never older, so a
    /// malformed header cannot lock a player out.
    /// </summary>
    public static bool IsOlder(string version, string minimum) =>
        TryParse(version, out var v) && TryParse(minimum, out var m) && v < m;

    private static bool TryParse(string text, out Version version)
    {
        // "0.1.314-dev" -> "0.1.314"; "1.0" is a valid Version as it is.
        int cut = text.IndexOfAny(new[] { '-', '+', ' ' });
        return Version.TryParse(cut >= 0 ? text[..cut] : text, out version!);
    }
}
