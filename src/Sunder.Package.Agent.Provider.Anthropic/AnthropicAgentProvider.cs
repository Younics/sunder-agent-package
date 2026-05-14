using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Anthropic;

public sealed class AnthropicAgentProvider(IPackageContext packageContext) : IAgentChatProvider
{
    private static readonly IReadOnlyList<AgentModelDescriptor> Models =
    [
        new("anthropic/claude-opus-4-7", "Claude Opus 4.7", 1000000, 128000),
        new("anthropic/claude-sonnet-4-6", "Claude Sonnet 4.6", 1000000, 64000, IsRecommended: true),
        new("anthropic/claude-haiku-4-5", "Claude Haiku 4.5", 200000, 64000),
    ];

    public AgentProviderDescriptor Descriptor { get; } = new(
        "anthropic",
        "Anthropic",
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
        return ValueTask.FromResult(Models);
    }

    public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(string.IsNullOrWhiteSpace(GetApiKey())
            ? new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.NeedsConfiguration,
                "An Anthropic API key is required. Open Settings -> Packages -> Sunder Agent Provider Anthropic and enter an API key.")
            : new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Anthropic API-key mode is configured."));
    }

    public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentProviderRunCapabilities(
            SupportsNativeToolCalling: true,
            SupportsStreamingToolCalls: false,
            SupportsMultipleToolCalls: false,
            Summary: "Anthropic supports native tool use with one tool call per turn in Sunder.",
            SupportsImageInput: true,
            SupportsPdfInput: true));
    }

    public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IChatClient>(new AnthropicChatClient(context, GetApiKey));
    }

    private string? GetApiKey() => packageContext.Secrets.GetSecret("auth.apiKey");
}
