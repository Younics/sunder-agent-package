using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class SemanticModelRuntimeResolver(
    IPackageExtensionCatalog extensionCatalog,
    MemorySemanticSettingsService settingsService)
{
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;
    private readonly MemorySemanticSettingsService _settingsService = settingsService;

    public IAgentRuntimeCatalog? RuntimeCatalog
        => _extensionCatalog.GetExtensions(PackageExtensionPoints.RuntimeCatalogs).FirstOrDefault();

    public async Task<ResolvedEmbeddingProvider?> ResolveForProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        var binding = RuntimeCatalog?.GetModelBinding(profileId, AgentModelCapabilityKinds.Embedding)
                      ?? ResolveLegacyBinding(RuntimeCatalog?.GetProfile(profileId));
        if (binding is null || string.IsNullOrWhiteSpace(binding.ProviderId) || string.IsNullOrWhiteSpace(binding.ModelId))
        {
            return null;
        }

        var provider = GetProvider(binding.ProviderId);
        if (provider is null)
        {
            return null;
        }

        var readiness = await provider.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
        return readiness.Status == AgentProviderReadinessStatus.Ready
            ? new ResolvedEmbeddingProvider(provider, binding.ProviderId, binding.ModelId)
            : null;
    }

    public async Task<SemanticEmbeddingContext> ResolveForSessionAsync(
        Guid sessionId,
        string? profileId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _settingsService.IsSemanticRetrievalEnabledAsync(cancellationToken))
        {
            return SemanticEmbeddingContext.Disabled("Semantic retrieval is disabled in package settings.");
        }

        var runtimeCatalog = RuntimeCatalog;
        if (runtimeCatalog?.GetSession(sessionId) is null)
        {
            return SemanticEmbeddingContext.Unavailable("Session not found.");
        }

        var profile = string.IsNullOrWhiteSpace(profileId)
            ? runtimeCatalog.GetSessionProfile(sessionId)
            : runtimeCatalog.GetProfile(profileId);
        var binding = string.IsNullOrWhiteSpace(profileId)
            ? runtimeCatalog.GetSessionModelBinding(sessionId, AgentModelCapabilityKinds.Embedding)
            : runtimeCatalog.GetModelBinding(profileId, AgentModelCapabilityKinds.Embedding);
        binding ??= ResolveLegacyBinding(profile);
        if (binding is null || string.IsNullOrWhiteSpace(binding.ProviderId) || string.IsNullOrWhiteSpace(binding.ModelId))
        {
            return SemanticEmbeddingContext.Disabled("No embedding provider/model is configured on this agent.");
        }

        var provider = GetProvider(binding.ProviderId);
        if (provider is null)
        {
            return SemanticEmbeddingContext.Unavailable($"Embedding provider '{binding.ProviderId}' is not installed.");
        }

        var readiness = await provider.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
        return readiness.Status == AgentProviderReadinessStatus.Ready
            ? SemanticEmbeddingContext.Ready(binding.ProviderId, binding.ModelId, provider.Descriptor.DisplayName)
            : SemanticEmbeddingContext.Unavailable(
                $"Embedding provider '{provider.Descriptor.DisplayName}' is not ready: {readiness.Message}");
    }

    public AgentProfileModelBindingRecord? ResolveSessionBinding(Guid sessionId)
    {
        var runtimeCatalog = RuntimeCatalog;
        return runtimeCatalog?.GetSessionModelBinding(sessionId, AgentModelCapabilityKinds.Embedding)
               ?? ResolveLegacyBinding(runtimeCatalog?.GetSessionProfile(sessionId));
    }

    private IAgentEmbeddingProvider? GetProvider(string providerId)
        => _extensionCatalog.GetExtensions(PackageExtensionPoints.EmbeddingProviders)
            .FirstOrDefault(candidate => string.Equals(
                candidate.Descriptor.ProviderId,
                providerId,
                StringComparison.OrdinalIgnoreCase));

    private static AgentProfileModelBindingRecord? ResolveLegacyBinding(AgentProfileRecord? profile)
        => profile is null
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

public sealed record ResolvedEmbeddingProvider(
    IAgentEmbeddingProvider Provider,
    string ProviderId,
    string ModelId);

public sealed record SemanticEmbeddingContext(
    string StatusLabel,
    string StatusText,
    bool IsReady,
    string? ProviderId = null,
    string? ModelId = null,
    string? ProviderDisplayName = null)
{
    public static SemanticEmbeddingContext Ready(string providerId, string modelId, string providerDisplayName)
        => new(
            "Ready",
            $"Semantic retrieval is active via {providerDisplayName} / {modelId}.",
            true,
            providerId,
            modelId,
            providerDisplayName);

    public static SemanticEmbeddingContext Disabled(string statusText) => new("Disabled", statusText, false);

    public static SemanticEmbeddingContext Unavailable(string statusText) => new("Unavailable", statusText, false);
}
