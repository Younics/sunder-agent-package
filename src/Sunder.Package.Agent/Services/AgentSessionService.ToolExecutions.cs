using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentSessionService
{
    internal AgentToolExecutionRecord? GetToolExecution(Guid executionId)
        => _store.GetToolExecution(executionId);

    internal AgentToolResult? GetToolExecutionResult(Guid executionId)
        => _store.GetToolExecutionResult(executionId);

    internal AgentTranscriptToolDetailRecord? GetTranscriptToolDetail(
        AgentTranscriptToolDetailRequest request)
        => _store.GetTranscriptToolDetail(request);

    public Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetTranscriptToolDetail(request));
    }

    internal IReadOnlyList<AgentToolExecutionPreparationResult> PrepareToolExecutions(
        AgentDurableRunLease lease,
        IReadOnlyList<AgentToolExecutionPreparation> preparations)
    {
        IReadOnlyList<AgentToolExecutionPreparationResult>? results;
        lock (lease.SyncRoot)
        {
            results = _store.TryPrepareToolExecutions(lease.Key, lease.Epoch, preparations);
            if (results is not null)
            {
                foreach (var result in results)
                {
                    EnqueueLeaseNotification(
                        lease,
                        () => NotifyTurnAndSessionChanged(lease.Key.SessionId, result.ToolCallTurn));
                }
            }
        }

        if (results is null)
        {
            throw new AgentRunTranscriptWriteRejectedException();
        }
        DrainLeaseNotifications(lease);
        return results;
    }

    internal AgentToolExecutionStartResult StartToolExecution(
        AgentDurableRunLease lease,
        Guid executionId,
        string invocationFingerprint)
    {
        AgentToolExecutionStartResult? result;
        lock (lease.SyncRoot)
        {
            result = _store.TryStartToolExecution(
                lease.Key,
                lease.Epoch,
                executionId,
                invocationFingerprint);
        }

        return result ?? throw new AgentRunTranscriptWriteRejectedException();
    }

    internal void PublishToolExecutionStarted(
        AgentDurableRunLease lease,
        AgentTurnRecord toolCallTurn)
    {
        lock (lease.SyncRoot)
        {
            EnqueueLeaseNotification(
                lease,
                () => NotifyTurnChanged(lease.Key.SessionId, toolCallTurn));
        }
        DrainLeaseNotifications(lease);
    }

    internal AgentTurnRecord CompleteToolExecution(
        AgentDurableRunLease lease,
        Guid executionId,
        AgentToolExecutionStatus status,
        AgentToolResult result,
        string outcomeCode,
        bool allowPreparedReadOnlyCompletion = false,
        AgentPendingPermissionRequestRecord? permissionRequest = null,
        AgentPendingPermissionStatus? permissionStatus = null)
    {
        AgentToolExecutionCompletionResult? completion;
        lock (lease.SyncRoot)
        {
            completion = _store.TryCompleteToolExecution(
                lease.Key,
                lease.Epoch,
                executionId,
                status,
                result,
                outcomeCode,
                allowPreparedReadOnlyCompletion,
                permissionRequest,
                permissionStatus);
            if (completion is not null)
            {
                EnqueueLeaseNotification(
                    lease,
                    () => NotifyTurnAndSessionChanged(
                        lease.Key.SessionId,
                        completion.ToolResultTurn));
            }
        }

        if (completion is null)
        {
            throw new AgentRunTranscriptWriteRejectedException();
        }
        DrainLeaseNotifications(lease);
        return completion.ToolResultTurn;
    }

    internal AgentRunSuspensionResult SuspendRunAndCompleteToolExecution(
        AgentDurableRunLease lease,
        AgentRunSuspension suspension,
        string? suspensionSummary,
        Guid executionId,
        AgentToolResult result,
        string outcomeCode,
        AgentPendingPermissionRequestRecord? permissionRequest = null)
    {
        AgentToolExecutionSuspensionResult? persisted;
        lock (lease.SyncRoot)
        {
            persisted = _store.SuspendRunAndCompleteToolExecution(
                lease.Key,
                lease.Epoch,
                suspension,
                suspensionSummary,
                executionId,
                result,
                outcomeCode,
                permissionRequest);
            if (persisted is not null)
            {
                lease.AdvanceTo(lease.Epoch + 1);
                EnqueueLeaseNotification(
                    lease,
                    () => NotifyTurnAndSessionChanged(
                        lease.Key.SessionId,
                        persisted.Completion.ToolResultTurn));
            }
        }

        if (persisted is null)
        {
            throw new AgentRunTranscriptWriteRejectedException();
        }
        DrainLeaseNotifications(lease);
        return persisted.Suspension;
    }

    internal AgentTurnRecord? TryReplaceChildSuspensionToolResult(
        AgentDurableRunLease lease,
        string callId,
        AgentToolResult result)
    {
        AgentTurnRecord? turn;
        lock (lease.SyncRoot)
        {
            turn = _store.TryReplaceChildSuspensionToolResult(
                lease.Key,
                lease.Epoch,
                callId,
                result);
            if (turn is not null)
            {
                EnqueueLeaseNotification(
                    lease,
                    () => NotifyTurnAndSessionChanged(lease.Key.SessionId, turn));
            }
        }

        DrainLeaseNotifications(lease);
        return turn;
    }
}
