using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Anthropic;

public sealed class AnthropicAgentProvider : IAgentChatProvider, IAgentUtilityModelProvider
{
    private readonly IPackageContext _packageContext;
    private readonly ProviderCredentialAccessor _credentials;

    public AnthropicAgentProvider(IPackageContext packageContext)
        : this(
            packageContext,
            new ProviderCredentialAccessor(packageContext.Secrets, AnthropicProviderConfiguration.ApiKeySecretKey))
    {
    }

    internal AnthropicAgentProvider(IPackageContext packageContext, ProviderCredentialAccessor credentials)
    {
        _packageContext = packageContext;
        _credentials = credentials;
        Descriptor = new AgentProviderDescriptor(
            "anthropic",
            "Anthropic",
            [AgentAuthMode.ApiKey],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true)
        {
            PackageId = packageContext.PackageId,
        };
    }

    public AgentProviderDescriptor Descriptor { get; }

    public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AnthropicModelCatalog.Models);
    }

    public async ValueTask<string?> ResolveUtilityModelIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return UtilityModelSettingsState.ResolveModelId(
            await _packageContext.Configuration.GetValueAsync(AnthropicProviderConfiguration.UtilityModelKey, cancellationToken),
            AnthropicProviderConfiguration.DefaultUtilityModelId,
            AnthropicModelCatalog.UtilityModelOptions.Select(option => option.Value));
    }

    public async ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return !await _credentials.HasCredentialAsync(cancellationToken)
            ? new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.NeedsConfiguration,
                "An Anthropic API key is required. Open Settings -> Packages -> Sunder Agent Provider Anthropic and enter an API key.")
            : new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Anthropic API-key mode is configured.");
    }

    public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentProviderRunCapabilities(
            SupportsNativeToolCalling: true,
            SupportsStreamingToolCalls: false,
            SupportsMultipleToolCalls: true,
            Summary: "Anthropic supports native tool use and Sunder can run parallel-safe tools concurrently.",
            SupportsImageInput: true,
            SupportsPdfInput: true));
    }

    public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IChatClient>(new AnthropicChatClient(context, _credentials));
    }
}
