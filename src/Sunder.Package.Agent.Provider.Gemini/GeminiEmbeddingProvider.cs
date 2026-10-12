using Google.GenAI;
using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Provider.Gemini;

[SunderServiceProvider(typeof(IEmbeddingProviderService), ProviderId = "gemini.embedding")]
public sealed partial class GeminiEmbeddingProvider : IEmbeddingProviderService
{
    private readonly ProviderCredentialAccessor _credentials;
    private readonly Func<string, Client> _clientFactory;

    private static readonly IReadOnlyList<AgentEmbeddingModelDescriptor> Models =
    [
        new() { ModelId = "gemini/text-embedding-004", DisplayName = "Text Embedding 004", Dimensions = 768, IsRecommended = true },
        new() { ModelId = "gemini/gemini-embedding-001", DisplayName = "Gemini Embedding 001", Dimensions = null, IsRecommended = false },
    ];

    public GeminiEmbeddingProvider(IPackageContext packageContext)
        : this(
            packageContext,
            new ProviderCredentialAccessor(packageContext.Secrets, GeminiProviderConfiguration.ApiKeySecretKey))
    {
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal GeminiEmbeddingProvider(IPackageContext packageContext, ProviderSettings settings)
        : this(packageContext, settings.Credentials) { }

    internal GeminiEmbeddingProvider(IPackageContext packageContext, ProviderCredentialAccessor credentials,
        Func<string, Client>? clientFactory = null)
    {
        _credentials = credentials;
        _clientFactory = clientFactory ?? (apiKey => new Client(apiKey: apiKey));
        Descriptor = new AgentEmbeddingProviderDescriptor
        {
            ProviderId = "gemini",
            DisplayName = "Google Gemini",
            SupportedAuthModes = [AgentAuthMode.ApiKey],
            PackageId = packageContext.PackageId,
        };
    }

    public AgentEmbeddingProviderDescriptor Descriptor { get; }

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
        return ValueTask.FromResult("https://generativelanguage.googleapis.com");
    }

    public async ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = await _credentials.HasCredentialAsync(cancellationToken);
        return new()
        {
            ProviderId = Descriptor.ProviderId,
            Status = configured ? AgentProviderReadinessStatus.Ready : AgentProviderReadinessStatus.NeedsConfiguration,
            Message = configured
                ? "Gemini embeddings are ready via API key."
                : "A Gemini API key is required for embeddings. Open Settings -> Packages -> Sunder Agent Provider Gemini and enter an API key.",
        };
    }

    public async ValueTask<AgentEmbeddingGenerationResult?> GenerateAsync(
        CSunder_Agent_Embedding_Provider_EmbeddingProviderGenerateRequest request,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await _credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("A Gemini API key is required for embeddings.");
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return null;
        }

        var results = await GenerateBatchAsync(new() { ModelId = request.ModelId, Texts = [request.Text] }, cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    public async ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateBatchAsync(
        CSunder_Agent_Embedding_Provider_EmbeddingProviderGenerateBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        var apiKey = await _credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("A Gemini API key is required for embeddings.");
        }

        var results = ProviderEmbeddingBatch.CreateResultBuffer<AgentEmbeddingGenerationResult>(request.Texts, out var validTexts);
        if (validTexts.Count == 0)
        {
            return results;
        }

        using var client = _clientFactory(apiKey);
        for (var index = 0; index < validTexts.Count; index++)
        {
            var input = validTexts[index];
            var response = await client.Models.EmbedContentAsync(
                model: ProviderModelId.RemovePrefix(request.ModelId, "gemini"),
                contents: input.Text,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var embedding = response.Embeddings?.FirstOrDefault();
            results[input.Index] = embedding?.Values is null
                ? null
                : new AgentEmbeddingGenerationResult
                {
                    ModelId = request.ModelId,
                    Values = embedding.Values.Select(static value => (double)value).ToArray(),
                };
        }

        return results;
    }

}
