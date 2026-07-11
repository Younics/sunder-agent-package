using Google.GenAI;
using Google.GenAI.Types;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Gemini;

public sealed class GeminiEmbeddingProvider : IAgentEmbeddingProvider
{
    private readonly ProviderCredentialAccessor _credentials;

    private static readonly IReadOnlyList<AgentEmbeddingModelDescriptor> Models =
    [
        new("gemini/text-embedding-004", "Text Embedding 004", Dimensions: 768, IsRecommended: true),
        new("gemini/gemini-embedding-001", "Gemini Embedding 001"),
    ];

    public GeminiEmbeddingProvider(IPackageContext packageContext)
        : this(
            packageContext,
            new ProviderCredentialAccessor(packageContext.Secrets, GeminiProviderConfiguration.ApiKeySecretKey))
    {
    }

    internal GeminiEmbeddingProvider(IPackageContext packageContext, ProviderCredentialAccessor credentials)
    {
        _credentials = credentials;
        Descriptor = new AgentEmbeddingProviderDescriptor(
            "gemini",
            "Google Gemini",
            [AgentAuthMode.ApiKey])
        {
            PackageId = packageContext.PackageId,
        };
    }

    public AgentEmbeddingProviderDescriptor Descriptor { get; }

    public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Models);
    }

    public async ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return !await _credentials.HasCredentialAsync(cancellationToken)
            ? new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.NeedsConfiguration,
                "A Gemini API key is required for embeddings. Open Settings -> Packages -> Sunder Agent Provider Gemini and enter an API key.")
            : new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Gemini embeddings are ready via API key.");
    }

    public async ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
        string modelId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await _credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("A Gemini API key is required for embeddings.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var results = await GenerateEmbeddingsAsync(modelId, [text], cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    public async ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
        string modelId,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await _credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("A Gemini API key is required for embeddings.");
        }

        var results = ProviderEmbeddingBatch.CreateResultBuffer(texts, out var validTexts);
        if (validTexts.Count == 0)
        {
            return results;
        }

        using var client = new Client(apiKey: apiKey);
        for (var index = 0; index < validTexts.Count; index++)
        {
            var input = validTexts[index];
            var response = await client.Models.EmbedContentAsync(
                model: ProviderModelId.RemovePrefix(modelId, "gemini"),
                contents: input.Text,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var embedding = response.Embeddings?.FirstOrDefault();
            results[input.Index] = embedding?.Values is null
                ? null
                : new AgentEmbeddingGenerationResult(modelId, embedding.Values.Select(value => (float)value).ToArray());
        }

        return results;
    }

}
