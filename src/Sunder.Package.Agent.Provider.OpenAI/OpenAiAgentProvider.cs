using Microsoft.Extensions.AI;
using OpenAI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Sunder.Sdk.Abstractions;

#pragma warning disable OPENAI001

namespace Sunder.Package.Agent.Provider.OpenAI;

public sealed class OpenAiAgentProvider(
    ApiKeyAuthStrategy apiKeyAuthStrategy,
    CodexConnectedAuthStrategy codexConnectedAuthStrategy,
    CodexConnectedTransport codexConnectedTransport,
    IPackageContext packageContext) : IAgentChatProvider
{
    private readonly IPackageContext _packageContext = packageContext;

    private static readonly IReadOnlyList<AgentModelVariantDescriptor> ReasoningVariants =
    [
        new("none", "None", "Disable reasoning effort when the selected model supports it.", AgentReasoningEffort.None),
        new("low", "Low", "Use low reasoning effort for faster responses.", AgentReasoningEffort.Low),
        new("medium", "Medium", "Use balanced reasoning effort.", AgentReasoningEffort.Medium),
        new("high", "High", "Use high reasoning effort for complex tasks.", AgentReasoningEffort.High),
        new("xhigh", "Xhigh", "Use extra-high reasoning effort for the hardest tasks.", AgentReasoningEffort.ExtraHigh),
    ];

    private static readonly IReadOnlyList<AgentModelDescriptor> Models =
    [
        new("openai/gpt-5.5-fast", "GPT-5.5 Fast", 400000, 128000, IsRecommended: true, Variants: ReasoningVariants),
        new("openai/gpt-5.5", "GPT-5.5", 400000, 128000, IsRecommended: true, Variants: ReasoningVariants),
        new("openai/gpt-5.4", "GPT-5.4", 1050000, 128000, IsRecommended: true, Variants: ReasoningVariants),
        new("openai/gpt-5.4-mini", "GPT-5.4 Mini", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.3-codex", "GPT-5.3 Codex", 400000, 128000, IsRecommended: true, Variants: ReasoningVariants),
        new("openai/gpt-5.3-codex-spark", "GPT-5.3 Codex Spark", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.2", "GPT-5.2", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.2-codex", "GPT-5.2 Codex", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.1-codex", "GPT-5.1 Codex", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.1-codex-max", "GPT-5.1 Codex Max", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5.1-codex-mini", "GPT-5.1 Codex Mini", 400000, 128000, Variants: ReasoningVariants),
        new("openai/gpt-5-codex", "GPT-5 Codex", 400000, 128000, Variants: ReasoningVariants),
        new("openai/codex-mini-latest", "Codex Mini Latest", 200000, 100000, Variants: ReasoningVariants),
    ];

    public AgentProviderDescriptor Descriptor { get; } = new(
        "openai",
        "OpenAI",
        [AgentAuthMode.ApiKey, AgentAuthMode.CodexConnected],
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

    public async ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var authMode = OpenAiAuthMode.GetSelected(_packageContext.Configuration);
        if (authMode == OpenAiAuthMode.ApiKey)
        {
            return !string.IsNullOrWhiteSpace(apiKeyAuthStrategy.GetApiKey())
                ? new AgentProviderReadiness(
                    Descriptor.ProviderId,
                    AgentProviderReadinessStatus.Ready,
                    "API-key mode is active and an OpenAI API key is stored.")
                : new AgentProviderReadiness(
                    Descriptor.ProviderId,
                    AgentProviderReadinessStatus.NeedsConfiguration,
                    "API-key mode is active, but no OpenAI API key is stored. Open Settings -> Packages -> Sunder Agent Provider OpenAI and store an API key.");
        }

        var session = await codexConnectedAuthStrategy.TryEnsureAuthenticatedSilentlyAsync(cancellationToken);
        return session is not null
            ? new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                $"Codex-connected mode is active and authenticated until {session.ExpiresAtUtc:O}.")
            : new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.NeedsConfiguration,
                "Codex-connected mode is active, but ChatGPT Plus/Pro is not authorized. Open Settings -> Packages -> Sunder Agent Provider OpenAI and authorize.");
    }

    public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentProviderRunCapabilities(
            SupportsNativeToolCalling: true,
            SupportsStreamingToolCalls: true,
            SupportsMultipleToolCalls: false,
            Summary: "OpenAI chat uses the selected auth mode: API-key mode uses the official OpenAI Responses connector, and ChatGPT Plus/Pro mode uses the Codex-connected transport.",
            SupportsImageInput: true,
            SupportsPdfInput: true));
    }

    public async ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authMode = OpenAiAuthMode.GetSelected(_packageContext.Configuration);

        if (authMode == OpenAiAuthMode.ApiKey)
        {
            var apiKey = apiKeyAuthStrategy.GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new AgentChatProviderException(
                    "openai-auth-required",
                    "### OpenAI API key required\n\nAPI-key mode is active. Open **Settings -> Packages -> Sunder Agent Provider OpenAI** and store an API key.",
                    "openai-auth-required");
            }

            await context.LogProviderEventAsync(
                AgentLogLevel.Information,
                "openai.auth.mode.selected",
                "OpenAI auth mode selected.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["auth.mode"] = OpenAiAuthMode.ApiKey,
                },
                cancellationToken: cancellationToken);
            await context.LogProviderEventAsync(
                AgentLogLevel.Information,
                "openai.transport.selected",
                "OpenAI transport selected.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["transport.id"] = "openai-responses-api",
                },
                cancellationToken: cancellationToken);
            var model = OpenAiModelIds.Normalize(context.ModelId);
            return new OpenAIClient(apiKey).GetResponsesClient().AsIChatClient(model);
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
            AgentLogLevel.Information,
            "openai.auth.mode.selected",
            "OpenAI auth mode selected.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["auth.mode"] = OpenAiAuthMode.CodexConnected,
            },
            cancellationToken: cancellationToken);
        await context.LogProviderEventAsync(
            AgentLogLevel.Information,
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
            session);
    }
}
