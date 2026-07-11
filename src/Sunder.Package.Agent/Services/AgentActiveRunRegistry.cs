namespace Sunder.Package.Agent.Services;

public sealed class AgentActiveRunRegistry
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, AgentActiveRunHandle> _activeRuns = new();

    public void Set(Guid sessionId, AgentActiveRunHandle activeRun)
    {
        var activation = Activate(sessionId, activeRun);
        if (activation.Outcome == AgentRunActivationOutcome.Rejected)
        {
            return;
        }

        activation.DisplacedRun?.CancellationTokenSource.Cancel();
    }

    internal AgentRunActivationResult Activate(Guid sessionId, AgentActiveRunHandle candidate)
    {
        lock (_syncRoot)
        {
            if (!_activeRuns.TryGetValue(sessionId, out var current))
            {
                _activeRuns[sessionId] = candidate;
                return new AgentRunActivationResult(
                    AgentRunActivationOutcome.Activated,
                    candidate,
                    DisplacedRun: null);
            }

            if (candidate.RunRevision <= current.RunRevision)
            {
                return new AgentRunActivationResult(
                    AgentRunActivationOutcome.Rejected,
                    current,
                    DisplacedRun: null);
            }

            _activeRuns[sessionId] = candidate;
            return new AgentRunActivationResult(
                AgentRunActivationOutcome.Replaced,
                candidate,
                current);
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

    public bool IsCurrent(Guid sessionId, Guid runId, long runRevision)
    {
        lock (_syncRoot)
        {
            return _activeRuns.TryGetValue(sessionId, out var activeRun)
                && Matches(activeRun, runId, runRevision);
        }
    }

    [Obsolete("Legacy continuation compatibility only. Use the RunId-aware overload.")]
    public bool IsCurrent(Guid sessionId, long runRevision)
        => IsCurrent(sessionId, Guid.Empty, runRevision);

    public bool IsActive(Guid sessionId)
    {
        lock (_syncRoot)
        {
            return _activeRuns.ContainsKey(sessionId);
        }
    }

    internal AgentActiveRunHandle? GetCurrent(Guid sessionId, Guid runId, long runRevision)
    {
        lock (_syncRoot)
        {
            return _activeRuns.TryGetValue(sessionId, out var activeRun)
                   && Matches(activeRun, runId, runRevision)
                ? activeRun
                : null;
        }
    }

    public void CleanupCurrent(Guid sessionId, Guid runId, long runRevision)
        => TryCleanupCurrent(sessionId, runId, runRevision);

    internal bool TryCleanupCurrent(Guid sessionId, Guid runId, long runRevision)
    {
        AgentActiveRunHandle? removedRun = null;
        lock (_syncRoot)
        {
            if (_activeRuns.TryGetValue(sessionId, out var activeRun)
                && Matches(activeRun, runId, runRevision))
            {
                _activeRuns.Remove(sessionId);
                removedRun = activeRun;
            }
        }

        return removedRun is not null;
    }

    [Obsolete("Legacy continuation compatibility only. Use the RunId-aware overload.")]
    public void CleanupCurrent(Guid sessionId, long runRevision)
        => CleanupCurrent(sessionId, Guid.Empty, runRevision);

    private static bool Matches(AgentActiveRunHandle activeRun, Guid runId, long runRevision)
        => activeRun.RunRevision == runRevision
            // Persisted permission continuations created before RunId was added use Guid.Empty.
            && (runId == Guid.Empty || activeRun.RunId == runId);
}

internal sealed record AgentRunActivationResult(
    AgentRunActivationOutcome Outcome,
    AgentActiveRunHandle CurrentRun,
    AgentActiveRunHandle? DisplacedRun)
{
    public bool IsAccepted => Outcome is not AgentRunActivationOutcome.Rejected;
}

internal enum AgentRunActivationOutcome
{
    Activated = 0,
    Replaced = 1,
    Rejected = 2,
}

public sealed record AgentActiveRunHandle(
    Guid RunId,
    long RunRevision,
    DateTimeOffset StartedAtUtc,
    string ProfileId,
    string UserMessage,
    CancellationTokenSource CancellationTokenSource)
{
    internal Sunder.Package.Agent.Models.AgentDurableRunLease? DurableLease { get; init; }
}
