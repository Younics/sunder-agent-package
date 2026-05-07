using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class ProfileConfiguredEmbeddingProviderResolver(IPackageExtensionCatalog extensionCatalog)
{
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;

    public async Task<ResolvedEmbeddingProvider?> ResolveAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        var binding = GetRuntimeCatalog()?.GetModelBinding(profileId, AgentModelCapabilityKinds.Embedding)
                       ?? ResolveLegacyEmbeddingBinding(profileId);
        if (binding is null
            || string.IsNullOrWhiteSpace(binding.ProviderId)
            || string.IsNullOrWhiteSpace(binding.ModelId))
        {
            return null;
        }

        var provider = _extensionCatalog.GetExtensions(PackageExtensionPoints.EmbeddingProviders)
            .FirstOrDefault(candidate => string.Equals(candidate.Descriptor.ProviderId, binding.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return null;
        }

        var readiness = await provider.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
        return readiness.Status == AgentProviderReadinessStatus.Ready
            ? new ResolvedEmbeddingProvider(provider, binding.ProviderId, binding.ModelId)
            : null;
    }

    private IAgentRuntimeCatalog? GetRuntimeCatalog()
        => _extensionCatalog.GetExtensions(PackageExtensionPoints.RuntimeCatalogs).FirstOrDefault();

    private AgentProfileModelBindingRecord? ResolveLegacyEmbeddingBinding(string profileId)
    {
        var profile = GetRuntimeCatalog()?.GetProfile(profileId);
        return profile is null
               || string.IsNullOrWhiteSpace(profile.EmbeddingProviderId)
               || string.IsNullOrWhiteSpace(profile.EmbeddingModelId)
            ? null
            : new AgentProfileModelBindingRecord(
                profile.ProfileId,
                AgentModelCapabilityKinds.Embedding,
                profile.EmbeddingProviderId,
                profile.EmbeddingModelId,
                SettingsJson: null,
                profile.UpdatedAtUtc);
    }

}

public sealed record ResolvedEmbeddingProvider(
    IAgentEmbeddingProvider Provider,
    string ProviderId,
    string ModelId);
