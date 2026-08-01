using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Subagents.Models;

namespace Sunder.Package.Agent.Subagents.Services;

public sealed class SubagentFeature :
    IAgentProfileSelectableCapabilityProvider,
    IAgentProfileSelectableCapabilityChangeNotifier,
    IAgentToolSource,
    IAgentPromptContextContributor,
    IAgentToolPresentationResolver
{
    private readonly SubagentService _subagentService;
    private readonly SubagentDescriptorSchema _descriptors;
    private readonly SubagentRequestParser _requestParser;
    private readonly SubagentDelegationOrchestrator _orchestrator;
    private readonly SubagentBatchResultRenderer _resultRenderer;

    public SubagentFeature(
        SubagentService subagentService,
        AgentRpcCatalog rpcCatalog)
    {
        _subagentService = subagentService;
        var permissionStatusAdapter = new SubagentPermissionStatusAdapter(rpcCatalog);
        _descriptors = new SubagentDescriptorSchema(subagentService, rpcCatalog, permissionStatusAdapter);
        _requestParser = new SubagentRequestParser();
        _resultRenderer = new SubagentBatchResultRenderer(subagentService, _requestParser, permissionStatusAdapter);
        var childRunCoordinator = new SubagentChildRunCoordinator(
            rpcCatalog,
            _descriptors,
            permissionStatusAdapter,
            _resultRenderer);
        _orchestrator = new SubagentDelegationOrchestrator(
            _requestParser,
            _descriptors,
            childRunCoordinator,
            permissionStatusAdapter,
            _resultRenderer);
    }

    public string ProviderId => SubagentConstants.PackageId;

    public string SourceId => SubagentConstants.PackageId;

    public string SourceKind => "subagent";

    public string DisplayName => SubagentDescriptorSchema.SourceDisplayName;

    public string ContributorId => SubagentConstants.PackageId;

    public event Action? SelectableCapabilitiesChanged
    {
        add => _subagentService.SubagentsChanged += value;
        remove => _subagentService.SubagentsChanged -= value;
    }

    public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(
        AgentProfileSelectableCapabilityRequest request,
        CancellationToken cancellationToken = default)
        => _descriptors.ListCapabilitiesAsync(request, cancellationToken);

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
        => _descriptors.ListToolsAsync(context, cancellationToken);

    public ValueTask<AgentToolReadiness?> GetReadinessAsync(
        string requestedToolId,
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
        => _descriptors.GetReadinessAsync(requestedToolId, context, cancellationToken);

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
        => _resultRenderer.ResolveToolPresentation(request);

    public ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
        => _orchestrator.ExecuteAsync(context, request, cancellationToken);

    public ValueTask<AgentPromptContextContribution?> ContributeContextAsync(
        AgentPromptContextRequest request,
        CancellationToken cancellationToken = default)
        => _descriptors.ContributeContextAsync(request, cancellationToken);

    internal static string BuildDelegateTasksResultContent(IReadOnlyList<SubagentTaskResult> results)
        => SubagentBatchResultRenderer.BuildDelegateTasksResultContent(results);
}
