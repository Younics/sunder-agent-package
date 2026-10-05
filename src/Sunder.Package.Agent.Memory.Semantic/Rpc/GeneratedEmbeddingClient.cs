using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Embedding_Provider;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Embedding_Provider;

namespace Sunder.Package.Agent.Memory.Semantic.Rpc;

internal sealed class GeneratedEmbeddingClient(ISunderRpcClient rpc, Api.Contract contract, Api.Provider provider, CancellationToken retirement)
    : IAgentEmbeddingProvider, IAgentEmbeddingSpaceIdentityProvider
{
    private AgentEmbeddingProviderDescriptor? _descriptor;
    public AgentEmbeddingProviderDescriptor Descriptor => _descriptor ??= Invoke(async (client, token) =>
        SemanticProviderValues.Convert<AgentEmbeddingProviderDescriptor>(await client.DescribeAsync(new(), token).ConfigureAwait(false)), default).AsTask().GetAwaiter().GetResult();

    public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        => Invoke<IReadOnlyList<AgentEmbeddingModelDescriptor>>(async (client, token) =>
            SemanticProviderValues.Convert<AgentEmbeddingModelDescriptor[]>(await client.ListModelsAsync(new(), token).ConfigureAwait(false)), cancellationToken);

    public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => SemanticProviderValues.Convert<AgentEmbeddingProviderReadiness>(
            await client.GetReadinessAsync(new(), token).ConfigureAwait(false)), cancellationToken);

    public ValueTask<string> GetEmbeddingSpaceIdentityAsync(string modelId, CancellationToken cancellationToken = default)
        => Invoke((client, token) => client.SpaceIdentityAsync(new() { Value = modelId }, token), cancellationToken);

    public ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(string modelId, string text, CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => Map(await client.GenerateAsync(new() { ModelId = modelId, Text = text }, token).ConfigureAwait(false)), cancellationToken);

    public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(string modelId, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        => Invoke<IReadOnlyList<AgentEmbeddingGenerationResult?>>(async (client, token) =>
            (await client.GenerateBatchAsync(new() { ModelId = modelId, Texts = texts }, token).ConfigureAwait(false)).Select(Map).ToArray(), cancellationToken);

    private async ValueTask<T> Invoke<T>(Func<Wire.EmbeddingProviderClient, CancellationToken, ValueTask<T>> callback, CancellationToken token)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, retirement);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: invocation.Token).ConfigureAwait(false);
        return await callback(contract.CreateClient(scope, provider).SEmbedding_2dprovider, invocation.Token).ConfigureAwait(false);
    }

    private static AgentEmbeddingGenerationResult? Map(Wire.AgentEmbeddingGenerationResult? value)
        => value is null ? null : SemanticProviderValues.Convert<AgentEmbeddingGenerationResult>(value);
}
