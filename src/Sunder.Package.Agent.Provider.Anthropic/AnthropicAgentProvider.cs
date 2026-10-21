using Anthropic;
using Microsoft.Extensions.AI;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IChatProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Chat_Provider.IChatProviderService;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;
using AgentChatClientContext = Sunder.Package.Agent.Contracts.Models.AgentChatClientContext;

namespace Sunder.Package.Agent.Provider.Anthropic;

[SunderServiceProvider(typeof(IChatProviderService), ProviderId = "anthropic.chat")]
public sealed partial class AnthropicAgentProvider : IChatProviderService
{
    private readonly IPackageContext _packageContext;
    private readonly ProviderCredentialAccessor _credentials;
    private readonly Func<string, IAnthropicClient> _clientFactory;

    public AnthropicAgentProvider(IPackageContext packageContext)
        : this(
            packageContext,
            new ProviderCredentialAccessor(packageContext.Secrets, AnthropicProviderConfiguration.ApiKeySecretKey))
    {
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal AnthropicAgentProvider(IPackageContext packageContext, ProviderSettings settings)
        : this(packageContext, settings.Credentials) { }

    internal AnthropicAgentProvider(IPackageContext packageContext, ProviderCredentialAccessor credentials,
        Func<string, IAnthropicClient>? clientFactory = null)
    {
        _packageContext = packageContext;
        _credentials = credentials;
        _clientFactory = clientFactory ?? (apiKey => new AnthropicClient { ApiKey = apiKey });
        Descriptor = new AgentProviderDescriptor
        {
            ProviderId = "anthropic",
            DisplayName = "Anthropic",
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
        return ValueTask.FromResult(AnthropicModelCatalog.Models);
    }

    public async ValueTask<string?> ResolveUtilityModelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await AnthropicProviderConfiguration.UtilityModelSelection
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
                ? "Anthropic API-key mode is configured."
                : "An Anthropic API key is required. Open Settings -> Packages -> AI Agent - Anthropic and enter an API key.",
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
            Summary = "Anthropic supports native tool use and Sunder can run parallel-safe tools concurrently.",
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

    public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IChatClient>(new AnthropicChatClient(context, _credentials, _clientFactory));
    }
}
