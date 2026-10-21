using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderService(SunderServiceExposure.Public, ContractId = "younics.ai.agent.embedding.provider",
    ServiceId = "embedding-provider", IncludeSchemaDialect = true)]
[SunderRuntimeUse(Actions = ["discover", "invoke"])]
public interface EmbeddingProviderService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentEmbeddingProviderDescriptor> DescribeAsync(CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<string> SpaceIdentityAsync(string modelId, CancellationToken cancellationToken = default);

    ValueTask<AgentEmbeddingGenerationResult?> GenerateAsync(
        string modelId, string text, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateBatchAsync(
        string modelId, IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
