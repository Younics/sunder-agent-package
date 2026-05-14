using Google.GenAI;
using Google.GenAI.Types;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Gemini;

public sealed class GeminiEmbeddingProvider(IPackageContext packageContext) : IAgentEmbeddingProvider
{
    private static readonly IReadOnlyList<AgentEmbeddingModelDescriptor> Models =
    [
        new("gemini/text-embedding-004", "Text Embedding 004", Dimensions: 768, IsRecommended: true),
        new("gemini/gemini-embedding-001", "Gemini Embedding 001"),
    ];

    public AgentEmbeddingProviderDescriptor Descriptor { get; } = new(
        "gemini",
        "Google Gemini",
        [AgentAuthMode.ApiKey])
    {
        PackageId = packageContext.PackageId
    };

    public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Models);
    }

    public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(string.IsNullOrWhiteSpace(GetApiKey())
            ? new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.NeedsConfiguration,
                "A Gemini API key is required for embeddings. Open Settings -> Packages -> Sunder Agent Provider Gemini and enter an API key.")
            : new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Gemini embeddings are ready via API key."));
    }

    public async ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
        string modelId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("A Gemini API key is required for embeddings.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var client = new Client(apiKey: apiKey);
        var response = await client.Models.EmbedContentAsync(
            model: NormalizeModelId(modelId),
            contents: text,
            cancellationToken: cancellationToken);
        var embedding = response.Embeddings?.FirstOrDefault();
        return embedding?.Values is null
            ? null
            : new AgentEmbeddingGenerationResult(modelId, embedding.Values.Select(value => (float)value).ToArray());
    }

    public async ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
        string modelId,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var results = new AgentEmbeddingGenerationResult?[texts.Count];
        for (var index = 0; index < texts.Count; index++)
        {
            results[index] = await GenerateEmbeddingAsync(modelId, texts[index], cancellationToken);
        }

        return results;
    }

    private string? GetApiKey() => packageContext.Secrets.GetSecret("api.key");

    private static string NormalizeModelId(string modelId)
    {
        const string prefix = "gemini/";
        return modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[prefix.Length..]
            : modelId;
    }
}
