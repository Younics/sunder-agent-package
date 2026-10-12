using Microsoft.Extensions.AI;
using OpenAI;
using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Services;
using Sunder.Package.Agent.Contracts.Models;

#pragma warning disable OPENAI001

namespace Sunder.Package.Agent.Provider.OpenAI;

[method: Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
[SunderServiceProvider(typeof(IChatProviderService), ProviderId = "openai.chat")]
public sealed partial class OpenAiAgentProvider(
    ApiKeyAuthStrategy apiKeyAuthStrategy,
    CodexConnectedAuthStrategy codexConnectedAuthStrategy,
    CodexConnectedTransport codexConnectedTransport,
    CodexResponseContinuationStore codexResponseContinuationStore,
    IPackageContext packageContext) : IChatProviderService
{
    private readonly IPackageContext _packageContext = packageContext;
    private readonly Func<string, OpenAIClient> _clientFactory = static key => new OpenAIClient(key);

    internal OpenAiAgentProvider(ApiKeyAuthStrategy apiKeyAuthStrategy, CodexConnectedAuthStrategy codexConnectedAuthStrategy,
        CodexConnectedTransport codexConnectedTransport, CodexResponseContinuationStore codexResponseContinuationStore,
        IPackageContext packageContext, Func<string, OpenAIClient> clientFactory)
        : this(apiKeyAuthStrategy, codexConnectedAuthStrategy, codexConnectedTransport, codexResponseContinuationStore, packageContext)
        => _clientFactory = clientFactory;

    public AgentProviderDescriptor Descriptor { get; } = new()
    {
        ProviderId = "openai",
        DisplayName = "OpenAI",
        SupportedAuthModes = [AgentAuthMode.ApiKey, AgentAuthMode.CodexConnected],
        SupportsStreaming = true,
        SupportsInterruptibleRuns = true,
        PackageId = packageContext.PackageId,
    };

    public ValueTask<AgentProviderDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Descriptor);
    }

    public async ValueTask<IReadOnlyList<AgentModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authMode = await OpenAiAuthMode.GetSelectedAsync(_packageContext.Settings, cancellationToken);
        return string.Equals(authMode, OpenAiAuthMode.CodexConnected, StringComparison.OrdinalIgnoreCase)
            ? OpenAiModelCatalog.CodexModels
            : OpenAiModelCatalog.Models;
    }

    public async ValueTask<string?> ResolveUtilityModelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await OpenAiProviderConfiguration.UtilityModelSelection
            .ResolveAsync(_packageContext.Settings, cancellationToken);
    }

    public async ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var authMode = await OpenAiAuthMode.GetSelectedAsync(_packageContext.Settings, cancellationToken);
        if (authMode == OpenAiAuthMode.ApiKey)
        {
            var configured = !string.IsNullOrWhiteSpace(await apiKeyAuthStrategy.GetApiKeyAsync(cancellationToken));
            return new()
            {
                ProviderId = Descriptor.ProviderId,
                Status = configured ? AgentProviderReadinessStatus.Ready : AgentProviderReadinessStatus.NeedsConfiguration,
                Message = configured
                    ? "API-key mode is active and an OpenAI API key is stored."
                    : "API-key mode is active, but no OpenAI API key is stored. Open Settings -> Packages -> Sunder Agent Provider OpenAI and store an API key.",
            };
        }

        var session = await codexConnectedAuthStrategy.TryEnsureAuthenticatedSilentlyAsync(cancellationToken);
        return new()
        {
            ProviderId = Descriptor.ProviderId,
            Status = session is not null ? AgentProviderReadinessStatus.Ready : AgentProviderReadinessStatus.NeedsConfiguration,
            Message = session is not null
                ? $"Codex-connected mode is active and authenticated until {session.ExpiresAtUtc:O}."
                : "Codex-connected mode is active, but ChatGPT Plus/Pro is not authorized. Open Settings -> Packages -> Sunder Agent Provider OpenAI and authorize.",
        };
    }

    public ValueTask<AgentProviderRunCapabilities> GetCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentProviderRunCapabilities
        {
            SupportsNativeToolCalling = true,
            SupportsStreamingToolCalls = true,
            SupportsMultipleToolCalls = true,
            Summary = "OpenAI chat uses the selected auth mode and Sunder can run parallel-safe tools concurrently.",
            SupportsImageInput = true,
            SupportsPdfInput = true,
            SupportsAudioInput = false,
            SupportsVideoInput = false,
            ContextWindowTokens = null,
            MaxOutputTokens = null,
        });
    }

    public IAsyncEnumerable<AgentChatStreamEvent> StreamChatAsync(AgentChatStreamRequest request, CancellationToken cancellationToken = default)
        => ProviderChatStream.ReadAsync(CreateChatClientAsync, request, cancellationToken);

    public async ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authMode = await OpenAiAuthMode.GetSelectedAsync(_packageContext.Settings, cancellationToken);

        if (authMode == OpenAiAuthMode.ApiKey)
        {
            var apiKey = await apiKeyAuthStrategy.GetApiKeyAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new AgentChatProviderException(
                    "openai-auth-required",
                    "### OpenAI API key required\n\nAPI-key mode is active. Open **Settings -> Packages -> Sunder Agent Provider OpenAI** and store an API key.",
                    "openai-auth-required");
            }

            await context.LogProviderEventAsync(
                PackageLogLevel.Information,
                "openai.auth.mode.selected",
                "OpenAI auth mode selected.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["auth.mode"] = OpenAiAuthMode.ApiKey,
                },
                cancellationToken: cancellationToken);
            await context.LogProviderEventAsync(
                PackageLogLevel.Information,
                "openai.transport.selected",
                "OpenAI transport selected.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["transport.id"] = "openai-responses-api",
                },
                cancellationToken: cancellationToken);
            var model = OpenAiModelIds.Normalize(context.ModelId);
            return new OpenAiModelOptionsChatClient(_clientFactory(apiKey).GetResponsesClient().AsIChatClient(model));
        }

        var session = await codexConnectedAuthStrategy.TryEnsureAuthenticatedSilentlyAsync(cancellationToken);
        if (session is null)
        {
            throw new AgentChatProviderException(
                "openai-codex-auth-required",
                "### Codex authorization required\n\nCodex-connected mode is active. Open **Settings -> Packages -> Sunder Agent Provider OpenAI**, click **Authorize**, and then retry.",
                "openai-codex-auth-required");
        }

        await context.LogProviderEventAsync(
            PackageLogLevel.Information,
            "openai.auth.mode.selected",
            "OpenAI auth mode selected.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["auth.mode"] = OpenAiAuthMode.CodexConnected,
            },
            cancellationToken: cancellationToken);
        await context.LogProviderEventAsync(
            PackageLogLevel.Information,
            "openai.transport.selected",
            "OpenAI transport selected.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["transport.id"] = codexConnectedTransport.TransportId,
            },
            cancellationToken: cancellationToken);
        return new OpenAiCodexChatClient(
            context,
            Descriptor.DisplayName,
            codexConnectedTransport,
            codexResponseContinuationStore,
            session);
    }

}
