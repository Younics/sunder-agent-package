using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentProfileService
{
    // Discovery/descriptors are shared only by the projections of this request.
    // Each RPC still uses its exact generated binding, including authorization
    // and retirement checks; subsequent requests always rediscover providers.
    internal async Task<(IReadOnlyList<AgentProviderDescriptor> Providers,
        IReadOnlyList<AgentModelDescriptor> Models, AgentProviderReadiness? Readiness)> GetChatProviderCatalogAsync(
        string? providerId, CancellationToken cancellationToken)
    {
        var providers = await ListChatProvidersAsync(cancellationToken, omitUnavailable: true).ConfigureAwait(false);
        var selected = string.IsNullOrWhiteSpace(providerId)
            ? default
            : providers.FirstOrDefault(provider => string.Equals(
                provider.Descriptor.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        var modelsTask = ListChatModelsAsync(selected.Provider, cancellationToken);
        var readinessTask = GetChatProviderReadinessAsync(selected.Provider, selected.Descriptor?.ProviderId, cancellationToken);
        await Task.WhenAll(modelsTask, readinessTask).ConfigureAwait(false);
        return (providers.Select(static provider => provider.Descriptor)
                .OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(),
            await modelsTask.ConfigureAwait(false),
            await readinessTask.ConfigureAwait(false));
    }

    internal async Task<(IReadOnlyList<AgentEmbeddingProviderDescriptor> Providers,
        IReadOnlyList<AgentEmbeddingModelDescriptor> Models, AgentEmbeddingProviderReadiness? Readiness)> GetEmbeddingProviderCatalogAsync(
        string? providerId, CancellationToken cancellationToken)
    {
        var providers = await ListEmbeddingProvidersAsync(cancellationToken, omitUnavailable: true).ConfigureAwait(false);
        var selected = string.IsNullOrWhiteSpace(providerId)
            ? default
            : providers.FirstOrDefault(provider => string.Equals(
                provider.Descriptor.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        var modelsTask = ListEmbeddingModelsAsync(selected.Provider, cancellationToken);
        var readinessTask = GetEmbeddingProviderReadinessAsync(selected.Provider, selected.Descriptor?.ProviderId, cancellationToken);
        await Task.WhenAll(modelsTask, readinessTask).ConfigureAwait(false);
        return (providers.Select(static provider => provider.Descriptor)
                .OrderBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(),
            await modelsTask.ConfigureAwait(false),
            await readinessTask.ConfigureAwait(false));
    }
}
