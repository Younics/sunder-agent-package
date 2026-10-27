using Sunder.Package.Agent.Contracts.Models;

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
            _chatSnapshotLoadPending = true;
        }
        PauseObservingChanges();

        try
        {
            var snapshot = await InvokeAsync(
                _owner.GetChatSnapshotAsync,
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
        catch (Exception exception)
        {
            var shouldResume = false;
            lock (_observationLock)
            {
                if (generation == _chatSnapshotLoadGeneration)
                {
                    _pendingChatSnapshotRequest = null;
                    _pendingChatSnapshot = null;
                    _chatSnapshotLoadPending = false;
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
        var authoritativeCachesInvalidated = false;
        lock (_observationLock)
        {
            if (!ReferenceEquals(_pendingChatSnapshot, snapshot))
            {
                return;
            }

            if (applied)
            {
                var runtimeChanged = HasRuntimeInstanceChanged(
                    _runtimeInstanceId,
                    snapshot.RuntimeInstanceId);
                AdoptRuntimeInstance(snapshot.RuntimeInstanceId);
                authoritativeCachesInvalidated = ApplyChatSnapshotCache(snapshot, runtimeChanged);
                _activeChatSnapshotRequest = _pendingChatSnapshotRequest;
            }
            _pendingChatSnapshotRequest = null;
            _pendingChatSnapshot = null;
            _chatSnapshotLoadPending = false;
            shouldResume = true;
        }

        if (shouldResume)
        {
            StartObservingChanges(ChangeObservationStartReason.HealthySnapshotHandoff);
        }
        if (authoritativeCachesInvalidated)
        {
            RaiseAuthoritativeProjectionsChanged();
        }
    }

    private bool ApplyChatSnapshotCache(
        AgentChatSnapshotProjection snapshot,
        bool runtimeChanged)
    {
        lock (_cacheLock)
        {
            var authoritativeCachesInvalidated = runtimeChanged || snapshot.Revision > _revision;
            if (authoritativeCachesInvalidated)
            {
                _dashboard = null;
                _dashboardLoad = null;
                _globalPermissions = null;
                _globalPermissionRevision = runtimeChanged
                    ? -1
                    : Math.Max(_globalPermissionRevision, snapshot.Revision);
                _globalPermissionGeneration++;
                _catalogSnapshot = null;
                _catalogGeneration++;
                _catalogs.Clear();
            }
            _workspaceSessions.Clear();
            _knownSessions.Clear();
            var retainedStreamingTurns = runtimeChanged
                ? Array.Empty<AgentTurnRecord>()
                : _knownTurns.Values
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
            foreach (var turn in snapshot.InitialTranscript.Turns.Concat(
                         snapshot.InitialTranscript.Compact?.Entries.Select(entry => entry.Message).OfType<AgentTurnRecord>() ?? []))
            {
                CacheTurnCore(turn);
            }
            _revision = runtimeChanged
                ? snapshot.Revision
                : Math.Max(_revision, snapshot.Revision);
            return authoritativeCachesInvalidated;
        }
    }

    private static bool HasRuntimeInstanceChanged(
        string? current,
        string? incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(current))
        {
            return true;
        }
        return !string.Equals(current, incoming, StringComparison.Ordinal);
    }

    private void AdoptRuntimeInstance(string? runtimeInstanceId)
    {
        if (!string.IsNullOrWhiteSpace(runtimeInstanceId))
        {
            _runtimeInstanceId = runtimeInstanceId;
        }
    }
}
