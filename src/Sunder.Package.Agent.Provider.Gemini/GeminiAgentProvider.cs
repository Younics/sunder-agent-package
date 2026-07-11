using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Gemini;

public sealed class GeminiAgentProvider : IAgentChatProvider, IAgentUtilityModelProvider
{
    private readonly IPackageContext _packageContext;
    private readonly ProviderCredentialAccessor _credentials;

    public GeminiAgentProvider(IPackageContext packageContext)
        : this(
            packageContext,
            new ProviderCredentialAccessor(packageContext.Secrets, GeminiProviderConfiguration.ApiKeySecretKey))
    {
    }

    internal GeminiAgentProvider(IPackageContext packageContext, ProviderCredentialAccessor credentials)
    {
        _packageContext = packageContext;
        _credentials = credentials;
        Descriptor = new AgentProviderDescriptor(
            "gemini",
            "Google Gemini",
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
        return ValueTask.FromResult(GeminiModelCatalog.Models);
    }

    public ValueTask<string?> ResolveUtilityModelIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(UtilityModelSettingsState.ResolveModelId(
            _packageContext.Configuration.GetValue(GeminiProviderConfiguration.UtilityModelKey),
            GeminiProviderConfiguration.DefaultUtilityModelId,
            GeminiModelCatalog.UtilityModelOptions.Select(option => option.Value)));
    }

    public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(!_credentials.HasCredential
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
        return ValueTask.FromResult<IChatClient>(new GeminiChatClient(context, _credentials));
    }
}
