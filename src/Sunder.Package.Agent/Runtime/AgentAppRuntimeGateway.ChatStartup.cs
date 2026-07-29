namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway
{
    public async Task<AgentChatSnapshotProjection> LoadChatSnapshotAsync(
        AgentChatSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        int generation;
        bool hadActiveSnapshot;
        lock (_observationLock)
        {
            hadActiveSnapshot = _activeChatSnapshotRequest is not null;
            generation = ++_chatSnapshotLoadGeneration;
            _pendingChatSnapshotRequest = null;
            _pendingChatSnapshot = null;
        }
        PauseObservingChanges();

        try
        {
            var snapshot = await InvokeAsync(
                AgentRuntimeOperations.ChatSnapshot,
                request,
                cancellationToken).ConfigureAwait(false);
            lock (_observationLock)
            {
                _runtimeInstanceId = snapshot.RuntimeInstanceId;
                if (generation == _chatSnapshotLoadGeneration)
                {
                    _pendingChatSnapshotRequest = request;
                    _pendingChatSnapshot = snapshot;
                }
            }
            return snapshot;
        }
        catch (Exception exception)
        {
            var shouldResume = false;
            lock (_observationLock)
            {
                if (generation == _chatSnapshotLoadGeneration)
                {
                    _pendingChatSnapshotRequest = null;
                    _pendingChatSnapshot = null;
                    shouldResume = true;
                }
            }
            if (shouldResume
                && (hadActiveSnapshot
                    || IsRuntimeAvailabilityFailure(exception, cancellationToken)))
            {
                var startReason = ConnectionState switch
                {
                    AgentRuntimeConnectionState.Connected => ChangeObservationStartReason.HealthySnapshotHandoff,
                    AgentRuntimeConnectionState.Connecting => ChangeObservationStartReason.Initial,
                    _ => ChangeObservationStartReason.Recovery,
                };
                StartObservingChanges(startReason);
            }
            throw;
        }
    }

    public void CompleteChatSnapshot(AgentChatSnapshotProjection snapshot, bool applied)
    {
        var shouldResume = false;
        lock (_observationLock)
        {
            if (!ReferenceEquals(_pendingChatSnapshot, snapshot))
            {
                return;
            }

            if (applied)
            {
                ApplyChatSnapshotCache(snapshot);
                _activeChatSnapshotRequest = _pendingChatSnapshotRequest;
            }
            _pendingChatSnapshotRequest = null;
            _pendingChatSnapshot = null;
            shouldResume = true;
        }

        if (shouldResume)
        {
            StartObservingChanges(ChangeObservationStartReason.HealthySnapshotHandoff);
        }
    }

    private void ApplyChatSnapshotCache(AgentChatSnapshotProjection snapshot)
    {
        lock (_cacheLock)
        {
            _workspaceSessions.Clear();
            _knownSessions.Clear();
            var retainedStreamingTurns = _knownTurns.Values
                .Where(turn => turn.IsStreaming)
                .ToArray();
            _knownTurns.Clear();
            foreach (var turn in retainedStreamingTurns)
            {
                CacheTurnCore(turn);
            }
            if (snapshot.SelectedWorkspace is not null)
            {
                var workspaceItems = snapshot.WorkspaceSessions.ToList();
                _workspaceSessions[snapshot.SelectedWorkspace.WorkspaceId] = workspaceItems;
                foreach (var item in workspaceItems)
                {
                    _knownSessions[item.Session.SessionId] = item;
                }
            }
            foreach (var turn in snapshot.InitialTranscript.Turns)
            {
                CacheTurnCore(turn);
            }
            _revision = Math.Max(_revision, snapshot.Revision);
        }
    }
}
