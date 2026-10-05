using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.OwnerRuntime;
using OwnerProvider = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Owner_Runtime.Provider;

namespace Sunder.Package.Agent.Rpc.OwnerApp;

// Contract identity and provider selection come from the generated same-package facade.
internal sealed class OwnerRuntimeGateway(ISunderRpcClient rpc)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SunderRpcJsonConverterFactory() },
    };
    private readonly SemaphoreSlim _profileGate = new(1, 1);
    private readonly Wire.AgentTransportProfileRequest _proposal = new()
    {
        RequestValueBytes = rpc.ResourceProfile?.MaximumRpcValueBytes ?? 8 * 1024 * 1024,
        ResponseValueBytes = rpc.ResourceProfile?.MaximumRpcValueBytes ?? 8 * 1024 * 1024,
        EventValueBytes = rpc.ResourceProfile?.MaximumRpcValueBytes ?? 8 * 1024 * 1024,
        ContentReferenceBytes = rpc.ResourceProfile?.MaximumContentReferenceBytes ?? 64 * 1024 * 1024,
        RuntimeReported = rpc.ResourceProfile is not null,
    };
    private ProfileCache? _profileCache;

    internal Wire.AgentTransportProfile? NegotiatedProfile => Volatile.Read(ref _profileCache)?.Profile;

    public async ValueTask<Wire.AgentTransportProfile> GetTransportProfileAsync(CancellationToken token = default)
    {
        var provider = await ResolveAsync(token).ConfigureAwait(false);
        return await NegotiateAsync(provider, token).ConfigureAwait(false);
    }

    public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(TRequest request,
        Func<Wire.AgentRuntimeClient, TRequest, CancellationToken, ValueTask<TResponse>> operation, CancellationToken token = default)
        where TRequest : class where TResponse : class
    {
        var provider = await ResolveAsync(token).ConfigureAwait(false);
        var profile = await NegotiateAsync(provider, token).ConfigureAwait(false);
        EnsureRequestFits(request, profile);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        return await operation(Client(scope, provider), request, token).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(TRequest request,
        Func<Wire.AgentRuntimeClient, TRequest, CancellationToken, IAsyncEnumerable<TEvent>> operation,
        [EnumeratorCancellation] CancellationToken token = default) where TRequest : class where TEvent : class
    {
        var provider = await ResolveAsync(token).ConfigureAwait(false);
        var profile = await NegotiateAsync(provider, token).ConfigureAwait(false);
        EnsureRequestFits(request, profile);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        await foreach (var item in operation(Client(scope, provider), request, token).WithCancellation(token).ConfigureAwait(false)) yield return item;
    }

    public async ValueTask<Wire.AgentAttachmentUploadHandle> UploadAsync(AgentAttachmentUploadRequest upload, CancellationToken token = default)
    {
        var info = AgentAttachmentUploadRules.Inspect(upload);
        var provider = await ResolveAsync(token).ConfigureAwait(false);
        var profile = await NegotiateAsync(provider, token).ConfigureAwait(false);
        if (upload.Content.LongLength > profile.ContentReferenceBytes) throw new InvalidOperationException("Attachment exceeds the negotiated Runtime content-reference limit.");
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        await using var source = new MemoryStream(upload.Content, writable: false);
        var content = await new PackageApi(rpc).OwnerRuntime.CreateClient(scope, provider).RegisterContentAsync(source,
            new(info.MediaType, info.FileName, upload.Content.LongLength, Repeatability: SunderRpcContentRepeatability.SingleUse, MaximumUses: 1), token).ConfigureAwait(false);
        var request = new Wire.AgentAttachmentContentUploadRequest
        {
            Content = content,
            Upload = new() { FileName = info.FileName, MediaType = info.MediaType, SizeBytes = upload.Content.LongLength, Sha256 = Convert.ToHexStringLower(SHA256.HashData(upload.Content)) },
        };
        EnsureRequestFits(request, profile);
        return (await Client(scope, provider).AttachmentUploadAsync(request, token).ConfigureAwait(false)).Handle;
    }

    public async ValueTask<byte[]> DownloadAsync(Wire.AgentAttachmentMetadata metadata, CancellationToken token = default)
    {
        var provider = await ResolveAsync(token).ConfigureAwait(false);
        var profile = await NegotiateAsync(provider, token).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        var request = new Wire.AgentAttachmentContentDownloadRequest { Metadata = metadata };
        EnsureRequestFits(request, profile);
        var reference = (await Client(scope, provider).AttachmentDownloadAsync(request, token).ConfigureAwait(false)).Content;
        if (reference.Length != metadata.SizeBytes || reference.Length is <= 0 or > AgentAttachmentUploadRules.MaxAttachmentBytes
            || reference.Length > profile.ContentReferenceBytes || !string.Equals(reference.Sha256, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime returned attachment content that does not match its bounded metadata.");
        await using var source = await scope.OpenContentAsync(reference, token).ConfigureAwait(false);
        var bytes = new byte[checked((int)reference.Length)];
        await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await source.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0 || !SHA256.HashData(bytes).AsSpan().SequenceEqual(Convert.FromHexString(reference.Sha256)))
            throw new InvalidDataException("Runtime attachment content failed length or digest validation.");
        return bytes;
    }

    public async ValueTask DiscardAsync(Wire.AgentAttachmentUploadHandle handle, CancellationToken token = default)
        => _ = await InvokeAsync(new Wire.AgentAttachmentContentDiscardRequest { Handle = handle },
            static (client, request, cancellation) => client.AttachmentDiscardAsync(request, cancellation), token).ConfigureAwait(false);

    private Wire.AgentRuntimeClient Client(ISunderRpcCallScope scope, OwnerProvider provider)
        => new PackageApi(rpc).OwnerRuntime.CreateClient(scope, provider).SAgent_2druntime;

    private async ValueTask<OwnerProvider> ResolveAsync(CancellationToken token)
    {
        try { return await new PackageApi(rpc).OwnerRuntime.ResolveSelectedPackageAsync(token).ConfigureAwait(false); }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.NotFound && exception.Error.Code == "rpc.provider-not-found")
        { throw new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "agent.runtime.unavailable", "The Agent Runtime owner provider is unavailable.")); }
    }

    private async ValueTask<Wire.AgentTransportProfile> NegotiateAsync(OwnerProvider provider, CancellationToken token)
    {
        var identity = new ProfileIdentity(provider.PackageId, provider.PackageVersion, provider.ProviderId,
            provider.ActivationId, provider.ActivationEpoch, provider.SessionGeneration);
        var cached = Volatile.Read(ref _profileCache);
        if (cached?.Identity == identity) return cached.Profile;
        await _profileGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            cached = _profileCache;
            if (cached?.Identity == identity) return cached.Profile;
            await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
            var value = await Client(scope, provider).TransportProfileAsync(_proposal, token).ConfigureAwait(false);
            if (value.RequestValueBytes <= 0 || value.ResponseValueBytes <= 0 || value.EventValueBytes <= 0 || value.ContentReferenceBytes <= 0)
                throw new InvalidDataException("Runtime returned an invalid Agent transport profile.");
            if (value.RequestValueBytes > _proposal.RequestValueBytes || value.ResponseValueBytes > _proposal.ResponseValueBytes
                || value.EventValueBytes > _proposal.EventValueBytes || value.ContentReferenceBytes > _proposal.ContentReferenceBytes
                || value.RuntimeReported && !_proposal.RuntimeReported)
                throw new InvalidDataException("Runtime returned an Agent transport profile above the proposed limits.");
            Volatile.Write(ref _profileCache, new(identity, value));
            return value;
        }
        finally { _profileGate.Release(); }
    }

    private static void EnsureRequestFits<T>(T request, Wire.AgentTransportProfile profile)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions).LongLength > Math.Max(1, profile.RequestValueBytes - 64 * 1024))
            throw new InvalidOperationException("Agent request exceeds the negotiated Runtime value limit.");
    }

    private sealed record ProfileIdentity(string PackageId, string PackageVersion, string ProviderId,
        Guid ActivationId, long ActivationEpoch, long SessionGeneration);
    private sealed record ProfileCache(ProfileIdentity Identity, Wire.AgentTransportProfile Profile);
}
