using System.Net.Http.Headers;
using System.Text.Json;
using System.ClientModel;
using OpenAI;
using OpenAI.Embeddings;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.LMStudio;

public sealed class LMStudioEmbeddingProvider(IPackageContext packageContext) : IAgentEmbeddingProvider
{
    public AgentEmbeddingProviderDescriptor Descriptor { get; } = new(
        "lmstudio",
        "LM Studio",
        []);

    public async ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var baseUrl = GetBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return [];
        }

        try
        {
            using var httpClient = CreateHttpClient(baseUrl);
            using var response = await httpClient.GetAsync("models", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return document.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array
                ? dataElement.EnumerateArray()
                    .Where(item => item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetProperty("id").GetString()!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .Select(id => new AgentEmbeddingModelDescriptor($"lmstudio/{id}", id))
                    .ToArray()
                : [];
        }
        catch
        {
            return [];
        }
    }

    public async ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var baseUrl = GetBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.NeedsConfiguration,
                "An LM Studio base URL is required. Open Settings -> Packages -> Sunder Agent Provider LM Studio and enter one.");
        }

        try
        {
            using var httpClient = CreateHttpClient(baseUrl);
            using var response = await httpClient.GetAsync("models", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new AgentEmbeddingProviderReadiness(
                    Descriptor.ProviderId,
                    AgentProviderReadinessStatus.Ready,
                    "LM Studio is reachable and ready for embeddings.")
                : new AgentEmbeddingProviderReadiness(
                    Descriptor.ProviderId,
                    AgentProviderReadinessStatus.Failed,
                    $"LM Studio returned {(int)response.StatusCode} {response.ReasonPhrase} while loading models.");
        }
        catch (Exception ex)
        {
            return new AgentEmbeddingProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Failed,
                $"LM Studio is not reachable: {ex.Message}");
        }
    }

    public async ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
        string modelId,
        string text,
        CancellationToken cancellationToken = default)
    {
        var results = await GenerateEmbeddingsAsync(modelId, [text], cancellationToken).ConfigureAwait(false);
        return results.Count == 0 ? null : results[0];
    }

    public async ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
        string modelId,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = GetBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("An LM Studio base URL is required for embeddings.");
        }

        var results = ProviderEmbeddingBatch.CreateResultBuffer(texts, out var validTexts);
        if (validTexts.Count == 0)
        {
            return results;
        }

        var client = CreateEmbeddingClient(baseUrl, modelId);
        var response = await client.GenerateEmbeddingsAsync(
            validTexts.Select(item => item.Text).ToArray(),
            options: null,
            cancellationToken).ConfigureAwait(false);
        var embeddings = response.Value
            .OrderBy(embedding => embedding.Index)
            .Select(embedding => new AgentEmbeddingGenerationResult(modelId, embedding.ToFloats().ToArray()))
            .ToArray();

        ProviderEmbeddingBatch.ApplyOrderedResults(results, validTexts, embeddings);
        return results;
    }

    private string GetBaseUrl()
    {
        var configuredBaseUrl = packageContext.Configuration.GetValue("connection.baseUrl")?.Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? LMStudioProviderConfiguration.DefaultBaseUrl
            : configuredBaseUrl;
    }

    private string? GetApiKey() => packageContext.Secrets.GetSecret("connection.apiKey");

    private HttpClient CreateHttpClient(string baseUrl)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl + "/") };
        var apiKey = GetApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return httpClient;
    }

    private EmbeddingClient CreateEmbeddingClient(string baseUrl, string modelId)
    {
        var apiKey = GetApiKey();
        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "lm-studio" : apiKey);
        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
        return new EmbeddingClient(NormalizeModelId(modelId), credential, options);
    }

    private static string NormalizeModelId(string modelId)
    {
        const string prefix = "lmstudio/";
        return modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[prefix.Length..]
            : modelId;
    }
}
