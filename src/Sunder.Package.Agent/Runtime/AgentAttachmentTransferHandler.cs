using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Runtime;

internal sealed class AgentAttachmentTransferHandler(
    AgentAttachmentTransferService transfers,
    AgentAttachmentService attachments)
{
    public async ValueTask<AgentAttachmentContentUploadResult> UploadAsync(
        SunderRpcInvocationContext context,
        AgentAttachmentContentUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var upload = request.Upload;
        var content = request.Content;
        if (content.Length != upload.SizeBytes
            || !string.Equals(content.Sha256, upload.Sha256, StringComparison.OrdinalIgnoreCase)
            || content.Length is <= 0 or > AgentAttachmentService.MaxAttachmentBytes)
        {
            throw new AgentOwnerOperationException(
                "Attachment content reference does not match its bounded upload descriptor.");
        }
        await using var source = await context.OpenContentAsync(content, cancellationToken).ConfigureAwait(false);
        try
        {
            var handle = await transfers.ReceiveUploadAsync(upload, source, cancellationToken).ConfigureAwait(false);
            return new AgentAttachmentContentUploadResult(handle);
        }
        catch (AgentAttachmentTransferOperationException exception)
        {
            throw new AgentOwnerOperationException("Attachment content could not be accepted.", exception);
        }
    }

    public async ValueTask<AgentAttachmentContentDownloadResult> DownloadAsync(
        SunderRpcInvocationContext context,
        AgentAttachmentContentDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = request.Metadata;
        var path = attachments.GetAttachmentContentPath(metadata);
        var content = await context.RegisterContentFileAsync(
            path,
            new SunderRpcContentRegistrationOptions(
                metadata.MediaType,
                metadata.FileName,
                metadata.SizeBytes,
                Repeatability: SunderRpcContentRepeatability.SingleUse,
                MaximumUses: 1),
            cancellationToken).ConfigureAwait(false);
        return new AgentAttachmentContentDownloadResult(content);
    }

    public ValueTask<AgentAttachmentContentDiscardResult> DiscardAsync(
        AgentAttachmentContentDiscardRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentAttachmentContentDiscardResult(
            transfers.DiscardUpload(request.Handle)));
    }
}
