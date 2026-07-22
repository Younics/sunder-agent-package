using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed class AgentSessionDeletionFence
{
    internal static AgentSessionDeletionFence Shared { get; } = new();

    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, int> _sessionIds = [];
    private readonly Dictionary<string, int> _workspaceIds = new(StringComparer.OrdinalIgnoreCase);

    internal IDisposable Enter(IReadOnlyCollection<Guid> sessionIds, string? workspaceId = null)
    {
        lock (_syncRoot)
        {
            foreach (var sessionId in sessionIds)
            {
                _sessionIds[sessionId] = _sessionIds.GetValueOrDefault(sessionId) + 1;
            }
            if (!string.IsNullOrWhiteSpace(workspaceId))
            {
                _workspaceIds[workspaceId] = _workspaceIds.GetValueOrDefault(workspaceId) + 1;
            }
        }

        return new Lease(this, sessionIds.ToArray(), workspaceId);
    }

    internal bool IsFenced(AgentSessionRecord session)
    {
        lock (_syncRoot)
        {
            return _sessionIds.ContainsKey(session.SessionId)
                   || session.ParentSessionId is { } parentId && _sessionIds.ContainsKey(parentId)
                   || session.RootSessionId is { } rootId && _sessionIds.ContainsKey(rootId)
                   || !string.IsNullOrWhiteSpace(session.WorkspaceId)
                   && _workspaceIds.ContainsKey(session.WorkspaceId);
        }
    }

    internal bool IsWorkspaceFenced(string? workspaceId)
    {
        lock (_syncRoot)
        {
            return !string.IsNullOrWhiteSpace(workspaceId)
                   && _workspaceIds.ContainsKey(workspaceId);
        }
    }

    private void Exit(IReadOnlyList<Guid> sessionIds, string? workspaceId)
    {
        lock (_syncRoot)
        {
            foreach (var sessionId in sessionIds)
            {
                Decrement(_sessionIds, sessionId);
            }
            if (!string.IsNullOrWhiteSpace(workspaceId))
            {
                Decrement(_workspaceIds, workspaceId);
            }
        }
    }

    private static void Decrement<TKey>(Dictionary<TKey, int> entries, TKey key)
        where TKey : notnull
    {
        if (entries.TryGetValue(key, out var count) && count > 1)
        {
            entries[key] = count - 1;
        }
        else
        {
            entries.Remove(key);
        }
    }

    private sealed class Lease(
        AgentSessionDeletionFence owner,
        IReadOnlyList<Guid> sessionIds,
        string? workspaceId) : IDisposable
    {
        private AgentSessionDeletionFence? _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Exit(sessionIds, workspaceId);
    }
}
