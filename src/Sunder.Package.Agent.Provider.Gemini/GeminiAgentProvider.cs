using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Gemini;

public sealed class GeminiAgentProvider(IPackageContext packageContext) : IAgentChatProvider, IAgentUtilityModelProvider
{
    public AgentProviderDescriptor Descriptor { get; } = new(
        "gemini",
        "Google Gemini",
        [AgentAuthMode.ApiKey],
        SupportsStreaming: true,
        SupportsInterruptibleRuns: true
    )
    {
        PackageId = packageContext.PackageId
    };

    public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GeminiModelCatalog.Models);
    }

    public ValueTask<string?> ResolveUtilityModelIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuredModelId = packageContext.Configuration.GetValue(GeminiProviderConfiguration.UtilityModelKey);
        return ValueTask.FromResult<string?>(IsKnownModel(configuredModelId)
            ? configuredModelId!.Trim()
            : GeminiProviderConfiguration.DefaultUtilityModelId);
    }

    public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(string.IsNullOrWhiteSpace(GetApiKey())
            ? new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.NeedsConfiguration,
                "A Gemini API key is required. Open Settings -> Packages -> Sunder Agent Provider Gemini and enter an API key.")
            : new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Gemini API-key mode is configured."));
    }

    public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentProviderRunCapabilities(
            SupportsNativeToolCalling: true,
            SupportsStreamingToolCalls: false,
            SupportsMultipleToolCalls: true,
            Summary: "Gemini supports native function calling and Sunder can run parallel-safe tools concurrently.",
            SupportsImageInput: true,
            SupportsPdfInput: true,
            SupportsAudioInput: true,
            SupportsVideoInput: true));
    }

    public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IChatClient>(new GeminiChatClient(context, GetApiKey));
    }

    private string? GetApiKey() => packageContext.Secrets.GetSecret("auth.apiKey");

    private static bool IsKnownModel(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId)
        && GeminiModelCatalog.Models.Any(model => string.Equals(model.ModelId, modelId.Trim(), StringComparison.OrdinalIgnoreCase));
}
