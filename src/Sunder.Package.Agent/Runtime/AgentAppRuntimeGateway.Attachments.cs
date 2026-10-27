using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway
{
    public Task<AgentAttachmentUploadRequest> LoadUploadRequestFromFileAsync(
        string path,
        CancellationToken cancellationToken = default)
        => AgentAttachmentUploadRules.LoadFromFileAsync(path, cancellationToken);

    public AgentAttachmentInfo InspectUpload(AgentAttachmentUploadRequest upload)
        => AgentAttachmentUploadRules.Inspect(upload);

    public Task<byte[]> ReadAttachmentBytesAsync(
        AgentAttachmentMetadata metadata,
        CancellationToken cancellationToken = default)
        => _resources.DownloadAttachmentAsync(metadata, cancellationToken).AsTask();

    private async ValueTask<AgentRunCommandResult> InvokeRunWithAttachmentsAsync(
        AgentRunCommand command,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        CancellationToken cancellationToken)
    {
        if (attachments.Count > AgentAttachmentUploadRules.MaxAttachmentsPerMessage)
        {
            throw new InvalidOperationException(
                $"A message can include at most {AgentAttachmentUploadRules.MaxAttachmentsPerMessage} attachments.");
        }

        var transportProfile = await _resources.GetTransportProfileAsync(cancellationToken).ConfigureAwait(false);

        AgentRuntimePayloadLimits.EnsureRequestFits(command with
        {
            AttachmentHandles = Enumerable.Range(0, attachments.Count)
                .Select(_ => new AgentAttachmentUploadHandle(new string('0', 32)))
                .ToArray(),
        }, transportProfile);

        var handles = new List<AgentAttachmentUploadHandle>(attachments.Count);
        try
        {
            foreach (var attachment in attachments)
            {
                AgentAttachmentUploadRules.Inspect(attachment);
                handles.Add(await _resources.UploadAttachmentAsync(
                    attachment,
                    cancellationToken).ConfigureAwait(false));
            }

            var request = command with { AttachmentHandles = handles.ToArray() };
            AgentRuntimePayloadLimits.EnsureRequestFits(request, _resources.TransportProfile);
            var result = await InvokeAsync(
                _owner.ExecuteRunCommandAsync,
                request,
                cancellationToken).ConfigureAwait(false);
            if (result.UserTurn is { } receipt && (receipt.SessionId != command.SessionId
                || receipt.TurnId != command.UserTurnId || receipt.Role != AgentMessageRole.User || receipt.Kind != AgentTurnKind.Message))
                throw new InvalidDataException("Runtime returned a different admitted user turn.");
            if (result.Checkpoint is { } checkpoint)
            {
                if (checkpoint.SessionId != command.SessionId)
                    throw new InvalidDataException("Runtime returned a checkpoint for another session.");
                lock (_cacheLock)
                {
                    if (_knownSessions.TryGetValue(command.SessionId, out var snapshot)
                        && IsCheckpointNewer(checkpoint, snapshot.Checkpoint))
                    {
                        var updated = snapshot with { Checkpoint = checkpoint };
                        _knownSessions[command.SessionId] = updated;
                        if (snapshot.Session.WorkspaceId is { } workspace
                            && _workspaceSessions.TryGetValue(workspace, out var items))
                        {
                            var index = items.FindIndex(item => item.Session.SessionId == command.SessionId);
                            if (index >= 0) items[index] = updated;
                        }
                    }
                }
                Raise(SessionChanged, command.SessionId);
            }
            if (result.UserTurn is { } turn)
            {
                CacheAdmissionReceipt(turn);
                // The receipt and the change stream may arrive in either order.
                // Do not advance the stream cursor here; exact row/revision
                // reconciliation makes duplicate presentation idempotent.
                Raise(TurnMutated, new AgentTurnMutation(
                    turn.SessionId, turn.TurnId, turn.ContentRevision,
                    AgentTurnMutationKind.Add, 0, null, turn.UpdatedAtUtc, turn));
            }
            // Accepted transfers are already consumed by Runtime. Do not hold
            // Send open on redundant cleanup RPCs after its authoritative receipt.
            handles.Clear();
            return result;
        }
        finally
        {
            foreach (var handle in handles)
            {
                try
                {
                    await _resources.DiscardAttachmentAsync(handle, _lifetime.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Runtime consumes successful handles; abandoned transfers also expire server-side.
                }
            }
        }
    }

    private static bool IsCheckpointNewer(AgentRunCheckpointRecord candidate, AgentRunCheckpointRecord? current)
        // Admission returns the preparing checkpoint, never a later transition
        // within that run. Keep any same-run checkpoint already observed by the
        // stream; wall-clock movement cannot make admission overwrite it.
        => current is null || candidate.RunRevision > current.RunRevision;
}
