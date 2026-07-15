namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway
{
    public async Task<AgentChatSnapshotProjection> LoadChatSnapshotAsync(
        AgentChatSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        int generation;
        lock (_observationLock)
        {
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
                if (generation == _chatSnapshotLoadGeneration)
                {
                    _pendingChatSnapshotRequest = request;
                    _pendingChatSnapshot = snapshot;
                }
            }
            return snapshot;
        }
        catch
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
            if (shouldResume)
            {
                StartObservingChanges();
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
            StartObservingChanges();
        }
    }

    private void ApplyChatSnapshotCache(AgentChatSnapshotProjection snapshot)
    {
        lock (_cacheLock)
        {
            _workspaceSessions.Clear();
            _knownSessions.Clear();
            if (snapshot.SelectedWorkspace is not null)
            {
                var workspaceItems = snapshot.WorkspaceSessions.ToList();
                _workspaceSessions[snapshot.SelectedWorkspace.WorkspaceId] = workspaceItems;
                foreach (var item in workspaceItems)
                {
                    _knownSessions[item.Session.SessionId] = item;
                }
            }
            _revision = Math.Max(_revision, snapshot.Revision);
        }
    }
}
