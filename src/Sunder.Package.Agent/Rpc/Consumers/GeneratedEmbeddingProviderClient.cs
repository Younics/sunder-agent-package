using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Embedding_Provider;
using Wire = Sunder.Generated.Rpc.EmbeddingProvider;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedEmbeddingProviderClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentEmbeddingProvider, IAgentEmbeddingSpaceIdentityProvider, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.embedding.provider-stale",
        "The selected embedding provider activation is no longer current.");
    private AgentEmbeddingProviderDescriptor? _descriptor;

    public AgentEmbeddingProviderDescriptor Descriptor
    {
        get
        {
            _calls.ThrowIfStopped();
            return _descriptor ??= Invoke(async (client, token) =>
            {
                var value = await client.DescribeAsync(new(), token).ConfigureAwait(false);
                return new AgentEmbeddingProviderDescriptor(value.ProviderId, value.DisplayName,
                    value.SupportedAuthModes.Select(mode => mode switch
                    {
                        Wire.AgentAuthMode.ApiKey => AgentAuthMode.ApiKey,
                        Wire.AgentAuthMode.CodexConnected => AgentAuthMode.CodexConnected,
                        _ => throw new InvalidDataException("Unknown embedding authentication mode."),
                    }).ToArray()) { PackageId = value.PackageId };
            }, default).AsTask().GetAwaiter().GetResult();
        }
    }

    public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        => Invoke<IReadOnlyList<AgentEmbeddingModelDescriptor>>(async (client, token) =>
            (await client.ListModelsAsync(new(), token).ConfigureAwait(false))
                .Select(value => new AgentEmbeddingModelDescriptor(value.ModelId, value.DisplayName, checked((int?)value.Dimensions), value.IsRecommended)).ToArray(), cancellationToken);

    public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
        => Invoke(async (client, token) =>
        {
            var value = await client.GetReadinessAsync(new(), token).ConfigureAwait(false);
            return new AgentEmbeddingProviderReadiness(value.ProviderId, value.Status switch
            {
                Wire.AgentProviderReadinessStatus.Ready => AgentProviderReadinessStatus.Ready,
                Wire.AgentProviderReadinessStatus.NeedsConfiguration => AgentProviderReadinessStatus.NeedsConfiguration,
                Wire.AgentProviderReadinessStatus.Failed => AgentProviderReadinessStatus.Failed,
                _ => throw new InvalidDataException("Unknown embedding readiness state."),
            }, value.Message);
        }, cancellationToken);

    public ValueTask<string> GetEmbeddingSpaceIdentityAsync(string modelId, CancellationToken cancellationToken = default)
        => Invoke((client, token) => client.SpaceIdentityAsync(new() { Value = modelId }, token), cancellationToken);

    public ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(string modelId, string text, CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => Map(await client.GenerateAsync(new() { ModelId = modelId, Text = text }, token).ConfigureAwait(false)), cancellationToken);

    public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(string modelId, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        => Invoke<IReadOnlyList<AgentEmbeddingGenerationResult?>>(async (client, token) =>
            (await client.GenerateBatchAsync(new() { ModelId = modelId, Texts = texts }, token).ConfigureAwait(false)).Select(Map).ToArray(), cancellationToken);

    private ValueTask<T> Invoke<T>(Func<Wire.EmbeddingProviderClient, CancellationToken, ValueTask<T>> invoke, CancellationToken token)
        => _calls.Invoke((scope, cancellation) => invoke(new PackageApi(scope).EmbeddingProvider
            .CreateClient(scope, new Api.Provider(provider)).SEmbedding_2dprovider, cancellation), token);

    private static AgentEmbeddingGenerationResult? Map(Wire.AgentEmbeddingGenerationResult? value)
        => value is null ? null : new(value.ModelId, value.Values.ToArray());

    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
