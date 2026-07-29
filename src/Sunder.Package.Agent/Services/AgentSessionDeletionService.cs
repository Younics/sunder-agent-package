using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services;

internal sealed class AgentSessionDeletionService(
    AgentSessionService sessions,
    AgentWorkspaceService workspaces,
    AgentActiveRunRegistry activeRuns,
    AgentSessionTransitionGate transitionGate,
    AgentSessionDeletionFence deletionFence)
{
    internal async Task DeleteSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var tree = ResolveSessionTree(sessionId);
        if (tree.Count == 0)
        {
            return;
        }

        await DeleteAsync(
            tree,
            workspaceId: null,
            () => sessions.DeleteSession(sessionId),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task DeleteWorkspaceAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var workspaceSessions = sessions.ListSessionsForWorkspace(workspaceId);
        await DeleteAsync(
            workspaceSessions,
            workspaceId,
            () => workspaces.DeleteWorkspace(workspaceId),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteAsync(
        IReadOnlyList<AgentSessionRecord> targetSessions,
        string? workspaceId,
        Action deletePersistence,
        CancellationToken cancellationToken)
    {
        var sessionIds = targetSessions
            .Select(session => session.SessionId)
            .ToHashSet();
        var initialGates = await EnterGatesAsync(sessionIds, cancellationToken).ConfigureAwait(false);
        IDisposable? fence = null;
        IReadOnlyList<AgentActiveRunHandle> inFlight;
        try
        {
            fence = deletionFence.Enter(sessionIds, workspaceId);
            inFlight = activeRuns.ListInFlight(sessionIds);
            foreach (var session in targetSessions)
            {
                StopLatestRun(session.SessionId, inFlight);
            }
        }
        catch
        {
            fence?.Dispose();
            throw;
        }
        finally
        {
            DisposeGates(initialGates);
        }

        try
        {
            foreach (var run in inFlight)
            {
                await TryCancelAsync(run.CancellationTokenSource).ConfigureAwait(false);
            }
            if (inFlight.Count > 0)
            {
                await Task.WhenAll(inFlight.Select(run => run.Completion))
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var refreshedIds = workspaceId is null
                ? ResolveSessionTree(targetSessions[0].SessionId)
                    .Select(session => session.SessionId)
                    .ToHashSet()
                : sessions.ListSessionsForWorkspace(workspaceId)
                    .Select(session => session.SessionId)
                    .ToHashSet();
            var finalGates = await EnterGatesAsync(refreshedIds, cancellationToken).ConfigureAwait(false);
            try
            {
                var lateRuns = activeRuns.ListInFlight(refreshedIds);
                if (lateRuns.Count > 0)
                {
                    throw new InvalidOperationException(
                        "A session run became active after deletion was fenced.");
                }
                deletePersistence();
            }
            finally
            {
                DisposeGates(finalGates);
            }
        }
        finally
        {
            fence?.Dispose();
        }
    }

    private void StopLatestRun(
        Guid sessionId,
        IReadOnlyList<AgentActiveRunHandle> inFlight)
    {
        var run = sessions.GetLatestRun(sessionId);
        if (run?.FinishedAtUtc is not null || run is null)
        {
            return;
        }

        var active = inFlight.FirstOrDefault(candidate =>
            candidate.RunId == run.Key.RunId
            && candidate.RunRevision == run.Key.RunRevision);
        var lease = active?.DurableLease is { } activeLease && activeLease.Key == run.Key
            ? activeLease
            : new AgentDurableRunLease(run);
        sessions.TryStopRun(lease, "Run stopped because its session is being deleted.");
    }

    private IReadOnlyList<AgentSessionRecord> ResolveSessionTree(Guid sessionId)
    {
        var root = sessions.GetSession(sessionId);
        if (root is null)
        {
            return [];
        }

        var allSessions = sessions.ListSessions();
        var byParent = allSessions
            .Where(session => session.ParentSessionId is not null)
            .GroupBy(session => session.ParentSessionId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var result = new List<AgentSessionRecord> { root };
        var pending = new Queue<Guid>();
        pending.Enqueue(root.SessionId);
        while (pending.TryDequeue(out var parentId))
        {
            if (!byParent.TryGetValue(parentId, out var children))
            {
                continue;
            }
            foreach (var child in children)
            {
                result.Add(child);
                pending.Enqueue(child.SessionId);
            }
        }
        return result;
    }

    private async Task<List<IDisposable>> EnterGatesAsync(
        IReadOnlySet<Guid> sessionIds,
        CancellationToken cancellationToken)
    {
        var gates = new List<IDisposable>(sessionIds.Count);
        try
        {
            foreach (var sessionId in sessionIds.Order())
            {
                gates.Add(await transitionGate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false));
            }
            return gates;
        }
        catch
        {
            DisposeGates(gates);
            throw;
        }
    }

    private static void DisposeGates(IReadOnlyList<IDisposable> gates)
    {
        for (var index = gates.Count - 1; index >= 0; index--)
        {
            gates[index].Dispose();
        }
    }

    private static async Task TryCancelAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
