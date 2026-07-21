using System.Text;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Appends human-readable, greppable lines to a per-process session file, for reviewing that the
/// server's gameplay decisions carried the right data. Off unless <c>Diagnostics:SessionLog</c> is
/// true, so a release deployment writes nothing.
///
/// Format is shared with the Unity client's session log: <c>&lt;utc&gt; &lt;CATEGORY&gt; &lt;message&gt;</c>.
/// </summary>
public interface ISessionLog
{
    bool Enabled { get; }

    /// <summary>Writes one line. <paramref name="category"/> is a short uppercase tag like "PVE-CLAIM".</summary>
    void Log(string category, string message);
}

public class SessionLog : ISessionLog
{
    private readonly object _gate = new();
    private readonly string? _path;

    public SessionLog(IConfiguration configuration, IWebHostEnvironment environment, ILogger<SessionLog> logger)
    {
        Enabled = configuration.GetValue("Diagnostics:SessionLog", false);
        if (!Enabled)
            return;

        try
        {
            var dir = Path.Combine(environment.ContentRootPath, "logs");
            Directory.CreateDirectory(dir);
            // One file per process start — a clean file per test run.
            _path = Path.Combine(dir, $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt");
            File.AppendAllText(_path, $"{Timestamp()} SESSION-START server pid={Environment.ProcessId}{System.Environment.NewLine}");
            logger.LogInformation("Session log writing to {Path}.", _path);
        }
        catch (Exception ex)
        {
            // Diagnostics must never take the server down.
            logger.LogWarning(ex, "Could not open a session log; continuing without one.");
            Enabled = false;
            _path = null;
        }
    }

    public bool Enabled { get; private set; }

    public void Log(string category, string message)
    {
        if (!Enabled || _path == null)
            return;

        var line = $"{Timestamp()} {category} {message}{System.Environment.NewLine}";
        try
        {
            lock (_gate)
                File.AppendAllText(_path, line, Encoding.UTF8);
        }
        catch
        {
            // A failed diagnostic write is not worth surfacing or retrying.
        }
    }

    private static string Timestamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}
