using System.Diagnostics;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway
{
    private enum ChangeObservationStartReason
    {
        Initial,
        HealthySnapshotHandoff,
        Recovery,
    }

    private void StartObservingChanges(
        ChangeObservationStartReason startReason = ChangeObservationStartReason.Initial)
    {
        CancellationTokenSource cancellation;
        int generation;
        lock (_observationLock)
        {
            if (_disposed || _observationCancellation is not null)
            {
                return;
            }

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _observationCancellation = cancellation;
            generation = ++_observationGeneration;
        }

        _ = ObserveChangesAsync(generation, startReason, cancellation.Token);
    }

    private void PauseObservingChanges()
    {
        CancellationTokenSource? cancellation;
        lock (_observationLock)
        {
            _observationGeneration++;
            cancellation = _observationCancellation;
            _observationCancellation = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private bool IsCurrentObservation(int generation)
    {
        lock (_observationLock)
        {
            return !_disposed
                   && _observationCancellation is not null
                   && generation == _observationGeneration;
        }
    }

    private async Task ObserveChangesAsync(
        int generation,
        ChangeObservationStartReason startReason,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        var reconnectDelay = InitialReconnectDelay;
        var nextSubscriptionReason = startReason;
        while (!cancellationToken.IsCancellationRequested && IsCurrentObservation(generation))
        {
            if (!_transport.IsAvailable)
            {
                SetConnectionStateIfCurrent(generation, AgentRuntimeConnectionState.Unavailable);
                nextSubscriptionReason = ChangeObservationStartReason.Recovery;
                if (!await DelayForReconnectAsync(reconnectDelay, cancellationToken).ConfigureAwait(false)) return;
                reconnectDelay = NextReconnectDelay(reconnectDelay);
                continue;
            }
            var connectedAt = Stopwatch.GetTimestamp();
            try
            {
                if (nextSubscriptionReason == ChangeObservationStartReason.Initial)
                {
                    SetConnectionStateIfCurrent(generation, AgentRuntimeConnectionState.Connecting);
                }
                else if (nextSubscriptionReason == ChangeObservationStartReason.Recovery)
                {
                    SetConnectionStateIfCurrent(generation, AgentRuntimeConnectionState.Reconnecting);
                }
                nextSubscriptionReason = ChangeObservationStartReason.Recovery;
                await foreach (var change in _transport.SubscribeAsync(
                                   AgentRuntimeOperations.Changes,
                                   new AgentChangeSubscription(
                                       _revision,
                                       SupportsTurnMutations: true),
                                   cancellationToken))
                {
                    if (!IsCurrentObservation(generation)) return;
                    var requiresSnapshot = ApplyChange(change, generation);
                    if (requiresSnapshot)
                    {
                        await ResnapshotAsync(generation, cancellationToken).ConfigureAwait(false);
                    }
                    SetConnectionStateIfCurrent(generation, AgentRuntimeConnectionState.Connected);
                }
                reconnectDelay = ResetReconnectDelayAfterStableConnection(connectedAt, reconnectDelay);
                SetConnectionStateIfCurrent(generation, AgentRuntimeConnectionState.Reconnecting);
                if (!await DelayForReconnectAsync(reconnectDelay, cancellationToken).ConfigureAwait(false)) return;
                reconnectDelay = NextReconnectDelay(reconnectDelay);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (IsRuntimeAvailabilityFailure(exception, cancellationToken))
            {
                SetConnectionStateIfCurrent(generation, AgentRuntimeConnectionState.Reconnecting);
                reconnectDelay = ResetReconnectDelayAfterStableConnection(connectedAt, reconnectDelay);
                if (!await DelayForReconnectAsync(reconnectDelay, cancellationToken).ConfigureAwait(false)) return;
                reconnectDelay = NextReconnectDelay(reconnectDelay);
            }
            catch
            {
                SetConnectionStateIfCurrent(generation, AgentRuntimeConnectionState.Unavailable);
                return;
            }
        }
    }

    private static TimeSpan ResetReconnectDelayAfterStableConnection(
        long connectedAt,
        TimeSpan reconnectDelay)
        => Stopwatch.GetElapsedTime(connectedAt) >= TimeSpan.FromSeconds(30)
            ? InitialReconnectDelay
            : reconnectDelay;

    private bool ApplyChange(AgentRuntimeChange change, int generation)
    {
        lock (_observationLock)
        {
            if (_disposed
                || _observationCancellation is null
                || generation != _observationGeneration)
            {
                return false;
            }

            return ApplyCurrentChange(change);
        }
    }

    private bool ApplyCurrentChange(AgentRuntimeChange change)
    {
        if (!string.IsNullOrWhiteSpace(change.RuntimeInstanceId))
        {
            var runtimeChanged = !string.IsNullOrWhiteSpace(_runtimeInstanceId)
                                 && !string.Equals(
                                     _runtimeInstanceId,
                                     change.RuntimeInstanceId,
                                     StringComparison.Ordinal);
            _runtimeInstanceId = change.RuntimeInstanceId;
            if (runtimeChanged)
            {
                _revision = change.Revision;
                InvalidateAll();
                return true;
            }
        }
        if (change.Kind is AgentRuntimeChangeKind.ResnapshotRequired)
        {
            _revision = change.Revision;
            InvalidateAll();
            return true;
        }
        if (change.Kind is not AgentRuntimeChangeKind.Connected
            && change.Revision > _revision + 1)
        {
            _revision = change.Revision;
            InvalidateAll();
            return true;
        }
        if (change.Revision <= _revision && change.Kind is not AgentRuntimeChangeKind.Connected) return false;
        _revision = change.Revision;
        switch (change.Kind)
        {
            case AgentRuntimeChangeKind.Connected:
                break;
            case AgentRuntimeChangeKind.Profile:
                InvalidateDashboard();
                Raise(ProfileChanged, change.ProfileId ?? string.Empty);
                break;
            case AgentRuntimeChangeKind.Catalog:
                InvalidateCatalog();
                Raise(SelectableCapabilitiesChanged);
                break;
            case AgentRuntimeChangeKind.Workspace:
                InvalidateDashboard();
                Raise(WorkspacesChanged);
                break;
            case AgentRuntimeChangeKind.Session:
                if (change.Session is { } session)
                {
                    CacheSession(session);
                }
                else if (change.SessionId is { } removedSessionId)
                {
                    RemoveCachedSession(removedSessionId);
                }
                if (change.SessionId is { } sessionId) Raise(SessionChanged, sessionId);
                break;
            case AgentRuntimeChangeKind.Turn:
                if (change.SessionId is { } turnSessionId && change.Turn is { } turn)
                {
                    CacheTurn(turn);
                    Raise(TurnChanged, turnSessionId, turn);
                    Raise(TurnMutated, new AgentTurnMutation(
                        turnSessionId,
                        turn.TurnId,
                        turn.ContentRevision,
                        AgentTurnMutationKind.Add,
                        BaseContentLength: 0,
                        Text: null,
                        turn.UpdatedAtUtc,
                        turn,
                        change.Revision));
                }
                break;
            case AgentRuntimeChangeKind.TurnMutation:
                if (change.TurnMutation is { } mutation)
                {
                    mutation = mutation with { RuntimeRevision = change.Revision };
                    var changedTurn = ApplyTurnMutationToCache(mutation);
                    Raise(TurnMutated, mutation);
                    if (changedTurn is not null)
                    {
                        Raise(TurnChanged, mutation.SessionId, changedTurn);
                    }
                }
                break;
            case AgentRuntimeChangeKind.TranscriptReset:
                if (change.SessionId is { } resetSessionId)
                {
                    RemoveCachedTurns(resetSessionId);
                    Raise(TranscriptReset, resetSessionId);
                }
                break;
            case AgentRuntimeChangeKind.RunActivity:
                if (change.SessionId is { } activitySessionId && change.RunActivity is { } activity)
                    Raise(RunActivityChanged, activitySessionId, activity);
                break;
            case AgentRuntimeChangeKind.Permission:
                InvalidateGlobalPermissions(change.Revision);
                if (change.SessionId is { } permissionSessionId)
                {
                    Raise(SessionChanged, permissionSessionId);
                }
                break;
        }
        return false;
    }

    private async Task ResnapshotAsync(int generation, CancellationToken cancellationToken)
    {
        await _resnapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCurrentObservation(generation))
            {
                return;
            }

            InvalidateAll();
            AgentChatSnapshotRequest? chatRequest;
            lock (_observationLock)
            {
                chatRequest = _activeChatSnapshotRequest;
            }
            if (chatRequest is not null)
            {
                var snapshot = await InvokeAsync(
                    AgentRuntimeOperations.ChatSnapshot,
                    chatRequest,
                    cancellationToken).ConfigureAwait(false);
                if (!IsCurrentObservation(generation))
                {
                    return;
                }
                lock (_observationLock)
                {
                    if (!IsCurrentObservation(generation)
                        || snapshot.Revision < _revision)
                    {
                        return;
                    }
                    _runtimeInstanceId = snapshot.RuntimeInstanceId;
                    ApplyChatSnapshotCache(snapshot);
                }
                Raise(ChatSnapshotReloaded, snapshot);
                return;
            }

            _ = await LoadDashboardAsync(cancellationToken).ConfigureAwait(false);
            if (!IsCurrentObservation(generation))
            {
                return;
            }
            Raise(ProfileChanged, string.Empty);
            Raise(SelectableCapabilitiesChanged);
            Raise(WorkspacesChanged);
        }
        finally
        {
            _resnapshotGate.Release();
        }
    }
}
