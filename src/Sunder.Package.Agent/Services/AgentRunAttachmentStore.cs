using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed class AgentRunAttachmentStore(AgentAttachmentService attachmentService)
{
    private readonly AgentAttachmentService _attachmentService = attachmentService;

    public async Task<IReadOnlyList<AgentStoredAttachment>> StoreAsync(
        Guid sessionId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        CancellationToken cancellationToken)
        => await StoreAsync(
            sessionId,
            Guid.NewGuid(),
            attachments,
            cancellationToken).ConfigureAwait(false);

    internal async Task<IReadOnlyList<AgentStoredAttachment>> StoreAsync(
        Guid sessionId,
        Guid userTurnId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        CancellationToken cancellationToken)
    {
        if (attachments.Count == 0)
        {
            return [];
        }

        if (attachments.Count > AgentAttachmentService.MaxAttachmentsPerMessage)
        {
            throw new InvalidOperationException($"A message can include at most {AgentAttachmentService.MaxAttachmentsPerMessage} attachments.");
        }

        var storedAttachments = new List<AgentStoredAttachment>(attachments.Count);
        try
        {
            foreach (var attachment in attachments)
            {
                storedAttachments.Add(await _attachmentService
                    .StoreAttachmentAsync(sessionId, userTurnId, attachment, cancellationToken)
                    .ConfigureAwait(false));
            }

            return storedAttachments;
        }
        catch
        {
            Cleanup(storedAttachments);
            throw;
        }
    }

    internal async Task<IReadOnlyList<AgentStoredAttachment>> AdoptAsync(
        Guid sessionId,
        Guid userTurnId,
        IReadOnlyList<AgentCompletedAttachmentUpload> uploads,
        CancellationToken cancellationToken)
    {
        var requests = new List<AgentAttachmentUploadRequest>(uploads.Count);
        foreach (var upload in uploads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(upload.SourcePath);
            if (!fileInfo.Exists || fileInfo.Length != upload.Descriptor.SizeBytes)
            {
                throw new InvalidDataException("Completed attachment upload storage does not match its descriptor.");
            }

            var content = await File.ReadAllBytesAsync(upload.SourcePath, cancellationToken).ConfigureAwait(false);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content))
                .ToLowerInvariant();
            if (!string.Equals(hash, upload.Descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Completed attachment upload hash does not match its descriptor.");
            }
            requests.Add(new AgentAttachmentUploadRequest(
                upload.Descriptor.FileName,
                upload.Descriptor.MediaType,
                content));
        }

        return await StoreAsync(sessionId, userTurnId, requests, cancellationToken).ConfigureAwait(false);
    }

    internal IReadOnlyList<Exception> Cleanup(
        IReadOnlyList<AgentStoredAttachment> attachments)
    {
        var failures = new List<Exception>();
        foreach (var attachment in attachments)
        {
            try
            {
                _attachmentService.DeleteStoredAttachment(attachment.Metadata);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        return failures;
    }
}
