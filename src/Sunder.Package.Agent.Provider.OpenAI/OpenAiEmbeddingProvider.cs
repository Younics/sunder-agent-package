using OpenAI.Embeddings;
using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Provider.OpenAI;

[method: Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
[SunderServiceProvider(typeof(IEmbeddingProviderService), ProviderId = "openai.embedding")]
public sealed partial class OpenAiEmbeddingProvider(
    ApiKeyAuthStrategy apiKeyAuthStrategy,
    IPackageContext packageContext) : IEmbeddingProviderService
{
    private readonly Func<string, string, EmbeddingClient> _clientFactory = static (model, key) => new EmbeddingClient(model, key);

    internal OpenAiEmbeddingProvider(ApiKeyAuthStrategy apiKeyAuthStrategy, IPackageContext packageContext,
        Func<string, string, EmbeddingClient> clientFactory) : this(apiKeyAuthStrategy, packageContext)
        => _clientFactory = clientFactory;

    private static readonly IReadOnlyList<AgentEmbeddingModelDescriptor> Models =
    [
        new() { ModelId = "openai/text-embedding-3-small", DisplayName = "Text Embedding 3 Small", Dimensions = 1536, IsRecommended = true },
        new() { ModelId = "openai/text-embedding-3-large", DisplayName = "Text Embedding 3 Large", Dimensions = 3072, IsRecommended = false }
    ];

    public AgentEmbeddingProviderDescriptor Descriptor { get; } = new()
    {
        ProviderId = "openai",
        DisplayName = "OpenAI",
        SupportedAuthModes = [AgentAuthMode.ApiKey],
        PackageId = packageContext.PackageId,
    };

    public ValueTask<AgentEmbeddingProviderDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Descriptor);
    }

    public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Models);
    }

    public ValueTask<string> SpaceIdentityAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult("https://api.openai.com/v1");
    }

    public async ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configured = !string.IsNullOrWhiteSpace(await apiKeyAuthStrategy.GetApiKeyAsync(cancellationToken));
        return new()
        {
            ProviderId = Descriptor.ProviderId,
            Status = configured ? AgentProviderReadinessStatus.Ready : AgentProviderReadinessStatus.NeedsConfiguration,
            Message = configured
                ? "OpenAI embeddings are ready via stored API key."
                : "An OpenAI API key is required for embeddings. You may keep ChatGPT Plus/Pro authorization enabled for chat while also storing an API key for embeddings.",
        };
    }

    public async ValueTask<AgentEmbeddingGenerationResult?> GenerateAsync(
        CSunder_Agent_Embedding_Provider_EmbeddingProviderGenerateRequest request,
        CancellationToken cancellationToken = default)
    {
        var results = await GenerateBatchAsync(new() { ModelId = request.ModelId, Texts = [request.Text] }, cancellationToken);
        return results.Count == 0 ? null : results[0];
    }

    public async ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateBatchAsync(
        CSunder_Agent_Embedding_Provider_EmbeddingProviderGenerateBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await apiKeyAuthStrategy.GetApiKeyAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("An OpenAI API key is required for embeddings.");
        }

        var results = ProviderEmbeddingBatch.CreateResultBuffer<AgentEmbeddingGenerationResult>(request.Texts, out var validTexts);
        if (validTexts.Count == 0)
        {
            return results;
        }

        var client = _clientFactory(ProviderModelId.RemovePrefix(request.ModelId, "openai"), apiKey);
        var response = await client.GenerateEmbeddingsAsync(
            validTexts.Select(item => item.Text).ToArray(),
            options: null,
            cancellationToken);
        var embeddings = response.Value
            .Select(embedding => new ProviderIndexedEmbedding<AgentEmbeddingGenerationResult>(
                embedding.Index,
                new AgentEmbeddingGenerationResult
                {
                    ModelId = request.ModelId,
                    Values = embedding.ToFloats().ToArray().Select(static value => (double)value).ToArray(),
                }))
            .ToArray();

        ProviderEmbeddingBatch.ApplyIndexedResults(results, validTexts, embeddings);
        return results;
    }
}
