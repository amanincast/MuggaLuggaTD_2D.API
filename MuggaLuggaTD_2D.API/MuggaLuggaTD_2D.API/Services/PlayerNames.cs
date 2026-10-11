namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// The one name a player goes by everywhere (Leaderboards row 6): their display name, or their user name if
/// they have none.
/// </summary>
public static class PlayerNames
{
    public static string Of(string? displayName, string? userName) =>
        !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim()
        : !string.IsNullOrWhiteSpace(userName) ? userName
        : "Unknown";
}
