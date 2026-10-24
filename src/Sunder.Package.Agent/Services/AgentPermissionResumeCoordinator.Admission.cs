using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentPermissionResumeCoordinator
{
    internal Task<AgentRunCheckpointRecord?> AdmitDecisionAsync(Guid sessionId, string requestId,
        bool approve, bool approveForSession, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = _backgroundWork
            ?? throw new InvalidOperationException("Permission command admission requires the Agent worker's background-work owner.");
        var admitted = new TaskCompletionSource<AgentRunCheckpointRecord?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = owner.RunOwnedAsync(async ownedToken =>
        {
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(ownedToken, cancellationToken);
            void Acknowledge() => admitted.TrySetResult(_sessionService.GetLatestCheckpoint(sessionId));
            try
            {
                return approve
                    ? await ApproveCoreAsync(sessionId, requestId, approveForSession, ownedToken, admission.Token, Acknowledge).ConfigureAwait(false)
                    : await DenyCoreAsync(sessionId, requestId, ownedToken, admission.Token, Acknowledge).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (error.CancellationToken == admission.Token && admission.IsCancellationRequested)
            {
                throw new OperationCanceledException(error.Message, error, cancellationToken.IsCancellationRequested ? cancellationToken : ownedToken);
            }
        });
        _ = ObserveDecisionAsync(work, admitted, sessionId, requestId);
        // Request cancellation can stop waiting for the durable acknowledgement.
        // After the claim/decision it cannot cancel the worker's continuation.
        return admitted.Task.WaitAsync(cancellationToken);
    }

    private async Task ObserveDecisionAsync(Task<AgentRunCheckpointRecord?> work,
        TaskCompletionSource<AgentRunCheckpointRecord?> admitted, Guid sessionId, string requestId)
    {
        try { admitted.TrySetResult(await work.ConfigureAwait(false)); }
        catch (OperationCanceledException error) { admitted.TrySetCanceled(error.CancellationToken); }
        catch (Exception error)
        {
            if (admitted.TrySetException(error)) return;
            _runEventLogger.LogRunEvent(PackageLogLevel.Error, sessionId, Guid.Empty, 0,
                "permission.owned_continuation.failed", "Accepted permission continuation failed after its acknowledgement.",
                attributes: new Dictionary<string, object?> { ["permission.request_id"] = requestId }, exception: error);
        }
    }
}
