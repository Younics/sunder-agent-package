using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed class AgentRunAttachmentStore(AgentAttachmentService attachmentService)
{
    private readonly AgentAttachmentService _attachmentService = attachmentService;

    public async Task<IReadOnlyList<AgentStoredAttachment>> StoreAsync(
        Guid sessionId,
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
        foreach (var attachment in attachments)
        {
            storedAttachments.Add(await _attachmentService.StoreAttachmentAsync(sessionId, attachment, cancellationToken).ConfigureAwait(false));
        }

        return storedAttachments;
    }
}
