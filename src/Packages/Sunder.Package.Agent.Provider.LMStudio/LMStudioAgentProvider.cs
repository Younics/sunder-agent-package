using System.Net.Http.Headers;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using AIChatClient = Microsoft.Extensions.AI.IChatClient;

namespace Sunder.Package.Agent.Provider.LMStudio;

public sealed class LMStudioAgentProvider(IPackageContext packageContext) : IAgentChatProvider
{
    public AgentProviderDescriptor Descriptor { get; } = new(
        "lmstudio",
        "LM Studio",
        [],
        SupportsStreaming: true,
        SupportsInterruptibleRuns: true
    );

    public async ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
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
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);

            return document.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array
                ? dataElement.EnumerateArray()
                    .Where(item => item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetProperty("id").GetString()!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .Select(id => new AgentModelDescriptor($"lmstudio/{id}", id, 131072, 8192))
                    .ToArray()
                : [];
        }
        catch
        {
            return [];
        }
    }

    public async ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var baseUrl = GetBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.NeedsConfiguration,
                "An LM Studio base URL is required. Open Settings -> Packages -> Sunder Agent Provider LM Studio and enter one.");
        }

        try
        {
            using var httpClient = CreateHttpClient(baseUrl);
            using var response = await httpClient.GetAsync("models", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new AgentProviderReadiness(
                    Descriptor.ProviderId,
                    AgentProviderReadinessStatus.Ready,
                    "LM Studio is reachable and ready.")
                : new AgentProviderReadiness(
                    Descriptor.ProviderId,
                    AgentProviderReadinessStatus.Failed,
                    $"LM Studio returned {(int)response.StatusCode} {response.ReasonPhrase} while loading models.");
        }
        catch (Exception ex)
        {
            return new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Failed,
                $"LM Studio is not reachable: {ex.Message}");
        }
    }

    public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentProviderRunCapabilities(
            SupportsNativeToolCalling: true,
            SupportsStreamingToolCalls: true,
            SupportsMultipleToolCalls: false,
            Summary: "LM Studio can expose OpenAI-compatible native tool calls when the loaded model supports them."));
    }

    public ValueTask<AIChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<AIChatClient>(new LMStudioChatClient(context, GetBaseUrl, GetApiKey));
    }

    private string? GetBaseUrl() => packageContext.Configuration.GetValue("connection.baseUrl")?.Trim().TrimEnd('/');

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

}
