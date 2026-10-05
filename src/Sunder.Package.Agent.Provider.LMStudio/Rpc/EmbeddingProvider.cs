using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Embedding_Provider;

namespace Sunder.Package.Agent.Provider.LMStudio.Rpc;

[SunderRpcProvider(typeof(IEmbeddingService), ProviderId = "lmstudio.embedding")]
internal sealed partial class EmbeddingProvider(IAgentEmbeddingProvider provider) : IEmbeddingService
{
    public ValueTask<Wire.AgentEmbeddingProviderDescriptor> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(LMStudioProviderValues.Convert<Wire.AgentEmbeddingProviderDescriptor>(provider.Descriptor)); }
    public async ValueTask<IReadOnlyList<Wire.AgentEmbeddingModelDescriptor>> ListModelsAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
        => LMStudioProviderValues.Convert<Wire.AgentEmbeddingModelDescriptor[]>(await provider.GetAvailableModelsAsync(token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentEmbeddingProviderReadiness> GetReadinessAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
        => LMStudioProviderValues.Convert<Wire.AgentEmbeddingProviderReadiness>(await provider.GetReadinessAsync(token).ConfigureAwait(false));
    public async ValueTask<string> SpaceIdentityAsync(SunderRpcInvocationContext context, Wire.StringRequest request, CancellationToken token)
        => provider is IAgentEmbeddingSpaceIdentityProvider identity
            ? await identity.GetEmbeddingSpaceIdentityAsync(request.Value, token).ConfigureAwait(false) : string.Empty;
    public async ValueTask<Wire.AgentEmbeddingGenerationResult?> GenerateAsync(SunderRpcInvocationContext context, Wire.EmbeddingRequest request, CancellationToken token)
        => LMStudioProviderValues.Optional<Wire.AgentEmbeddingGenerationResult>(await provider.GenerateEmbeddingAsync(request.ModelId, request.Text, token).ConfigureAwait(false));
    public async ValueTask<IReadOnlyList<Wire.AgentEmbeddingGenerationResult?>> GenerateBatchAsync(SunderRpcInvocationContext context, Wire.EmbeddingBatchRequest request, CancellationToken token)
        => (await provider.GenerateEmbeddingsAsync(request.ModelId, request.Texts, token).ConfigureAwait(false))
            .Select(LMStudioProviderValues.Optional<Wire.AgentEmbeddingGenerationResult>).ToArray();
}
