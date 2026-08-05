using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.PackageViews;

internal static class AgentProfileProviderModelCatalog
{
    public static IProviderModelCatalog CreateChat(IAgentProfileGateway service)
        => new ProviderModelCatalogAdapter(
            async cancellationToken =>
            {
                var providers = service is IAgentCatalogLoader loader
                    ? (await loader.LoadCatalogAsync(new AgentCatalogRequest(), cancellationToken)
                        .ConfigureAwait(false)).ChatProviders
                    : service.ListChatProviderDescriptors();
                return providers
                    .Select(provider => new ProviderCatalogOption(
                        provider.ProviderId,
                        provider.DisplayName,
                        provider.PackageId))
                    .ToArray();
            },
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

    public static IProviderModelCatalog CreateEmbedding(IAgentProfileGateway service)
        => new ProviderModelCatalogAdapter(
            async cancellationToken =>
            {
                var providers = service is IAgentCatalogLoader loader
                    ? (await loader.LoadCatalogAsync(new AgentCatalogRequest(), cancellationToken)
                        .ConfigureAwait(false)).EmbeddingProviders
                    : service.ListEmbeddingProviderDescriptors();
                return providers
                    .Select(provider => new ProviderCatalogOption(
                        provider.ProviderId,
                        provider.DisplayName,
                        provider.PackageId))
                    .ToArray();
            },
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
