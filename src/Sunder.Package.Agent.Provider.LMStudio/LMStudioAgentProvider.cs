using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IChatProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Chat_Provider.IChatProviderService;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;
using AIChatClient = Microsoft.Extensions.AI.IChatClient;
using AgentChatClientContext = Sunder.Package.Agent.Contracts.Models.AgentChatClientContext;

namespace Sunder.Package.Agent.Provider.LMStudio;

[SunderServiceProvider(typeof(IChatProviderService), ProviderId = "lmstudio.chat")]
public sealed partial class LMStudioAgentProvider : IChatProviderService, IDisposable
{
    private const int DefaultContextWindow = 131072;
    private const int DefaultMaxOutputTokens = 8192;

    private readonly IPackageContext _packageContext;
    private readonly LMStudioConnection _connection;
    private readonly LMStudioModelCatalogService _catalog;
    private readonly bool _ownsConnection;

    public LMStudioAgentProvider(IPackageContext packageContext)
    {
        _packageContext = packageContext;
        _connection = new LMStudioConnection(packageContext);
        _catalog = new LMStudioModelCatalogService(_connection);
        _ownsConnection = true;
        Descriptor = CreateDescriptor(packageContext.PackageId);
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal LMStudioAgentProvider(
        IPackageContext packageContext,
        LMStudioConnection connection,
        LMStudioModelCatalogService catalog)
    {
        _packageContext = packageContext;
        _connection = connection;
        _catalog = catalog;
        Descriptor = CreateDescriptor(packageContext.PackageId);
    }

    public AgentProviderDescriptor Descriptor { get; }

    public ValueTask<AgentProviderDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Descriptor);
    }

    public async ValueTask<IReadOnlyList<AgentModelDescriptor>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _catalog.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? result.Models
                .Where(model => model.IsChatCandidate)
                .Select(model => new AgentModelDescriptor
                {
                    ModelId = $"lmstudio/{model.Id}",
                    DisplayName = model.DisplayName,
                    ContextWindow = model.ContextWindow ?? DefaultContextWindow,
                    MaxOutputTokens = model.MaxOutputTokens ?? DefaultMaxOutputTokens,
                    IsRecommended = false,
                    Variants = ProviderModelReasoning.Create((model.ReasoningOptions ?? []).Where(id => id is "low" or "medium" or "high").ToArray()),
                    SpeedOptions = null,
                    ModeOptions = null,
                    ReleaseDate = null,
                    DefaultReasoningVariantId = model.DefaultReasoning,
                })
                .ToArray()
            : [];
    }

    public async ValueTask<string?> ResolveUtilityModelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuredModelId = (await _packageContext.Settings
            .GetValueAsync(LMStudioProviderConfiguration.UtilityModelKey, cancellationToken))?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredModelId))
        {
            return ProviderModelId.EnsurePrefix(configuredModelId, "lmstudio");
        }

        return (await ListModelsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.ModelId;
    }

    public async ValueTask<AgentProviderReadiness> GetReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = await _connection.GetOptionsAsync(cancellationToken);
        if (connection.Options is null)
        {
            return new()
            {
                ProviderId = Descriptor.ProviderId,
                Status = AgentProviderReadinessStatus.NeedsConfiguration,
                Message = $"The LM Studio base URL is invalid: {connection.ValidationError}",
            };
        }

        var result = await _catalog.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return new()
            {
                ProviderId = Descriptor.ProviderId,
                Status = AgentProviderReadinessStatus.Failed,
                Message = result.Failure!.Message,
            };
        }

        var hasChatModel = result.Models.Any(model => model.IsChatCandidate);
        return new()
        {
            ProviderId = Descriptor.ProviderId,
            Status = hasChatModel
                ? AgentProviderReadinessStatus.Ready
                : AgentProviderReadinessStatus.NeedsConfiguration,
            Message = hasChatModel
                ? "LM Studio is reachable and ready."
                : "LM Studio is reachable, but no chat model is loaded or discoverable.",
        };
    }

    public async ValueTask<AgentProviderRunCapabilities> GetCapabilitiesAsync(
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _catalog.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        var model = result.Models.FirstOrDefault(item => string.Equals(item.Id, ProviderModelId.RemovePrefix(modelId ?? string.Empty, "lmstudio"), StringComparison.OrdinalIgnoreCase));
        return new AgentProviderRunCapabilities
        {
            SupportsNativeToolCalling = model?.SupportsTools ?? true,
            SupportsStreamingToolCalls = model?.SupportsTools ?? true,
            SupportsMultipleToolCalls = model?.SupportsTools ?? true,
            Summary = "LM Studio can expose OpenAI-compatible native tool calls when the loaded model supports them, and Sunder can run parallel-safe tools concurrently.",
            SupportsImageInput = model?.SupportsVision ?? false,
            SupportsPdfInput = false,
            SupportsAudioInput = false,
            SupportsVideoInput = false,
            ContextWindowTokens = model?.ContextWindow,
            MaxOutputTokens = model?.MaxOutputTokens,
        };
    }

    public IAsyncEnumerable<AgentChatStreamEvent> StreamChatAsync(AgentChatStreamRequest request, CancellationToken cancellationToken = default)
        => ProviderChatStream.ReadAsync(CreateChatClientAsync, request, cancellationToken);

    public ValueTask<AIChatClient> CreateChatClientAsync(
        AgentChatClientContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<AIChatClient>(new LMStudioChatClient(context, _connection, _catalog));
    }

    public void Dispose()
    {
        if (_ownsConnection)
        {
            _connection.Dispose();
        }
    }

    private static AgentProviderDescriptor CreateDescriptor(string packageId)
        => new()
        {
            ProviderId = "lmstudio",
            DisplayName = "LM Studio",
            SupportedAuthModes = [],
            SupportsStreaming = true,
            SupportsInterruptibleRuns = true,
            PackageId = packageId,
        };
}
