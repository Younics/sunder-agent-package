using System.Security.Cryptography;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway
{
    public Task<AgentAttachmentUploadRequest> LoadUploadRequestFromFileAsync(
        string path,
        CancellationToken cancellationToken = default)
        => AgentAttachmentService.LoadLocalUploadRequestFromFileAsync(path, cancellationToken);

    public AgentAttachmentInfo InspectUpload(AgentAttachmentUploadRequest upload)
        => AgentAttachmentService.InspectUploadContent(upload);

    public async Task<byte[]> ReadAttachmentBytesAsync(
        AgentAttachmentMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        using var content = new MemoryStream();
        var offset = 0;
        int? totalBytes = null;
        while (true)
        {
            var chunk = await InvokeAsync(
                AgentRuntimeOperations.AttachmentTransfers,
                new AgentAttachmentTransferRequest(
                    AgentAttachmentTransferKind.ReadDownloadChunk,
                    Offset: offset,
                    Metadata: metadata),
                cancellationToken).ConfigureAwait(false);
            var bytes = chunk.Content ?? [];
            if (bytes.Length > AgentRuntimePayloadLimits.AttachmentDownloadChunkBytes
                || chunk.NextOffset != offset + bytes.Length
                || chunk.TotalBytes is < 0 or > (int)AgentAttachmentService.MaxAttachmentBytes
                || totalBytes is not null && totalBytes != chunk.TotalBytes)
            {
                throw new InvalidDataException("Runtime returned an invalid attachment download chunk.");
            }

            totalBytes ??= chunk.TotalBytes;
            await content.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            offset = chunk.NextOffset;
            if (chunk.IsComplete)
            {
                if (offset != totalBytes)
                {
                    throw new InvalidDataException("Runtime completed an attachment download at the wrong length.");
                }
                return content.ToArray();
            }
            if (bytes.Length == 0 || offset >= totalBytes)
            {
                throw new InvalidDataException("Runtime attachment download made no progress.");
            }
        }
    }

    private async ValueTask<AgentRunCommandResult> InvokeRunWithAttachmentsAsync(
        AgentRunCommand command,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        CancellationToken cancellationToken)
    {
        if ((command.UserMessage?.Length ?? 0) > AgentRuntimePayloadLimits.MaximumRunMessageCharacters)
        {
            throw new InvalidOperationException(
                $"Agent message exceeds the {AgentRuntimePayloadLimits.MaximumRunMessageCharacters} character Runtime transport limit.");
        }
        if (attachments.Count > AgentAttachmentService.MaxAttachmentsPerMessage)
        {
            throw new InvalidOperationException(
                $"A message can include at most {AgentAttachmentService.MaxAttachmentsPerMessage} attachments.");
        }

        AgentRuntimePayloadLimits.EnsureRunCommandFits(command with
        {
            AttachmentHandles = Enumerable.Range(0, attachments.Count)
                .Select(_ => new AgentAttachmentUploadHandle(new string('0', 32)))
                .ToArray(),
        });

        var handles = new List<AgentAttachmentUploadHandle>(attachments.Count);
        try
        {
            foreach (var attachment in attachments)
            {
                AgentAttachmentService.InspectUploadContent(attachment);
                var descriptor = new AgentAttachmentUploadDescriptor(
                    attachment.FileName,
                    attachment.MediaType,
                    attachment.Content.Length,
                    Convert.ToHexString(SHA256.HashData(attachment.Content)).ToLowerInvariant());
                var started = await InvokeAsync(
                    AgentRuntimeOperations.AttachmentTransfers,
                    new AgentAttachmentTransferRequest(
                        AgentAttachmentTransferKind.BeginUpload,
                        Upload: descriptor),
                    cancellationToken).ConfigureAwait(false);
                var transferId = started.TransferId
                    ?? throw new InvalidDataException("Runtime did not return an attachment upload handle.");
                handles.Add(new AgentAttachmentUploadHandle(transferId));

                var offset = 0;
                while (offset < attachment.Content.Length)
                {
                    var count = Math.Min(
                        AgentRuntimePayloadLimits.AttachmentUploadChunkBytes,
                        attachment.Content.Length - offset);
                    var written = await InvokeAsync(
                        AgentRuntimeOperations.AttachmentTransfers,
                        new AgentAttachmentTransferRequest(
                            AgentAttachmentTransferKind.WriteUploadChunk,
                            transferId,
                            Offset: offset,
                            Content: attachment.Content.AsSpan(offset, count).ToArray()),
                        cancellationToken).ConfigureAwait(false);
                    if (written.NextOffset != offset + count
                        || written.TotalBytes != attachment.Content.Length)
                    {
                        throw new InvalidDataException("Runtime returned an invalid attachment upload offset.");
                    }
                    offset = written.NextOffset;
                }

                var completed = await InvokeAsync(
                    AgentRuntimeOperations.AttachmentTransfers,
                    new AgentAttachmentTransferRequest(
                        AgentAttachmentTransferKind.CompleteUpload,
                        transferId),
                    cancellationToken).ConfigureAwait(false);
                if (!completed.IsComplete || completed.NextOffset != attachment.Content.Length)
                {
                    throw new InvalidDataException("Runtime did not complete the attachment upload.");
                }
            }

            var request = command with { AttachmentHandles = handles };
            AgentRuntimePayloadLimits.EnsureRunCommandFits(request);
            return await InvokeAsync(
                AgentRuntimeOperations.Runs,
                request,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var handle in handles)
            {
                try
                {
                    _ = await _transport.InvokeAsync(
                        AgentRuntimeOperations.AttachmentTransfers,
                        new AgentAttachmentTransferRequest(
                            AgentAttachmentTransferKind.AbortUpload,
                            handle.TransferId),
                        _lifetime.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Runtime consumes successful handles; abandoned transfers also expire server-side.
                }
            }
        }
    }
}
