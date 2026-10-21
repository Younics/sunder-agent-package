using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.Presentation;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.PackageViews;

internal static class AgentProfileProviderModelCatalog
{
    public static IProviderModelCatalog CreateChat(AgentServices.IAgentProfileService service)
        => new ProviderModelCatalogAdapter(
            async cancellationToken =>
            {
                var providers = await service.ListChatProviderDescriptorsAsync(cancellationToken).ConfigureAwait(false);
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

    public static IProviderModelCatalog CreateEmbedding(AgentServices.IAgentProfileService service)
        => new ProviderModelCatalogAdapter(
            async cancellationToken =>
            {
                var providers = await service.ListEmbeddingProviderDescriptorsAsync(cancellationToken).ConfigureAwait(false);
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
