using Microsoft.AspNetCore.SignalR;
using MuggaLuggaTD_2D.API.Hubs;
using MuggaLuggaTD_2D.API.Services;

namespace MuggaLuggaTD_2D.API.Tests.TestSupport;

/// <summary>
/// A hub context that records what would have been broadcast instead of sending it.
///
/// <para>Hand-written rather than mocked: the tests need exactly one thing from SignalR - that a
/// season close tells the realm - and a fake that records the method name and its arguments says
/// that more plainly than a mock framework's setup would.</para>
/// </summary>
public class FakeHubContext : IHubContext<GameHub>
{
    public List<(string Group, string Method, object?[] Args)> Sent { get; } = new();

    public IHubClients Clients { get; }
    public IGroupManager Groups { get; } = new NoOpGroups();

    public FakeHubContext()
    {
        Clients = new RecordingClients(this);
    }

    /// <summary>The methods broadcast to a group, in order.</summary>
    public IEnumerable<string> MethodsSentTo(Guid gameInstanceId)
        => Sent.Where(s => s.Group == gameInstanceId.ToString()).Select(s => s.Method);

    /// <summary>The single argument of the last broadcast of a given method, or null.</summary>
    public object? LastPayloadOf(string method)
        => Sent.LastOrDefault(s => s.Method == method).Args?.FirstOrDefault();

    private class RecordingClients : IHubClients
    {
        private readonly FakeHubContext _owner;

        public RecordingClients(FakeHubContext owner) => _owner = owner;

        public IClientProxy Group(string groupName) => new RecordingProxy(_owner, groupName);

        public IClientProxy All => new RecordingProxy(_owner, "all");
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => All;
        public IClientProxy Client(string connectionId) => new RecordingProxy(_owner, connectionId);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => All;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Group(groupName);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => All;
        public IClientProxy User(string userId) => new RecordingProxy(_owner, userId);
        public IClientProxy Users(IReadOnlyList<string> userIds) => All;
    }

    private class RecordingProxy : IClientProxy
    {
        private readonly FakeHubContext _owner;
        private readonly string _group;

        public RecordingProxy(FakeHubContext owner, string group)
        {
            _owner = owner;
            _group = group;
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            _owner.Sent.Add((_group, method, args));
            return Task.CompletedTask;
        }
    }

    private class NoOpGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

/// <summary>A session log that keeps its lines in memory, so a test can assert a decision was recorded.</summary>
public class FakeSessionLog : ISessionLog
{
    public List<(string Category, string Message)> Lines { get; } = new();

    public bool Enabled => true;

    public void Log(string category, string message) => Lines.Add((category, message));

    public IEnumerable<string> Of(string category)
        => Lines.Where(l => l.Category == category).Select(l => l.Message);
}
