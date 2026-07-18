using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Storage;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentSessionService
{
    private readonly object _sessionNotificationSyncRoot = new();
    private readonly Dictionary<Guid, Queue<Action>> _sessionNotificationQueues = [];
    private readonly HashSet<Guid> _dispatchingSessionNotifications = [];

    private void NotifySessionChanged(Guid sessionId)
        => QueueSessionNotification(sessionId, () => DispatchSessionChanged(sessionId));

    private void DispatchSessionChanged(Guid sessionId)
    {
        var handlers = SessionChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<Guid> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sessionId);
            }
            catch
            {
                // UI or extension listeners must not break persisted agent state changes.
            }
        }
    }

    private void NotifyTurnChanged(
        Guid sessionId,
        AgentTurnRecord turn,
        AgentTurnMutation? mutation = null)
        => QueueSessionNotification(
            sessionId,
            () => DispatchTurnChanged(sessionId, turn, mutation));

    private void DispatchTurnChanged(
        Guid sessionId,
        AgentTurnRecord turn,
        AgentTurnMutation? mutation = null)
    {
        var handlers = TurnChanged;
        if (handlers is not null)
        {
            foreach (Action<Guid, AgentTurnRecord> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(sessionId, turn);
                }
                catch
                {
                    // UI or extension listeners must not break persisted agent turn changes.
                }
            }
        }

        DispatchTurnMutated(mutation ?? new AgentTurnMutation(
            sessionId,
            turn.TurnId,
            turn.ContentRevision,
            AgentTurnMutationKind.Add,
            BaseContentLength: 0,
            Text: null,
            turn.UpdatedAtUtc,
            turn));
    }

    private void DispatchTurnMutated(AgentTurnMutation mutation)
    {
        var handlers = TurnMutated;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<AgentTurnMutation> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(mutation);
            }
            catch
            {
                // UI or extension listeners must not break persisted agent turn changes.
            }
        }
    }

    private static AgentTurnMutation CreateTurnMutation(AgentTurnWriteResult result)
        => new(
            result.Turn.SessionId,
            result.Turn.TurnId,
            result.Turn.ContentRevision,
            result.MutationKind,
            result.BaseContentLength,
            result.Text,
            result.Turn.UpdatedAtUtc,
            result.MutationKind is AgentTurnMutationKind.Replace ? result.Turn : null);

    private void NotifyCompletedStreamingTurns(
        Guid sessionId,
        IReadOnlyList<AgentCompletedStreamingTurn> completedTurns)
        => QueueSessionNotification(
            sessionId,
            () => DispatchCompletedStreamingTurns(sessionId, completedTurns));

    private void DispatchCompletedStreamingTurns(
        Guid sessionId,
        IReadOnlyList<AgentCompletedStreamingTurn> completedTurns)
    {
        foreach (var completed in completedTurns)
        {
            var turn = completed.Turn;
            DispatchTurnChanged(
                sessionId,
                turn,
                new AgentTurnMutation(
                    turn.SessionId,
                    turn.TurnId,
                    turn.ContentRevision,
                    AgentTurnMutationKind.Complete,
                    completed.ContentLength,
                    Text: null,
                    turn.UpdatedAtUtc));
        }
    }

    private void NotifyTurnAndSessionChanged(
        Guid sessionId,
        AgentTurnRecord turn,
        AgentTurnMutation? mutation = null)
        => QueueSessionNotification(sessionId, () =>
        {
            DispatchTurnChanged(sessionId, turn, mutation);
            DispatchSessionChanged(sessionId);
        });

    private void NotifyCompletedStreamingTurnsAndSessionChanged(
        Guid sessionId,
        IReadOnlyList<AgentCompletedStreamingTurn> completedTurns)
        => QueueSessionNotification(sessionId, () =>
        {
            DispatchCompletedStreamingTurns(sessionId, completedTurns);
            DispatchSessionChanged(sessionId);
        });

    private void NotifyRunStartChanged(
        Guid sessionId,
        AgentTurnRecord userTurn,
        bool transcriptReset)
        => QueueSessionNotification(sessionId, () =>
        {
            if (transcriptReset)
            {
                DispatchTranscriptReset(sessionId);
            }
            DispatchTurnChanged(sessionId, userTurn);
            DispatchSessionChanged(sessionId);
        });

    private void NotifyTranscriptResetAndSessionChanged(Guid sessionId)
        => QueueSessionNotification(sessionId, () =>
        {
            DispatchTranscriptReset(sessionId);
            DispatchSessionChanged(sessionId);
        });

    private void QueueSessionNotification(Guid sessionId, Action notification)
    {
        var shouldDrain = false;
        lock (_sessionNotificationSyncRoot)
        {
            if (!_sessionNotificationQueues.TryGetValue(sessionId, out var queue))
            {
                queue = new Queue<Action>();
                _sessionNotificationQueues[sessionId] = queue;
            }

            queue.Enqueue(notification);
            shouldDrain = _dispatchingSessionNotifications.Add(sessionId);
        }

        if (shouldDrain)
        {
            DrainSessionNotifications(sessionId);
        }
    }

    private void DrainSessionNotifications(Guid sessionId)
    {
        while (true)
        {
            Action notification;
            lock (_sessionNotificationSyncRoot)
            {
                var queue = _sessionNotificationQueues[sessionId];
                if (!queue.TryDequeue(out notification!))
                {
                    _sessionNotificationQueues.Remove(sessionId);
                    _dispatchingSessionNotifications.Remove(sessionId);
                    return;
                }
            }

            try
            {
                notification();
            }
            catch
            {
                // Notification dispatch must not break already committed state changes.
            }
        }
    }

    private static void EnqueueLeaseNotification(
        AgentDurableRunLease lease,
        Action notification)
        => lease.NotificationQueue.Enqueue(notification);

    private static void DrainLeaseNotifications(AgentDurableRunLease lease)
    {
        lock (lease.SyncRoot)
        {
            if (lease.IsDispatchingNotifications)
            {
                return;
            }
            lease.IsDispatchingNotifications = true;
        }

        while (true)
        {
            Action notification;
            lock (lease.SyncRoot)
            {
                if (!lease.NotificationQueue.TryDequeue(out notification!))
                {
                    lease.IsDispatchingNotifications = false;
                    return;
                }
            }

            try
            {
                notification();
            }
            catch
            {
                // Notification dispatch must not break already committed state changes.
            }
        }
    }

    private void NotifyTranscriptReset(Guid sessionId)
        => QueueSessionNotification(sessionId, () => DispatchTranscriptReset(sessionId));

    private void DispatchTranscriptReset(Guid sessionId)
    {
        var handlers = TranscriptReset;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<Guid> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sessionId);
            }
            catch
            {
                // UI or extension listeners must not break persisted agent state changes.
            }
        }
    }

    private void NotifyRunActivityChanged(Guid sessionId, AgentRunActivityUpdate activity)
        => QueueSessionNotification(
            sessionId,
            () => DispatchRunActivityChanged(sessionId, activity));

    private void DispatchRunActivityChanged(Guid sessionId, AgentRunActivityUpdate activity)
    {
        var handlers = RunActivityChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<Guid, AgentRunActivityUpdate> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sessionId, activity);
            }
            catch
            {
                // Live activity listeners must not break agent execution.
            }
        }
    }
}
