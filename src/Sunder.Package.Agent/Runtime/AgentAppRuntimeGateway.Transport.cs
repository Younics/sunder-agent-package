using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Package.Agent.OwnerProtocol.Generated;

namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway
{
    private static bool IsRuntimeAvailabilityFailure(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        if (exception is SunderRpcException rpcException)
        {
            return rpcException.Error.Kind is SunderRpcErrorKind.Unavailable
                or SunderRpcErrorKind.StaleEndpoint
                or SunderRpcErrorKind.NotFound;
        }

        return exception is HttpRequestException
               or System.Net.Sockets.SocketException
               or TimeoutException
               or OperationCanceledException;
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AgentAppRuntimeGateway));
    }
}

internal sealed class AgentOwnerRpcClient : IAgentOwnerClient
{
    private readonly ISunderRpcClient _client;
    private readonly AgentTransportProfile _proposedProfile;
    private readonly SemaphoreSlim _profileGate = new(1, 1);
    private AgentTransportProfile _transportProfile;
    private string? _profileEndpoint;

    public AgentOwnerRpcClient(ISunderRpcClient client)
    {
        _client = client;
        _proposedProfile = client.ResourceProfile is { } resourceProfile
            ? new AgentTransportProfile(
                resourceProfile.MaximumRpcValueBytes,
                resourceProfile.MaximumRpcValueBytes,
                resourceProfile.MaximumRpcValueBytes,
                resourceProfile.MaximumContentReferenceBytes,
                RuntimeReported: true)
            : AgentTransportProfile.StandardFallback;
        _transportProfile = _proposedProfile;
    }

    public AgentTransportProfile TransportProfile => Volatile.Read(ref _transportProfile);

    public async ValueTask<AgentTransportProfile> GetTransportProfileAsync(
        CancellationToken cancellationToken)
    {
        var endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        await EnsureTransportProfileAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return TransportProfile;
    }

    public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        AgentOwnerRpcOperation<TRequest, TResponse> operation,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
    {
        var endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        await EnsureTransportProfileAsync(endpoint, cancellationToken).ConfigureAwait(false);
        AgentRuntimePayloadLimits.EnsureRequestFits(request, TransportProfile);
        await using var scope = await _client.CreateCallScopeAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var generatedClient = new Wire.AgentRuntimeClient(scope, endpoint);
        return await operation.InvokeAsync(generatedClient, request, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
        AgentOwnerRpcStream<TRequest, TEvent> stream,
        TRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TRequest : class
        where TEvent : class
    {
        var endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        await EnsureTransportProfileAsync(endpoint, cancellationToken).ConfigureAwait(false);
        AgentRuntimePayloadLimits.EnsureRequestFits(request, TransportProfile);
        await using var scope = await _client.CreateCallScopeAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var generatedClient = new Wire.AgentRuntimeClient(scope, endpoint);
        await foreach (var item in stream.SubscribeAsync(generatedClient, request, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public async ValueTask<AgentAttachmentUploadHandle> UploadAttachmentAsync(
        AgentAttachmentUploadRequest upload,
        CancellationToken cancellationToken)
    {
        var info = AgentAttachmentUploadRules.Inspect(upload);
        var endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        await EnsureTransportProfileAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (upload.Content.Length > TransportProfile.ContentReferenceBytes)
        {
            throw new InvalidOperationException(
                "Attachment exceeds the negotiated Runtime content-reference limit.");
        }

        await using var scope = await _client.CreateCallScopeAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var source = new MemoryStream(upload.Content, writable: false);
        var content = await scope.RegisterContentAsync(
            endpoint,
            source,
            new SunderRpcContentRegistrationOptions(
                info.MediaType,
                info.FileName,
                upload.Content.LongLength,
                Repeatability: SunderRpcContentRepeatability.SingleUse,
                MaximumUses: 1),
            cancellationToken).ConfigureAwait(false);
        var descriptor = new AgentAttachmentUploadDescriptor(
            info.FileName,
            info.MediaType,
            upload.Content.Length,
            Convert.ToHexString(SHA256.HashData(upload.Content)).ToLowerInvariant());
        var request = new AgentAttachmentContentUploadRequest(descriptor, content);
        AgentRuntimePayloadLimits.EnsureRequestFits(request, TransportProfile);
        var generatedClient = new Wire.AgentRuntimeClient(scope, endpoint);
        var result = await AgentRuntimeOperations.AttachmentUploads.InvokeAsync(
            generatedClient,
            request,
            cancellationToken).ConfigureAwait(false);
        return result.Handle;
    }

    public async ValueTask<byte[]> DownloadAttachmentAsync(
        AgentAttachmentMetadata metadata,
        CancellationToken cancellationToken)
    {
        var endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        await EnsureTransportProfileAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await using var scope = await _client.CreateCallScopeAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var request = new AgentAttachmentContentDownloadRequest(metadata);
        AgentRuntimePayloadLimits.EnsureRequestFits(request, TransportProfile);
        var generatedClient = new Wire.AgentRuntimeClient(scope, endpoint);
        var result = await AgentRuntimeOperations.AttachmentDownloads.InvokeAsync(
            generatedClient,
            request,
            cancellationToken).ConfigureAwait(false);
        var reference = result.Content;
        if (reference.Length != metadata.SizeBytes
            || reference.Length is <= 0 or > AgentAttachmentUploadRules.MaxAttachmentBytes
            || reference.Length > TransportProfile.ContentReferenceBytes
            || !string.Equals(reference.Sha256, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Runtime returned attachment content that does not match its bounded metadata.");
        }

        await using var source = await scope.OpenContentAsync(reference, cancellationToken).ConfigureAwait(false);
        var content = new byte[checked((int)reference.Length)];
        await source.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        if (await source.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0
            || !SHA256.HashData(content).AsSpan().SequenceEqual(Convert.FromHexString(reference.Sha256)))
        {
            throw new InvalidDataException("Runtime attachment content failed length or digest validation.");
        }
        return content;
    }

    public async ValueTask DiscardAttachmentAsync(
        AgentAttachmentUploadHandle handle,
        CancellationToken cancellationToken)
        => _ = await InvokeAsync(
            AgentRuntimeOperations.AttachmentDiscards,
            new AgentAttachmentContentDiscardRequest(handle),
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<SunderRpcEndpointReference> ResolveEndpointAsync(
        CancellationToken cancellationToken)
    {
        var descriptor = AgentOwnerRuntimeRpc.Descriptor;
        var catalog = await _client.DiscoverAsync(
            descriptor.ContractId,
            cancellationToken).ConfigureAwait(false);
        var provider = catalog.Providers.SingleOrDefault(candidate =>
            candidate.State == SunderRpcProviderState.Active
            && string.Equals(candidate.ProviderId, AgentOwnerRuntimeRpc.ProviderId, StringComparison.Ordinal)
            && string.Equals(candidate.ContractVersion, descriptor.Version, StringComparison.Ordinal)
            && string.Equals(candidate.DescriptorSha256, descriptor.Sha256, StringComparison.Ordinal)
            && string.Equals(candidate.AbiSha256, descriptor.AbiSha256, StringComparison.Ordinal));
        return provider?.Endpoint
               ?? throw new SunderRpcException(new SunderRpcError(
                   SunderRpcErrorKind.Unavailable,
                   "agent.runtime.unavailable",
                   "The Agent Runtime owner provider is unavailable."));
    }

    private async ValueTask EnsureTransportProfileAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken)
    {
        if (string.Equals(Volatile.Read(ref _profileEndpoint), endpoint.Value, StringComparison.Ordinal))
        {
            return;
        }
        await _profileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(_profileEndpoint, endpoint.Value, StringComparison.Ordinal))
            {
                return;
            }
            await using var scope = await _client.CreateCallScopeAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var generatedClient = new Wire.AgentRuntimeClient(scope, endpoint);
            var request = new AgentTransportProfileRequest(
                _proposedProfile.RequestValueBytes,
                _proposedProfile.ResponseValueBytes,
                _proposedProfile.EventValueBytes,
                _proposedProfile.ContentReferenceBytes,
                _proposedProfile.RuntimeReported);
            var negotiated = await AgentRuntimeOperations.TransportProfile.InvokeAsync(
                generatedClient,
                request,
                cancellationToken).ConfigureAwait(false);
            ValidateTransportProfile(negotiated, _proposedProfile);
            Volatile.Write(ref _transportProfile, negotiated);
            Volatile.Write(ref _profileEndpoint, endpoint.Value);
        }
        finally
        {
            _profileGate.Release();
        }
    }

    private static void ValidateTransportProfile(
        AgentTransportProfile profile,
        AgentTransportProfile proposedProfile)
    {
        if (profile.RequestValueBytes is < AgentRuntimePayloadLimits.MinimumValueBytes
                or > AgentRuntimePayloadLimits.MaximumValueBytes
            || profile.ResponseValueBytes is < AgentRuntimePayloadLimits.MinimumValueBytes
                or > AgentRuntimePayloadLimits.MaximumValueBytes
            || profile.EventValueBytes is < AgentRuntimePayloadLimits.MinimumValueBytes
                or > AgentRuntimePayloadLimits.MaximumValueBytes
            || profile.ContentReferenceBytes is < AgentRuntimePayloadLimits.MinimumContentReferenceBytes
                or > AgentRuntimePayloadLimits.MaximumContentReferenceBytes)
        {
            throw new InvalidDataException("Runtime returned an invalid Agent transport profile.");
        }

        if (profile.RequestValueBytes > proposedProfile.RequestValueBytes
            || profile.ResponseValueBytes > proposedProfile.ResponseValueBytes
            || profile.EventValueBytes > proposedProfile.EventValueBytes
            || profile.ContentReferenceBytes > proposedProfile.ContentReferenceBytes
            || profile.RuntimeReported && !proposedProfile.RuntimeReported)
        {
            throw new InvalidDataException("Runtime returned an Agent transport profile above the proposed limits.");
        }
    }
}
