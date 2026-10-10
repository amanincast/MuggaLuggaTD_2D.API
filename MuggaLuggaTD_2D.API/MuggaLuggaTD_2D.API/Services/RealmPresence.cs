using System.Collections.Concurrent;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Who is in a realm right now, or was lately: a player counts from joining the realm's hub group until
/// they leave it or their connection drops, and is "seen" at the moment they left. A Lesser Rift only
/// opens for a player seen in the last 30 minutes (Invasions Q2), so it is a call to action and not a
/// penalty for sleeping.
///
/// <para>Held in memory, not in the database: it is what is live on this server, and a restart that
/// forgets it is right (everyone's client reconnects and joins again within moments).</para>
/// </summary>
public class RealmPresence
{
    private readonly ConcurrentDictionary<string, (Guid Realm, string UserId)> _connections = new();
    private readonly ConcurrentDictionary<(Guid Realm, string UserId), DateTime> _lastSeen = new();

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public void Joined(string connectionId, Guid realm, string userId)
    {
        _connections[connectionId] = (realm, userId);
        _lastSeen[(realm, userId)] = Clock();
    }

    public void Left(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var who))
            _lastSeen[who] = Clock();
    }

    /// <summary>The players connected to <paramref name="realm"/> now, or seen there since <paramref name="since"/>.</summary>
    public HashSet<string> SeenSince(Guid realm, DateTime since)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, who) in _connections)
            if (who.Realm == realm) seen.Add(who.UserId);
        foreach (var (who, at) in _lastSeen)
            if (who.Realm == realm && at >= since) seen.Add(who.UserId);
        return seen;
    }
}
