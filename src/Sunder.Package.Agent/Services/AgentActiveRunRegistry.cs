namespace Sunder.Package.Agent.Services;

public sealed class AgentActiveRunRegistry
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Guid, AgentActiveRunHandle> _activeRuns = new();
    private readonly Dictionary<Guid, Dictionary<Guid, AgentActiveRunHandle>> _inFlightRuns = new();

    internal AgentRunActivationResult Activate(Guid sessionId, AgentActiveRunHandle candidate)
    {
        lock (_syncRoot)
        {
            if (!_activeRuns.TryGetValue(sessionId, out var current))
            {
                _activeRuns[sessionId] = candidate;
                TrackInFlight(sessionId, candidate);
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
            TrackInFlight(sessionId, candidate);
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

    public bool IsCurrent(Guid sessionId, Guid runId, long runRevision)
    {
        lock (_syncRoot)
        {
            return _activeRuns.TryGetValue(sessionId, out var activeRun)
                && Matches(activeRun, runId, runRevision);
        }
    }

    public bool IsActive(Guid sessionId)
    {
        lock (_syncRoot)
        {
            return _activeRuns.ContainsKey(sessionId);
        }
    }

    internal IReadOnlyList<AgentActiveRunHandle> ListInFlight(IReadOnlySet<Guid> sessionIds)
    {
        lock (_syncRoot)
        {
            return sessionIds
                .Where(_inFlightRuns.ContainsKey)
                .SelectMany(sessionId => _inFlightRuns[sessionId].Values)
                .DistinctBy(run => (run.RunId, run.RunRevision))
                .ToArray();
        }
    }

    internal void Complete(Guid sessionId, Guid runId, long runRevision)
    {
        AgentActiveRunHandle? completed = null;
        lock (_syncRoot)
        {
            if (_activeRuns.TryGetValue(sessionId, out var current)
                && Matches(current, runId, runRevision))
            {
                _activeRuns.Remove(sessionId);
            }

            if (_inFlightRuns.TryGetValue(sessionId, out var inFlight)
                && inFlight.TryGetValue(runId, out var candidate)
                && candidate.RunRevision == runRevision)
            {
                inFlight.Remove(runId);
                if (inFlight.Count == 0)
                {
                    _inFlightRuns.Remove(sessionId);
                }
                completed = candidate;
            }
        }

        completed?.MarkCompleted();
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

    private static bool Matches(AgentActiveRunHandle activeRun, Guid runId, long runRevision)
        => activeRun.RunRevision == runRevision
            // Persisted permission continuations created before RunId was added use Guid.Empty.
            && (runId == Guid.Empty || activeRun.RunId == runId);

    private void TrackInFlight(Guid sessionId, AgentActiveRunHandle candidate)
    {
        if (!_inFlightRuns.TryGetValue(sessionId, out var inFlight))
        {
            inFlight = new Dictionary<Guid, AgentActiveRunHandle>();
            _inFlightRuns.Add(sessionId, inFlight);
        }
        inFlight[candidate.RunId] = candidate;
    }
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
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal Sunder.Package.Agent.Models.AgentDurableRunLease? DurableLease { get; init; }

    internal Task Completion => _completion.Task;

    internal void MarkCompleted() => _completion.TrySetResult();
}
