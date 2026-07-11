using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.PackageViews;

internal static class AgentProfileProviderModelCatalog
{
    public static IProviderModelCatalog CreateChat(AgentProfileService service)
        => new ProviderModelCatalogAdapter(
            () => service.ListChatProviders()
                .Select(provider => new ProviderCatalogOption(
                    provider.Descriptor.ProviderId,
                    provider.Descriptor.DisplayName,
                    provider.Descriptor.PackageId))
                .ToArray(),
            async (providerId, cancellationToken) =>
            {
                var modelsTask = service.ListChatModelsAsync(providerId, cancellationToken);
                var readinessTask = service.GetChatProviderReadinessAsync(providerId, cancellationToken);
                await Task.WhenAll(modelsTask, readinessTask);
                var readiness = await readinessTask;
                return new ProviderModelCatalogResult(
                    (await modelsTask).Select(ProviderModelCatalogAdapter.ToCatalogOption).ToArray(),
                    readiness is null
                        ? "No chat provider selected."
                        : $"Chat provider status: {readiness.Status} - {readiness.Message}",
                    readiness is null || readiness.Status == AgentProviderReadinessStatus.Ready
                        ? null
                        : readiness.Message);
            });

    public static IProviderModelCatalog CreateEmbedding(AgentProfileService service)
        => new ProviderModelCatalogAdapter(
            () => service.ListEmbeddingProviders()
                .Select(provider => new ProviderCatalogOption(
                    provider.Descriptor.ProviderId,
                    provider.Descriptor.DisplayName,
                    provider.Descriptor.PackageId))
                .ToArray(),
            async (providerId, cancellationToken) =>
            {
                var modelsTask = service.ListEmbeddingModelsAsync(providerId, cancellationToken);
                var readinessTask = service.GetEmbeddingProviderReadinessAsync(providerId, cancellationToken);
                await Task.WhenAll(modelsTask, readinessTask);
                var readiness = await readinessTask;
                return new ProviderModelCatalogResult(
                    (await modelsTask).Select(ProviderModelCatalogAdapter.ToCatalogOption).ToArray(),
                    readiness is null
                        ? "No embedding provider selected."
                        : $"Embedding provider status: {readiness.Status} - {readiness.Message}",
                    readiness is null || readiness.Status == AgentProviderReadinessStatus.Ready
                        ? null
                        : readiness.Message);
            });
}
