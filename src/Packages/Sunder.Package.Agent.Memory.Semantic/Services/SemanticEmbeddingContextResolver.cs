using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class SemanticEmbeddingContextResolver(
    IPackageExtensionCatalog extensionCatalog,
    MemorySemanticSettingsService settingsService)
{
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;
    private readonly MemorySemanticSettingsService _settingsService = settingsService;

    public async Task<SemanticEmbeddingContext> ResolveForSessionAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default)
    {
        if (!_settingsService.IsSemanticRetrievalEnabled())
        {
            return SemanticEmbeddingContext.Disabled("Semantic retrieval is disabled in package settings.");
        }

        var session = GetRuntimeCatalog()?.GetSession(sessionId);
        if (session is null)
        {
            return SemanticEmbeddingContext.Unavailable("Session not found.");
        }

        var profile = string.IsNullOrWhiteSpace(profileId)
            ? GetRuntimeCatalog()?.GetSessionProfile(sessionId)
            : GetRuntimeCatalog()?.GetProfile(profileId);
        var binding = string.IsNullOrWhiteSpace(profileId)
            ? GetRuntimeCatalog()?.GetSessionModelBinding(sessionId, AgentModelCapabilityKinds.Embedding)
            : GetRuntimeCatalog()?.GetModelBinding(profileId, AgentModelCapabilityKinds.Embedding);
        binding ??= profile is null ? null : ResolveLegacyEmbeddingBinding(profile.ProfileId);
        if (binding is null || string.IsNullOrWhiteSpace(binding.ProviderId) || string.IsNullOrWhiteSpace(binding.ModelId))
        {
            return SemanticEmbeddingContext.Disabled("No embedding provider/model is configured on this agent profile.");
        }

        var provider = _extensionCatalog.GetExtensions(PackageExtensionPoints.EmbeddingProviders)
            .FirstOrDefault(candidate => string.Equals(candidate.Descriptor.ProviderId, binding.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return SemanticEmbeddingContext.Unavailable($"Embedding provider '{binding.ProviderId}' is not installed.");
        }

        var readiness = await provider.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
        if (readiness.Status != AgentProviderReadinessStatus.Ready)
        {
            return SemanticEmbeddingContext.Unavailable($"Embedding provider '{provider.Descriptor.DisplayName}' is not ready: {readiness.Message}");
        }

        return SemanticEmbeddingContext.Ready(binding.ProviderId, binding.ModelId, provider.Descriptor.DisplayName);
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

public sealed record SemanticEmbeddingContext(
    string StatusLabel,
    string StatusText,
    bool IsReady,
    string? ProviderId = null,
    string? ModelId = null,
    string? ProviderDisplayName = null)
{
    public static SemanticEmbeddingContext Ready(string providerId, string modelId, string providerDisplayName)
        => new("Ready", $"Semantic retrieval is active via {providerDisplayName} / {modelId}.", true, providerId, modelId, providerDisplayName);

    public static SemanticEmbeddingContext Disabled(string statusText)
        => new("Disabled", statusText, false);

    public static SemanticEmbeddingContext Unavailable(string statusText)
        => new("Unavailable", statusText, false);
}
