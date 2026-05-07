using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed class AgentRunStopCoordinator(
    AgentSessionService sessionService,
    AgentPermissionService permissionService,
    AgentMemoryCoordinator memoryCoordinator,
    AgentActiveRunRegistry activeRunRegistry,
    AgentProfileService profileService)
{
    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentPermissionService _permissionService = permissionService;
    private readonly AgentMemoryCoordinator _memoryCoordinator = memoryCoordinator;
    private readonly AgentActiveRunRegistry _activeRunRegistry = activeRunRegistry;
    private readonly AgentProfileService _profileService = profileService;

    public async Task<AgentRunCheckpointRecord?> StopAsync(Guid sessionId)
    {
        var sessions = ResolveSessionTree(sessionId);
        if (sessions.Count == 0)
        {
            return _sessionService.GetLatestCheckpoint(sessionId);
        }

        var sessionIds = sessions.Select(session => session.SessionId).ToHashSet();
        var activeRuns = _activeRunRegistry.RemoveMany(sessionIds);

        foreach (var activeRun in activeRuns.Values)
        {
            activeRun.CancellationTokenSource.Cancel();
            activeRun.CancellationTokenSource.Dispose();
        }

        var stoppedSessionIds = new HashSet<Guid>();
        AgentRunCheckpointRecord? requestedCheckpoint = null;
        foreach (var session in sessions)
        {
            activeRuns.TryGetValue(session.SessionId, out var activeRun);
            var latest = _sessionService.GetLatestCheckpoint(session.SessionId);
            var checkpoint = TrySaveStoppedCheckpoint(session, latest, activeRun, session.SessionId == sessionId);
            if (checkpoint is not null)
            {
                stoppedSessionIds.Add(session.SessionId);
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

        ClearPendingRequests(stoppedSessionIds);
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
        if (latest is not null && latest.Status is not (AgentRunStatus.Running or AgentRunStatus.WaitingForApproval))
        {
            return null;
        }

        var runRevision = latest?.RunRevision ?? activeRun?.RunRevision;
        if (runRevision is null)
        {
            return null;
        }

        var summary = isRequestedSession
            ? "Run stopped by the user before provider execution completed."
            : "Subsession stopped because the parent session was stopped.";
        return _sessionService.SaveCheckpoint(session.SessionId, runRevision.Value, AgentRunStatus.Stopped, summary);
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

    private void ClearPendingRequests(IReadOnlySet<Guid> sessionIds)
    {
        foreach (var id in sessionIds)
        {
            foreach (var pendingRequest in _permissionService.ListPendingRequests(id))
            {
                _permissionService.DeletePendingRequest(id, pendingRequest.RequestId);
            }
        }
    }

    private AgentProfileRecord? ResolveProfile(string? profileId)
        => string.IsNullOrWhiteSpace(profileId)
            ? null
            : _profileService.GetProfile(profileId);
}
