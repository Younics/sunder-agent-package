using System.Security.Cryptography;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Runtime;

// Agent owns attachment admission, digest checks and its negotiated presentation
// budget. Generated services own content registration, scopes and wire framing.
internal sealed class AgentOwnerResources(
    IServiceProviders<AgentServices.IAgentRuntimeService> owners,
    ISunderRpcClient rpc) : IAgentOwnerResources
{
    private readonly SemaphoreSlim _profileGate = new(1, 1);
    private readonly AgentTransportProfileRequest _proposal = new(
        rpc.ResourceProfile?.MaximumRpcValueBytes ?? 8 * 1024 * 1024,
        rpc.ResourceProfile?.MaximumRpcValueBytes ?? 8 * 1024 * 1024,
        rpc.ResourceProfile?.MaximumRpcValueBytes ?? 8 * 1024 * 1024,
        rpc.ResourceProfile?.MaximumContentReferenceBytes ?? 64 * 1024 * 1024,
        rpc.ResourceProfile is not null);
    private ProfileCache? _profileCache;

    public AgentTransportProfile TransportProfile => Volatile.Read(ref _profileCache)?.Profile
        ?? new(_proposal.RequestValueBytes, _proposal.ResponseValueBytes,
            _proposal.EventValueBytes, _proposal.ContentReferenceBytes, _proposal.RuntimeReported);

    public async ValueTask<AgentTransportProfile> GetTransportProfileAsync(CancellationToken token = default)
        => await NegotiateAsync(await ResolveAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);

    public async ValueTask<AgentAttachmentUploadHandle> UploadAttachmentAsync(AgentAttachmentUploadRequest upload, CancellationToken token = default)
    {
        var info = AgentAttachmentUploadRules.Inspect(upload);
        var owner = await ResolveAsync(token).ConfigureAwait(false);
        var profile = await NegotiateAsync(owner, token).ConfigureAwait(false);
        if (upload.Content.LongLength > profile.ContentReferenceBytes)
            throw new InvalidOperationException("Attachment exceeds the negotiated Runtime content-reference limit.");
        var descriptor = new AgentAttachmentUploadDescriptor(info.FileName, info.MediaType,
            upload.Content.Length, Convert.ToHexStringLower(SHA256.HashData(upload.Content)));
        // The existing envelope reserve is outside the inline descriptor budget;
        // the generated binding validates its actual content-reference framing.
        AgentRuntimePayloadLimits.EnsureRequestFits(descriptor, profile);
        await using var content = SunderContent.FromBytes(upload.Content, info.MediaType, info.FileName);
        var result = await owner.Service.UploadAttachmentAsync(new(descriptor, content), token).ConfigureAwait(false);
        return result.Handle;
    }

    public async ValueTask<byte[]> DownloadAttachmentAsync(AgentAttachmentMetadata metadata, CancellationToken token = default)
    {
        var owner = await ResolveAsync(token).ConfigureAwait(false);
        var profile = await NegotiateAsync(owner, token).ConfigureAwait(false);
        var request = new AgentAttachmentContentDownloadRequest(metadata);
        AgentRuntimePayloadLimits.EnsureRequestFits(request, profile);
        var result = await owner.Service.DownloadAttachmentAsync(request, token).ConfigureAwait(false);
        await using var content = result.Content;
        if (content.Length is not { } length || length != metadata.SizeBytes
            || length is <= 0 or > AgentAttachmentUploadRules.MaxAttachmentBytes
            || length > profile.ContentReferenceBytes
            || !string.Equals(content.Sha256, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime returned attachment content that does not match its bounded metadata.");
        await using var source = await content.OpenReadAsync(token).ConfigureAwait(false);
        var bytes = new byte[checked((int)length)];
        await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await source.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Runtime attachment content failed length or digest validation.");
        return bytes;
    }

    public async ValueTask DiscardAttachmentAsync(AgentAttachmentUploadHandle handle, CancellationToken token = default)
    {
        var owner = await ResolveAsync(token).ConfigureAwait(false);
        var profile = await NegotiateAsync(owner, token).ConfigureAwait(false);
        var request = new AgentAttachmentContentDiscardRequest(handle);
        AgentRuntimePayloadLimits.EnsureRequestFits(request, profile);
        _ = await owner.Service.DiscardAttachmentAsync(request, token).ConfigureAwait(false);
    }

    private async ValueTask<ServiceProvider<AgentServices.IAgentRuntimeService>> ResolveAsync(CancellationToken token)
    {
        var catalog = await owners.ListAsync(token).ConfigureAwait(false);
        var matches = catalog.Providers.Where(provider => provider.State == ServiceProviderState.Active
            && provider.PackageId == "younics.ai.agent" && provider.ProviderId == "agent.owner.runtime").Take(2).ToArray();
        if (matches.Length != 1)
            throw new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "agent.runtime.unavailable",
                "One exact Agent Runtime owner provider is required."));
        return matches[0];
    }

    private async ValueTask<AgentTransportProfile> NegotiateAsync(
        ServiceProvider<AgentServices.IAgentRuntimeService> owner, CancellationToken token)
    {
        var cached = Volatile.Read(ref _profileCache);
        if (ReferenceEquals(cached?.Owner, owner) && !owner.RetirementToken.IsCancellationRequested) return cached.Profile;
        await _profileGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            cached = _profileCache;
            if (ReferenceEquals(cached?.Owner, owner) && !owner.RetirementToken.IsCancellationRequested) return cached.Profile;
            var profile = await owner.Service.NegotiateTransportProfileAsync(_proposal, token).ConfigureAwait(false);
            if (profile.RequestValueBytes <= 0 || profile.ResponseValueBytes <= 0 || profile.EventValueBytes <= 0 || profile.ContentReferenceBytes <= 0)
                throw new InvalidDataException("Runtime returned an invalid Agent transport profile.");
            if (profile.RequestValueBytes > _proposal.RequestValueBytes || profile.ResponseValueBytes > _proposal.ResponseValueBytes
                || profile.EventValueBytes > _proposal.EventValueBytes || profile.ContentReferenceBytes > _proposal.ContentReferenceBytes
                || profile.RuntimeReported && !_proposal.RuntimeReported)
                throw new InvalidDataException("Runtime returned an Agent transport profile above the proposed limits.");
            await owner.EnsureCurrentAsync(token).ConfigureAwait(false);
            Volatile.Write(ref _profileCache, new(owner, profile));
            return profile;
        }
        finally { _profileGate.Release(); }
    }

    private sealed record ProfileCache(ServiceProvider<AgentServices.IAgentRuntimeService> Owner, AgentTransportProfile Profile);
}
