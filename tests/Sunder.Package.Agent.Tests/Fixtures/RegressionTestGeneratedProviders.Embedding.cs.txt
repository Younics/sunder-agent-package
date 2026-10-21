using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IEmbeddingProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Embedding_Provider.IEmbeddingProviderService;
using Sunder.Sdk.Services;

namespace RegressionTestGeneratedProviders;

[SunderServiceProvider(typeof(IEmbeddingProviderService), ProviderId = "fixture.regression.embedding.provider")]
public sealed partial class EmbeddingProvider(Func<string, Type, object?[], CancellationToken, object> invoke) : IEmbeddingProviderService
{
    private ValueTask<T> Reply<T>(string operation, object?[] arguments, CancellationToken token) => (ValueTask<T>)invoke(operation, typeof(T), arguments, token);
    public ValueTask<AgentEmbeddingProviderDescriptor> DescribeAsync(CancellationToken token = default) => Reply<AgentEmbeddingProviderDescriptor>(nameof(DescribeAsync), [], token);
    public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListModelsAsync(CancellationToken token = default) => Reply<IReadOnlyList<AgentEmbeddingModelDescriptor>>(nameof(ListModelsAsync), [], token);
    public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken token = default) => Reply<AgentEmbeddingProviderReadiness>(nameof(GetReadinessAsync), [], token);
    public ValueTask<string> SpaceIdentityAsync(string modelId, CancellationToken token = default) => Reply<string>(nameof(SpaceIdentityAsync), [modelId], token);
    public ValueTask<AgentEmbeddingGenerationResult?> GenerateAsync(CYounics_Ai_Agent_Embedding_Provider_EmbeddingProviderGenerateRequest request, CancellationToken token = default) => Reply<AgentEmbeddingGenerationResult?>(nameof(GenerateAsync), [request], token);
    public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateBatchAsync(CYounics_Ai_Agent_Embedding_Provider_EmbeddingProviderGenerateBatchRequest request, CancellationToken token = default) => Reply<IReadOnlyList<AgentEmbeddingGenerationResult?>>(nameof(GenerateBatchAsync), [request], token);
}
