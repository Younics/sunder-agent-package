using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentSessionService(AgentLocalStore store, AgentRpcCatalog? rpcCatalog = null)
    : IAgentSessionGateway,
      IAgentTurnMutationGateway,
      IAgentTranscriptHeaderGateway,
      IAgentTranscriptToolDetailGateway
{
    private readonly AgentLocalStore _store = store;
    private readonly AgentRpcCatalog? _rpcCatalog = rpcCatalog;

    internal AgentLocalStore Store => _store;

    internal AgentMemoryConsistencyBarrier? GetMemoryConsistencyBarrier(Guid runId)
        => _store.GetMemoryConsistencyBarrier(runId);

    public event Action<Guid>? SessionChanged;

    public event Action<Guid, AgentTurnRecord>? TurnChanged;

    public event Action<AgentTurnMutation>? TurnMutated;

    public event Action<Guid>? TranscriptReset;

    public event Action<Guid, AgentRunActivityUpdate>? RunActivityChanged;

    public AgentSessionRecord CreateSession(
        string title,
        Guid? parentSessionId = null,
        Guid? rootSessionId = null,
        Guid? parentRunId = null,
        long? parentRunRevision = null,
        string? parentToolCallId = null,
        string? taskId = null,
        string? profileId = null,
        string? behaviorLoopId = null,
        string? agentKind = null,
        string? workspaceId = null)
    {
        workspaceId = ResolveWorkspaceId(parentSessionId, workspaceId);
        var session = _store.CreateSession(title, parentSessionId, rootSessionId, parentRunId, parentRunRevision, parentToolCallId, taskId, profileId, behaviorLoopId, agentKind, workspaceId);
        NotifySessionChanged(session.SessionId);
        return session;
    }

    public void UpdateSession(AgentSessionRecord session)
    {
        session = NormalizeSessionWorkspaceForUpdate(session);
        _store.UpdateSession(session);
        NotifySessionChanged(session.SessionId);
    }

    public void DeleteSession(Guid sessionId)
    {
        var deletedSessionIds = _store.DeleteSessionTree(sessionId, SnapshotSessionDataCleaners());
        CompleteSessionDeletion(deletedSessionIds);
    }

    public void DeleteSessionsForWorkspace(string workspaceId)
    {
        var deletedSessionIds = _store.DeleteSessionTreesForWorkspace(
            workspaceId,
            SnapshotSessionDataCleaners());
        CompleteSessionDeletion(deletedSessionIds);
    }

    internal void CompleteSessionDeletion(IReadOnlyList<Guid> deletedSessionIds)
    {
        foreach (var deletedSessionId in deletedSessionIds)
        {
            NotifySessionChanged(deletedSessionId);
        }
        DispatchPendingSessionCleanup();
    }

    private string ResolveWorkspaceId(Guid? parentSessionId, string? workspaceId)
    {
        var normalizedWorkspaceId = NormalizeWorkspaceId(workspaceId);
        if (parentSessionId is not null)
        {
            var parentWorkspaceId = NormalizeWorkspaceId(_store.GetSession(parentSessionId.Value)?.WorkspaceId);
            if (parentWorkspaceId is null)
            {
                throw new InvalidOperationException("Child sessions must have a parent session with an assigned workspace.");
            }

            if (normalizedWorkspaceId is not null
                && !string.Equals(normalizedWorkspaceId, parentWorkspaceId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Child sessions must use their parent session workspace.");
            }

            return parentWorkspaceId;
        }

        if (normalizedWorkspaceId is null)
        {
            throw new InvalidOperationException("Root sessions must be created with an explicit workspace id.");
        }

        if (IsUnassignedSessionsWorkspace(normalizedWorkspaceId))
        {
            throw new InvalidOperationException("Root sessions cannot be created in Unassigned Sessions.");
        }

        return normalizedWorkspaceId;
    }

    private AgentSessionRecord NormalizeSessionWorkspaceForUpdate(AgentSessionRecord session)
    {
        var workspaceId = NormalizeWorkspaceId(session.WorkspaceId)
            ?? throw new InvalidOperationException("Sessions must have an assigned workspace.");
        var current = _store.GetSession(session.SessionId);
        var currentWorkspaceId = NormalizeWorkspaceId(current?.WorkspaceId);
        if (currentWorkspaceId is null)
        {
            return session with { WorkspaceId = workspaceId };
        }

        if (IsUnassignedSessionsWorkspace(workspaceId) && !IsUnassignedSessionsWorkspace(currentWorkspaceId))
        {
            throw new InvalidOperationException("Sessions cannot be moved to Unassigned Sessions.");
        }

        if (!IsUnassignedSessionsWorkspace(workspaceId)
            && !IsUnassignedSessionsWorkspace(currentWorkspaceId)
            && !string.Equals(workspaceId, currentWorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Sessions cannot be moved between workspaces.");
        }

        if (!IsUnassignedSessionsWorkspace(workspaceId)
            && IsUnassignedSessionsWorkspace(currentWorkspaceId)
            && current?.ParentSessionId is null)
        {
            throw new InvalidOperationException("Unassigned root sessions cannot be moved into a workspace.");
        }

        return session with { WorkspaceId = workspaceId };
    }

    private static string? NormalizeWorkspaceId(string? workspaceId)
        => string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim();

    private static bool IsUnassignedSessionsWorkspace(string workspaceId)
        => string.Equals(workspaceId, AgentLocalStore.UnassignedSessionsWorkspaceId, StringComparison.OrdinalIgnoreCase);

    internal IReadOnlyList<AgentSessionDataCleanerIdentity> SnapshotSessionDataCleaners()
        => SnapshotSessionDataCleaners(_rpcCatalog);

    internal static IReadOnlyList<AgentSessionDataCleanerIdentity> SnapshotSessionDataCleaners(
        AgentRpcCatalog? rpcCatalog)
    {
        if (rpcCatalog is null)
        {
            return [];
        }

        var cleaners = AgentRpcInvocation.Snapshot(
            rpcCatalog,
            AgentRpcServices.SessionCleaners,
            static cleaner => cleaner.CleanerId);
        return cleaners
            .Where(cleaner => !string.IsNullOrWhiteSpace(cleaner.PackageId)
                              && cleaner.PackageId.Trim().Length <= 256
                              && !string.IsNullOrWhiteSpace(cleaner.Metadata)
                              && cleaner.Metadata.Trim().Length <= 512)
            .Select(cleaner => new AgentSessionDataCleanerIdentity(
                cleaner.PackageId.Trim(),
                cleaner.Metadata.Trim()))
            .Distinct()
            .ToArray();
    }

    private void DispatchPendingSessionCleanup()
    {
        if (_rpcCatalog is null)
        {
            return;
        }

        try
        {
            AgentSessionCleanupDispatcher.DispatchAvailableNow(_store, _rpcCatalog);
        }
        catch
        {
            // The ids-only jobs remain durable for startup or package-reactivation retry.
        }
    }

    internal AgentRunTransitionResult? TryTransitionRun(
        AgentDurableRunLease lease,
        AgentRunStatus status,
        string? summary)
    {
        AgentRunTransitionResult? transition;
        lock (lease.SyncRoot)
        {
            transition = _store.TryTransitionRun(lease.Key, lease.Epoch, status, summary);
            if (transition is null)
            {
                return null;
            }

            lease.AdvanceTo(transition.Run.Epoch);
            EnqueueLeaseNotification(
                lease,
                () => NotifyCompletedStreamingTurnsAndSessionChanged(
                    lease.Key.SessionId,
                    transition.CompletedStreamingTurns,
                    transition.ToolResultTurns));
        }
        DrainLeaseNotifications(lease);
        return transition;
    }

    internal AgentUserTurnAdmissionResult AdmitUserTurn(AgentUserTurnAdmissionRequest request)
    {
        var cleaners = request.AdmissionKind == AgentRunAdmissionKind.Rollback
            ? SnapshotSessionDataCleaners()
            : [];
        var result = _store.AdmitUserTurn(request, cleaners);
        if (result.IsExisting)
        {
            return result;
        }

        NotifyRunAdmissionChanged(result);
        foreach (var deletedSessionId in result.Rollback?.DeletedSessionIds ?? [])
        {
            NotifySessionChanged(deletedSessionId);
        }
        if (result.Rollback?.DeletedSessionIds.Count > 0)
        {
            DispatchPendingSessionCleanup();
        }
        return result;
    }

    internal AgentRunTransitionResult? TryBeginAdmittedRunExecution(
        AgentDurableRunLease lease,
        string summary)
    {
        AgentRunTransitionResult? transition;
        lock (lease.SyncRoot)
        {
            transition = _store.TryBeginAdmittedRunExecution(lease.Key, lease.Epoch, summary);
            if (transition is null)
            {
                return null;
            }
            lease.AdvanceTo(transition.Run.Epoch);
            EnqueueLeaseNotification(
                lease,
                () => NotifySessionChanged(lease.Key.SessionId));
        }
        DrainLeaseNotifications(lease);
        return transition;
    }

    internal AgentRunStartPersistenceResult? TryStartRun(
        AgentDurableRunLease lease,
        string userMessage,
        IReadOnlyList<AgentStoredAttachment> attachments,
        Guid? rollbackAnchorTurnId,
        string runningSummary)
        => TryStartRun(
            lease,
            Guid.NewGuid(),
            userMessage,
            attachments,
            rollbackAnchorTurnId,
            runningSummary);

    internal AgentRunStartPersistenceResult? TryStartRun(
        AgentDurableRunLease lease,
        Guid userTurnId,
        string userMessage,
        IReadOnlyList<AgentStoredAttachment> attachments,
        Guid? rollbackAnchorTurnId,
        string runningSummary)
    {
        var activeCleaners = rollbackAnchorTurnId is null
            ? []
            : SnapshotSessionDataCleaners();
        AgentRunStartPersistenceResult? result;
        lock (lease.SyncRoot)
        {
            result = _store.TryStartRun(
                lease.Key,
                lease.Epoch,
                userTurnId,
                userMessage,
                attachments,
                rollbackAnchorTurnId,
                runningSummary,
                activeCleaners);
            if (result is null)
            {
                return null;
            }

            lease.AdvanceTo(result.Transition.Run.Epoch);
            EnqueueLeaseNotification(
                lease,
                () => NotifyRunStartChanged(
                    lease.Key.SessionId,
                    result.UserTurn,
                    result.Rollback is not null));
        }
        DrainLeaseNotifications(lease);

        if (result.Rollback is not null)
        {
            foreach (var deletedSessionId in result.Rollback.DeletedSessionIds)
            {
                NotifySessionChanged(deletedSessionId);
            }
            DispatchPendingSessionCleanup();
        }

        return result;
    }

    internal AgentRunTransitionResult? TryStopRun(
        AgentDurableRunLease lease,
        string summary)
    {
        AgentRunStopPersistenceResult? transition;
        lock (lease.SyncRoot)
        {
            transition = _store.TryStopRunAndActivePermissions(
                lease.Key,
                lease.Epoch,
                summary);
            if (transition is null)
            {
                return null;
            }

            lease.AdvanceTo(transition.Transition.Run.Epoch);
            EnqueueLeaseNotification(
                lease,
                () => NotifyCompletedStreamingTurnsAndSessionChanged(
                    lease.Key.SessionId,
                    transition.CompletedStreamingTurns,
                    transition.Transition.ToolResultTurns));
        }
        DrainLeaseNotifications(lease);
        return transition.Transition;
    }

    internal AgentRunSuspensionResult? SuspendRun(
        AgentDurableRunLease lease,
        AgentRunSuspension suspension,
        string? summary)
    {
        AgentRunSuspensionResult? result;
        lock (lease.SyncRoot)
        {
            result = _store.SuspendRun(
                lease.Key,
                lease.Epoch,
                suspension,
                summary);
            if (result is not null)
            {
                lease.AdvanceTo(lease.Epoch + 1);
            }
        }
        return result;
    }

    internal AgentPermissionSuspensionPersistenceResult? SavePendingPermissionRequestAndSuspendRun(
        AgentPendingPermissionRequestRecord request,
        AgentDurableRunLease lease)
    {
        AgentPermissionSuspensionPersistenceResult? result;
        lock (lease.SyncRoot)
        {
            result = _store.SavePendingPermissionRequestAndSuspendRun(request, lease.Epoch);
            if (result is not null)
            {
                lease.AdvanceTo(lease.Epoch + 1);
            }
        }
        return result;
    }

    internal AgentRunSuspensionResult? SuspendRun(
        AgentDurableRunKey key,
        AgentRunSuspension suspension,
        string? summary)
        => _store.SuspendRun(key, suspension, summary);

    internal AgentChildJoinTransitionResult CompleteChildJoinTask(
        AgentDurableRunLease lease,
        string continuationToken,
        AgentChildJoinTaskResult completedTask)
    {
        AgentChildJoinTransitionResult result;
        lock (lease.SyncRoot)
        {
            result = _store.CompleteChildJoinTask(
                lease.Key,
                lease.Epoch,
                continuationToken,
                completedTask);
            if (result.Epoch is { } epoch)
            {
                lease.AdvanceTo(epoch);
                EnqueueLeaseNotification(
                    lease,
                    () => NotifySessionChanged(lease.Key.SessionId));
            }
        }
        DrainLeaseNotifications(lease);
        return result;
    }

    internal AgentChildJoinTransitionResult CompleteChildJoinTask(
        AgentDurableRunKey key,
        string continuationToken,
        AgentChildJoinTaskResult completedTask)
        => _store.CompleteChildJoinTask(key, continuationToken, completedTask);

    internal AgentParentContinuationDispatchResult? TryClaimParentContinuationWork(
        string workId,
        AgentDurableRunLease lease,
        string continuationToken)
    {
        AgentParentContinuationDispatchResult? result;
        lock (lease.SyncRoot)
        {
            result = _store.TryClaimParentContinuationWork(
                workId,
                lease.Key,
                lease.Epoch,
                continuationToken);
            if (result is not null && result.Run.Epoch > lease.Epoch)
            {
                lease.AdvanceTo(result.Run.Epoch);
                EnqueueLeaseNotification(
                    lease,
                    () => NotifySessionChanged(lease.Key.SessionId));
            }
        }
        DrainLeaseNotifications(lease);
        return result;
    }

    internal bool MarkParentContinuationExecutionStarted(string workId)
        => _store.MarkParentContinuationExecutionStarted(workId);

    internal bool CompleteParentContinuationWork(string workId, bool failed, string? error)
        => _store.CompleteParentContinuationWork(workId, failed, error);

    internal bool RecordParentContinuationRetryPending(string workId, string error)
        => _store.RecordParentContinuationRetryPending(workId, error);

    internal AgentRunBudgetState ChargeRunBudget(
        AgentDurableRunLease lease,
        AgentRunBudgetCharge charge)
    {
        lock (lease.SyncRoot)
        {
            return _store.ChargeRunBudget(lease.Key, lease.Epoch, charge)
                   ?? throw new AgentRunTranscriptWriteRejectedException();
        }
    }

    public void ReportRunActivity(Guid sessionId, long runRevision, AgentRunActivityKind kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        NotifyRunActivityChanged(
            sessionId,
            new AgentRunActivityUpdate(runRevision, kind, text.Trim(), DateTimeOffset.UtcNow));
    }

    public AgentTurnRecord UpdateTextTurn(Guid turnId, string content)
    {
        var turn = _store.UpdateTextTurn(turnId, content);
        NotifyTurnChanged(
            turn.SessionId,
            turn,
            new AgentTurnMutation(
                turn.SessionId,
                turn.TurnId,
                turn.ContentRevision,
                AgentTurnMutationKind.Replace,
                BaseContentLength: 0,
                content,
                turn.UpdatedAtUtc,
                turn));
        return turn;
    }

    internal AgentTurnRecord UpdateTextTurn(
        AgentDurableRunLease lease,
        Guid turnId,
        string content)
    {
        AgentTurnWriteResult? result;
        lock (lease.SyncRoot)
        {
            result = _store.TryUpdateTextTurn(
                lease.Key,
                lease.Epoch,
                turnId,
                content);
            if (result is not null)
            {
                EnqueueLeaseNotification(lease, () => NotifyTurnChanged(
                    lease.Key.SessionId,
                    result.Turn,
                    CreateTurnMutation(result)));
            }
        }

        if (result is null)
        {
            throw new AgentRunTranscriptWriteRejectedException();
        }

        DrainLeaseNotifications(lease);
        return result.Turn;
    }

    internal AgentTurnRecord CompleteTextTurn(
        AgentDurableRunLease lease,
        Guid turnId)
    {
        AgentTurnWriteResult? result;
        lock (lease.SyncRoot)
        {
            result = _store.TryCompleteTextTurn(
                lease.Key,
                lease.Epoch,
                turnId);
            if (result is not null)
            {
                EnqueueLeaseNotification(lease, () => NotifyTurnChanged(
                    lease.Key.SessionId,
                    result.Turn,
                    CreateTurnMutation(result)));
            }
        }

        if (result is null)
        {
            throw new AgentRunTranscriptWriteRejectedException();
        }

        DrainLeaseNotifications(lease);
        return result.Turn;
    }

    public AgentTurnRecord AppendToolCallTurn(Guid sessionId, AgentMessageRole role, string callId, string toolId, string argumentsJson)
    {
        var turn = _store.AppendToolCallTurn(sessionId, role, callId, toolId, argumentsJson);
        NotifyTurnAndSessionChanged(sessionId, turn);
        return turn;
    }

    internal AgentTurnRecord AppendToolCallTurn(
        AgentDurableRunLease lease,
        AgentMessageRole role,
        string callId,
        string toolId,
        string argumentsJson)
    {
        AgentTurnRecord? turn;
        lock (lease.SyncRoot)
        {
            turn = _store.TryAppendToolCallTurn(
                lease.Key,
                lease.Epoch,
                role,
                callId,
                toolId,
                argumentsJson);
            if (turn is not null)
            {
                EnqueueLeaseNotification(
                    lease,
                    () => NotifyTurnAndSessionChanged(lease.Key.SessionId, turn));
            }
        }

        if (turn is null)
        {
            throw new AgentRunTranscriptWriteRejectedException();
        }

        DrainLeaseNotifications(lease);
        return turn;
    }

    public AgentTurnRecord AppendToolResultTurn(
        Guid sessionId,
        string callId,
        string toolId,
        string? argumentsJson,
        string? content,
        string? resultSummary,
        string? structuredPayloadJson,
        string? sourcesJson,
        bool wasTruncated,
        bool isError,
        string? errorCode,
        string? backendId,
        string? presentationPayloadJson = null)
    {
        var turn = _store.AppendToolResultTurn(
            sessionId,
            callId,
            toolId,
            argumentsJson,
            content,
            resultSummary,
            structuredPayloadJson,
            sourcesJson,
            wasTruncated,
            isError,
            errorCode,
            backendId,
            presentationPayloadJson);
        NotifyTurnAndSessionChanged(sessionId, turn);
        return turn;
    }

    internal AgentTurnRecord AppendToolResultTurn(
        AgentDurableRunLease lease,
        string callId,
        string toolId,
        string? argumentsJson,
        string? content,
        string? resultSummary,
        string? structuredPayloadJson,
        string? sourcesJson,
        bool wasTruncated,
        bool isError,
        string? errorCode,
        string? backendId,
        string? presentationPayloadJson = null)
    {
        AgentTurnRecord? turn;
        lock (lease.SyncRoot)
        {
            turn = _store.TryAppendToolResultTurn(
                lease.Key,
                lease.Epoch,
                callId,
                toolId,
                argumentsJson,
                content,
                resultSummary,
                structuredPayloadJson,
                sourcesJson,
                wasTruncated,
                isError,
                errorCode,
                backendId,
                presentationPayloadJson);
            if (turn is not null)
            {
                EnqueueLeaseNotification(
                    lease,
                    () => NotifyTurnAndSessionChanged(lease.Key.SessionId, turn));
            }
        }

        if (turn is null)
        {
            throw new AgentRunTranscriptWriteRejectedException();
        }

        DrainLeaseNotifications(lease);
        return turn;
    }

    public AgentRunCheckpointRecord SaveCheckpoint(Guid sessionId, long runRevision, AgentRunStatus status, string? summary)
    {
        var result = _store.SaveCheckpointWithCompletedTurns(
            sessionId,
            runRevision,
            status,
            summary);
        NotifyCompletedStreamingTurnsAndSessionChanged(
            sessionId,
            result.CompletedStreamingTurns,
            result.ToolResultTurns);
        return result.Checkpoint;
    }

    internal void PublishCommittedCheckpoint(AgentCheckpointPersistenceResult result)
    {
        NotifyCompletedStreamingTurnsAndSessionChanged(
            result.Checkpoint.SessionId,
            result.CompletedStreamingTurns,
            result.ToolResultTurns);
    }

    internal void PublishCommittedSessionChanged(Guid sessionId)
        => NotifySessionChanged(sessionId);

    internal void PublishCommittedPermissionDecision(
        AgentCheckpointPersistenceResult finalization,
        AgentTurnRecord toolResultTurn)
    {
        QueueSessionNotification(finalization.Checkpoint.SessionId, () =>
        {
            DispatchCompletedStreamingTurns(
                finalization.Checkpoint.SessionId,
                finalization.CompletedStreamingTurns);
            DispatchTurnChanged(
                finalization.Checkpoint.SessionId,
                toolResultTurn);
            foreach (var terminalizedTurn in finalization.ToolResultTurns)
            {
                if (terminalizedTurn.TurnId != toolResultTurn.TurnId)
                {
                    DispatchTurnChanged(finalization.Checkpoint.SessionId, terminalizedTurn);
                }
            }
            DispatchSessionChanged(finalization.Checkpoint.SessionId);
        });
    }

    internal AgentDurableRunRecord ReserveRun(Guid sessionId, string profileId, string userMessage)
        => _store.ReserveRun(sessionId, profileId, userMessage);

    public AgentSessionContextCheckpointRecord SaveSessionContextCheckpoint(
        Guid sessionId,
        Guid? firstOmittedTurnId,
        Guid? lastOmittedTurnId,
        int omittedTurnCount,
        string summaryText,
        string? detailsJson)
    {
        var checkpoint = _store.SaveSessionContextCheckpoint(
            sessionId,
            firstOmittedTurnId,
            lastOmittedTurnId,
            omittedTurnCount,
            summaryText,
            detailsJson);
        NotifySessionChanged(sessionId);
        return checkpoint;
    }

}
