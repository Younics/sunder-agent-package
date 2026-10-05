using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

internal interface IAgentOwnerClient
{
    AgentTransportProfile TransportProfile => AgentTransportProfile.StandardFallback;

    ValueTask<AgentTransportProfile> GetTransportProfileAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(TransportProfile);

    ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        AgentOwnerRpcOperation<TRequest, TResponse> operation,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class;

    IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
        AgentOwnerRpcStream<TRequest, TEvent> stream,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class
        where TEvent : class;

    ValueTask<AgentAttachmentUploadHandle> UploadAttachmentAsync(
        AgentAttachmentUploadRequest upload,
        CancellationToken cancellationToken)
        => ValueTask.FromException<AgentAttachmentUploadHandle>(
            new NotSupportedException("This Agent owner client does not support call-scoped attachment content."));

    ValueTask<byte[]> DownloadAttachmentAsync(
        AgentAttachmentMetadata metadata,
        CancellationToken cancellationToken)
        => ValueTask.FromException<byte[]>(
            new NotSupportedException("This Agent owner client does not support call-scoped attachment content."));

    ValueTask DiscardAttachmentAsync(
        AgentAttachmentUploadHandle handle,
        CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
