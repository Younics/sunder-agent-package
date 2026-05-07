using OpenAI.Embeddings;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.OpenAI;

public sealed class OpenAiEmbeddingProvider(
    ApiKeyAuthStrategy apiKeyAuthStrategy) : IAgentEmbeddingProvider
{
    private static readonly IReadOnlyList<AgentEmbeddingModelDescriptor> Models =
    [
        new("openai/text-embedding-3-small", "Text Embedding 3 Small", Dimensions: 1536, IsRecommended: true),
        new("openai/text-embedding-3-large", "Text Embedding 3 Large", Dimensions: 3072)
    ];

    public AgentEmbeddingProviderDescriptor Descriptor { get; } = new(
        "openai",
        "OpenAI",
        [AgentAuthMode.ApiKey]);

    public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Models);
    }

    public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(string.IsNullOrWhiteSpace(apiKeyAuthStrategy.GetApiKey())
            ? new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.NeedsConfiguration,
                "An OpenAI API key is required for embeddings. You may keep ChatGPT Plus/Pro authorization enabled for chat while also storing an API key for embeddings.")
            : new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "OpenAI embeddings are ready via stored API key."));
    }

    public async ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
        string modelId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var results = await GenerateEmbeddingsAsync(modelId, [text], cancellationToken);
        return results.Count == 0 ? null : results[0];
    }

    public async ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
        string modelId,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var apiKey = apiKeyAuthStrategy.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("An OpenAI API key is required for embeddings.");
        }

        var results = ProviderEmbeddingBatch.CreateResultBuffer(texts, out var validTexts);
        if (validTexts.Count == 0)
        {
            return results;
        }

        var client = new EmbeddingClient(NormalizeModelId(modelId), apiKey);
        var response = await client.GenerateEmbeddingsAsync(
            validTexts.Select(item => item.Text).ToArray(),
            options: null,
            cancellationToken);
        var embeddings = response.Value
            .OrderBy(embedding => embedding.Index)
            .Select(embedding => new AgentEmbeddingGenerationResult(modelId, embedding.ToFloats().ToArray()))
            .ToArray();

        ProviderEmbeddingBatch.ApplyOrderedResults(results, validTexts, embeddings);
        return results;
    }

    private static string NormalizeModelId(string modelId)
    {
        const string prefix = "openai/";
        return modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[prefix.Length..]
            : modelId;
    }
}
