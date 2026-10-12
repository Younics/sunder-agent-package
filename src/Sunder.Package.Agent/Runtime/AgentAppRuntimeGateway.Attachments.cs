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

            var request = command with { AttachmentHandles = handles };
            AgentRuntimePayloadLimits.EnsureRequestFits(request, _resources.TransportProfile);
            return await InvokeAsync(
                _owner.ExecuteRunCommandAsync,
                request,
                cancellationToken).ConfigureAwait(false);
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
}
