using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed class AgentRunStopCoordinator(
    AgentSessionService sessionService,
    AgentPermissionService permissionService,
    AgentMemoryCoordinator memoryCoordinator,
    AgentActiveRunRegistry activeRunRegistry,
    AgentProfileService profileService,
    AgentSessionTransitionGate? transitionGate = null)
{
    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentPermissionService _permissionService = permissionService;
    private readonly AgentMemoryCoordinator _memoryCoordinator = memoryCoordinator;
    private readonly AgentActiveRunRegistry _activeRunRegistry = activeRunRegistry;
    private readonly AgentProfileService _profileService = profileService;
    private readonly AgentSessionTransitionGate _transitionGate =
        transitionGate ?? AgentSessionTransitionGate.Shared;

    public async Task<AgentRunCheckpointRecord?> StopAsync(Guid sessionId)
    {
        var sessions = ResolveSessionTree(sessionId);
        if (sessions.Count == 0)
        {
            return _sessionService.GetLatestCheckpoint(sessionId);
        }

        AgentRunCheckpointRecord? requestedCheckpoint = null;
        foreach (var session in sessions)
        {
            AgentActiveRunHandle? activeRun;
            AgentRunCheckpointRecord? latest;
            AgentRunCheckpointRecord? checkpoint;
            using (await _transitionGate.EnterAsync(session.SessionId).ConfigureAwait(false))
            {
                activeRun = _activeRunRegistry.Remove(session.SessionId);
                latest = _sessionService.GetLatestCheckpoint(session.SessionId);
                checkpoint = TrySaveStoppedCheckpoint(
                    session,
                    latest,
                    activeRun,
                    session.SessionId == sessionId);
                activeRun?.CancellationTokenSource.Cancel();
            }

            if (checkpoint is not null)
            {
                if (activeRun is not null)
                {
                    await PublishRunStoppedAsync(session, activeRun, checkpoint).ConfigureAwait(false);
                }
            }

            if (session.SessionId == sessionId)
            {
                requestedCheckpoint = checkpoint ?? latest;
            }
        }

        return requestedCheckpoint ?? _sessionService.GetLatestCheckpoint(sessionId);
    }

    private IReadOnlyList<AgentSessionRecord> ResolveSessionTree(Guid sessionId)
    {
        var rootSession = _sessionService.GetSession(sessionId);
        if (rootSession is null)
        {
            return [];
        }

        var sessions = _sessionService.ListSessions();
        var descendantsByParent = sessions
            .Where(session => session.ParentSessionId is not null)
            .GroupBy(session => session.ParentSessionId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var ordered = new List<AgentSessionRecord> { rootSession };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootSession.SessionId);
        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            if (!descendantsByParent.TryGetValue(parentId, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                ordered.Add(child);
                queue.Enqueue(child.SessionId);
            }
        }

        return ordered;
    }

    private AgentRunCheckpointRecord? TrySaveStoppedCheckpoint(
        AgentSessionRecord session,
        AgentRunCheckpointRecord? latest,
        AgentActiveRunHandle? activeRun,
        bool isRequestedSession)
    {
        var durableRun = _sessionService.GetLatestRun(session.SessionId);
        if (durableRun?.FinishedAtUtc is not null
            || durableRun is null
               && latest is not null
               && latest.Status is not (AgentRunStatus.Running or AgentRunStatus.WaitingForApproval))
        {
            return null;
        }

        var runRevision = durableRun?.Key.RunRevision ?? latest?.RunRevision ?? activeRun?.RunRevision;
        if (runRevision is null)
        {
            return null;
        }

        var summary = isRequestedSession
            ? "Run stopped by the user before provider execution completed."
            : "Subsession stopped because the parent session was stopped.";
        if (durableRun is not null)
        {
            var lease = activeRun?.DurableLease is { } activeLease
                        && activeLease.Key == durableRun.Key
                ? activeLease
                : new Sunder.Package.Agent.Models.AgentDurableRunLease(durableRun);
            return _sessionService.TryStopRun(lease, summary)?.Checkpoint
                   ?? _sessionService.GetLatestCheckpoint(session.SessionId);
        }

        var checkpoint = _sessionService.SaveCheckpoint(
            session.SessionId,
            runRevision.Value,
            AgentRunStatus.Stopped,
            summary);
        foreach (var request in _permissionService.ListPendingRequests(session.SessionId))
        {
            var expiration = _permissionService.ExpireActiveRequest(
                session.SessionId,
                request.RequestId,
                "Permission request expired because the run was stopped.");
            if (expiration.Finalization is not null)
            {
                _sessionService.PublishCommittedCheckpoint(expiration.Finalization);
            }
            else if (expiration.Changed)
            {
                _sessionService.PublishCommittedSessionChanged(session.SessionId);
            }
        }

        return checkpoint;
    }

    private async Task PublishRunStoppedAsync(
        AgentSessionRecord session,
        AgentActiveRunHandle activeRun,
        AgentRunCheckpointRecord stoppedCheckpoint)
    {
        var profile = ResolveProfile(activeRun.ProfileId);
        if (profile is null)
        {
            return;
        }

        await _memoryCoordinator.PublishLifecycleEventAsync(
            AgentLifecycleEventKind.RunStopped,
            session,
            profile,
            activeRun.RunId,
            activeRun.RunRevision,
            AgentRunStatus.Stopped,
            activeRun.StartedAtUtc,
            activeRun.UserMessage,
            checkpoint: stoppedCheckpoint,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
    }

    private AgentProfileRecord? ResolveProfile(string? profileId)
        => string.IsNullOrWhiteSpace(profileId)
            ? null
            : _profileService.GetProfile(profileId);
}
