using Microsoft.Extensions.AI;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IChatProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Chat_Provider.IChatProviderService;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;
using AgentChatClientContext = Sunder.Package.Agent.Contracts.Models.AgentChatClientContext;

namespace Sunder.Package.Agent.Provider.Gemini;

[SunderServiceProvider(typeof(IChatProviderService), ProviderId = "gemini.chat")]
public sealed partial class GeminiAgentProvider : IChatProviderService
{
    private readonly IPackageContext _packageContext;
    private readonly ProviderCredentialAccessor _credentials;
    private readonly Func<string, Google.GenAI.Client> _clientFactory;

    public GeminiAgentProvider(IPackageContext packageContext)
        : this(
            packageContext,
            new ProviderCredentialAccessor(packageContext.Secrets, GeminiProviderConfiguration.ApiKeySecretKey))
    {
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal GeminiAgentProvider(IPackageContext packageContext, ProviderSettings settings)
        : this(packageContext, settings.Credentials) { }

    internal GeminiAgentProvider(IPackageContext packageContext, ProviderCredentialAccessor credentials,
        Func<string, Google.GenAI.Client>? clientFactory = null)
    {
        _packageContext = packageContext;
        _credentials = credentials;
        _clientFactory = clientFactory ?? (apiKey => new Google.GenAI.Client(apiKey: apiKey));
        Descriptor = new AgentProviderDescriptor
        {
            ProviderId = "gemini",
            DisplayName = "Google Gemini",
            SupportedAuthModes = [AgentAuthMode.ApiKey],
            SupportsStreaming = true,
            SupportsInterruptibleRuns = true,
            PackageId = packageContext.PackageId,
        };
    }

    public AgentProviderDescriptor Descriptor { get; }

    public ValueTask<AgentProviderDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Descriptor);
    }

    public ValueTask<IReadOnlyList<AgentModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GeminiModelCatalog.Models);
    }

    public async ValueTask<string?> ResolveUtilityModelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await GeminiProviderConfiguration.UtilityModelSelection
            .ResolveAsync(_packageContext.Settings, cancellationToken);
    }

    public async ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = await _credentials.HasCredentialAsync(cancellationToken);
        return new()
        {
            ProviderId = Descriptor.ProviderId,
            Status = configured ? AgentProviderReadinessStatus.Ready : AgentProviderReadinessStatus.NeedsConfiguration,
            Message = configured
                ? "Gemini API-key mode is configured."
                : "A Gemini API key is required. Open Settings -> Packages -> AI Agent - Gemini and enter an API key.",
        };
    }

    public ValueTask<AgentProviderRunCapabilities> GetCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentProviderRunCapabilities
        {
            SupportsNativeToolCalling = true,
            SupportsStreamingToolCalls = false,
            SupportsMultipleToolCalls = true,
            Summary = "Gemini supports native function calling and Sunder can run parallel-safe tools concurrently.",
            SupportsImageInput = true,
            SupportsPdfInput = true,
            SupportsAudioInput = true,
            SupportsVideoInput = true,
            ContextWindowTokens = null,
            MaxOutputTokens = null,
        });
    }

    public IAsyncEnumerable<AgentChatStreamEvent> StreamChatAsync(AgentChatStreamRequest request, CancellationToken cancellationToken = default)
        => ProviderChatStream.ReadAsync(CreateChatClientAsync, request, cancellationToken);

    public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IChatClient>(new GeminiChatClient(context, _credentials, _clientFactory));
    }
}
