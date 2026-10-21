using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Tests;

internal sealed class RegressionTestEmbeddingProvider(IAgentEmbeddingProvider provider) : AgentServices.IEmbeddingProviderService
{
    public ValueTask<AgentEmbeddingProviderDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(provider.Descriptor); }
    public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
        => provider.GetAvailableModelsAsync(cancellationToken);
    public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
        => provider.GetReadinessAsync(cancellationToken);
    public ValueTask<string> SpaceIdentityAsync(string modelId, CancellationToken cancellationToken = default)
        => provider is IAgentEmbeddingSpaceIdentityProvider identity ? identity.GetEmbeddingSpaceIdentityAsync(modelId, cancellationToken) : ValueTask.FromResult(string.Empty);
    public ValueTask<AgentEmbeddingGenerationResult?> GenerateAsync(string modelId, string text, CancellationToken cancellationToken = default)
        => provider.GenerateEmbeddingAsync(modelId, text, cancellationToken);
    public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateBatchAsync(string modelId, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        => provider.GenerateEmbeddingsAsync(modelId, texts, cancellationToken);
}

internal sealed class RegressionTestPermissionProvider(IAgentPermissionSurface surface) : AgentServices.IPermissionSurfaceService
{
    public ValueTask<AgentPermissionSurfaceDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentPermissionSurfaceDescriptor(surface.SurfaceId, surface.DisplayName, surface.ListActions()));
    }
}

internal sealed class RegressionTestSystemPromptProvider(IAgentSystemPromptContributor contributor) : AgentServices.ISystemPromptContributorService
{
    public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentServiceIdentity(contributor.ContributorId, contributor.DisplayName)); }
    public ValueTask<IReadOnlyList<AgentSystemPromptBlock>> ContributeAsync(AgentSystemPromptRequest request, CancellationToken cancellationToken = default)
        => contributor.ContributeAsync(request, cancellationToken);
}
