using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

// Agent attachment admission and negotiated presentation budgets. Content framing
// and transfer lifetime belong to the generated service's SDK resources.
internal interface IAgentOwnerResources
{
    AgentTransportProfile TransportProfile => AgentTransportProfile.StandardFallback;

    ValueTask<AgentTransportProfile> GetTransportProfileAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(TransportProfile);

    async ValueTask PrepareAsync<T>(T request, CancellationToken cancellationToken)
        => AgentRuntimePayloadLimits.EnsureRequestFits(request,
            await GetTransportProfileAsync(cancellationToken).ConfigureAwait(false));

    ValueTask<AgentAttachmentUploadHandle> UploadAttachmentAsync(AgentAttachmentUploadRequest upload, CancellationToken cancellationToken)
        => ValueTask.FromException<AgentAttachmentUploadHandle>(new NotSupportedException("This owner resource session cannot upload content."));

    ValueTask<byte[]> DownloadAttachmentAsync(AgentAttachmentMetadata metadata, CancellationToken cancellationToken)
        => ValueTask.FromException<byte[]>(new NotSupportedException("This owner resource session cannot download content."));

    ValueTask DiscardAttachmentAsync(AgentAttachmentUploadHandle handle, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
