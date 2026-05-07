namespace Sunder.Package.Agent.Services;

public sealed class AgentActiveRunRegistry
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, AgentActiveRunHandle> _activeRuns = new();

    public void Set(Guid sessionId, AgentActiveRunHandle activeRun)
    {
        lock (_syncRoot)
        {
            _activeRuns[sessionId] = activeRun;
        }
    }

    public AgentActiveRunHandle? Remove(Guid sessionId)
    {
        lock (_syncRoot)
        {
            if (!_activeRuns.Remove(sessionId, out var activeRun))
            {
                return null;
            }

            return activeRun;
        }
    }

    public IReadOnlyDictionary<Guid, AgentActiveRunHandle> RemoveMany(IReadOnlySet<Guid> sessionIds)
    {
        var removedRuns = new Dictionary<Guid, AgentActiveRunHandle>();
        lock (_syncRoot)
        {
            foreach (var sessionId in sessionIds)
            {
                if (_activeRuns.Remove(sessionId, out var activeRun))
                {
                    removedRuns[sessionId] = activeRun;
                }
            }
        }

        return removedRuns;
    }

    public bool IsCurrent(Guid sessionId, long runRevision)
    {
        lock (_syncRoot)
        {
            return _activeRuns.TryGetValue(sessionId, out var activeRun) && activeRun.RunRevision == runRevision;
        }
    }

    public bool IsActive(Guid sessionId)
    {
        lock (_syncRoot)
        {
            return _activeRuns.ContainsKey(sessionId);
        }
    }

    public void CleanupCurrent(Guid sessionId, long runRevision)
    {
        AgentActiveRunHandle? removedRun = null;
        lock (_syncRoot)
        {
            if (_activeRuns.TryGetValue(sessionId, out var activeRun) && activeRun.RunRevision == runRevision)
            {
                _activeRuns.Remove(sessionId);
                removedRun = activeRun;
            }
        }

        removedRun?.CancellationTokenSource.Dispose();
    }
}

public sealed record AgentActiveRunHandle(
    Guid RunId,
    long RunRevision,
    DateTimeOffset StartedAtUtc,
    string ProfileId,
    string UserMessage,
    CancellationTokenSource CancellationTokenSource);
