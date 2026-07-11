using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using AIChatClient = Microsoft.Extensions.AI.IChatClient;

namespace Sunder.Package.Agent.Provider.LMStudio;

public sealed class LMStudioAgentProvider : IAgentChatProvider, IAgentUtilityModelProvider, IDisposable
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

    public async ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _catalog.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? result.Models
                .Where(model => model.IsChatCandidate)
                .Select(model => new AgentModelDescriptor(
                    $"lmstudio/{model.Id}",
                    model.DisplayName,
                    model.ContextWindow ?? DefaultContextWindow,
                    model.MaxOutputTokens ?? DefaultMaxOutputTokens))
                .ToArray()
            : [];
    }

    public async ValueTask<string?> ResolveUtilityModelIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuredModelId = (await _packageContext.Configuration
            .GetValueAsync(LMStudioProviderConfiguration.UtilityModelKey, cancellationToken))?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredModelId))
        {
            return configuredModelId.StartsWith("lmstudio/", StringComparison.OrdinalIgnoreCase)
                ? configuredModelId
                : $"lmstudio/{configuredModelId}";
        }

        return (await GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.ModelId;
    }

    public async ValueTask<AgentProviderReadiness> GetReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = await _connection.GetOptionsAsync(cancellationToken);
        if (connection.Options is null)
        {
            return new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.NeedsConfiguration,
                $"The LM Studio base URL is invalid: {connection.ValidationError}");
        }

        var result = await _catalog.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Failed,
                result.Failure!.Message);
        }

        var hasChatModel = result.Models.Any(model => model.IsChatCandidate);
        return new AgentProviderReadiness(
            Descriptor.ProviderId,
            hasChatModel
                ? AgentProviderReadinessStatus.Ready
                : AgentProviderReadinessStatus.NeedsConfiguration,
            hasChatModel
                ? "LM Studio is reachable and ready."
                : "LM Studio is reachable, but no chat model is loaded or discoverable.");
    }

    public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentProviderRunCapabilities(
            SupportsNativeToolCalling: true,
            SupportsStreamingToolCalls: true,
            SupportsMultipleToolCalls: true,
            Summary: "LM Studio can expose OpenAI-compatible native tool calls when the loaded model supports them, and Sunder can run parallel-safe tools concurrently."));
    }

    public ValueTask<AIChatClient> CreateChatClientAsync(
        AgentChatClientContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<AIChatClient>(new LMStudioChatClient(context, _connection));
    }

    public void Dispose()
    {
        if (_ownsConnection)
        {
            _connection.Dispose();
        }
    }

    private static AgentProviderDescriptor CreateDescriptor(string packageId)
        => new(
            "lmstudio",
            "LM Studio",
            [],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true)
        {
            PackageId = packageId,
        };
}
