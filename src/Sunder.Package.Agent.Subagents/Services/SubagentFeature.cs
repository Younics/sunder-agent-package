using System.Runtime.CompilerServices;
using System.Threading.Channels;
using IPromptContextContributorService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Prompt_Context_Contributor.IPromptContextContributorService;
using ISelectableCapabilityProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Selectable_Capability_Provider.ISelectableCapabilityProviderService;
using IToolSourceService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Source.IToolSourceService;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Subagents.Services;

[SunderServiceProvider(typeof(IToolSourceService), ProviderId = "subagents.tools")]
[SunderServiceProvider(typeof(IPromptContextContributorService), ProviderId = "subagents.prompt.context")]
[SunderServiceProvider(typeof(ISelectableCapabilityProviderService), ProviderId = "subagents.selectable.capabilities")]
public sealed partial class SubagentFeature : IToolSourceService, IPromptContextContributorService, ISelectableCapabilityProviderService
{
    private readonly SubagentService _service;
    private readonly SubagentDescriptorSchema _descriptors;
    private readonly SubagentDelegationOrchestrator _orchestrator;
    private readonly SubagentBatchResultRenderer _renderer;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal SubagentFeature(SubagentService service, SubagentDescriptorSchema descriptors,
        SubagentDelegationOrchestrator orchestrator, SubagentBatchResultRenderer renderer)
    { _service = service; _descriptors = descriptors; _orchestrator = orchestrator; _renderer = renderer; }

    public ValueTask<AgentToolSourceDescriptor> DescribeAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentToolSourceDescriptor
        { SourceId = SubagentConstants.PackageId, DisplayName = SubagentDescriptorSchema.SourceDisplayName, SourceKind = "subagent" });
    }
    ValueTask<AgentPromptContextContributorDescriptor> IPromptContextContributorService.DescribeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentPromptContextContributorDescriptor
        { ContributorId = SubagentConstants.PackageId, DisplayName = SubagentDescriptorSchema.SourceDisplayName, AcknowledgmentSinkId = null });
    }
    ValueTask<AgentServiceIdentity> ISelectableCapabilityProviderService.DescribeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentServiceIdentity { Id = SubagentConstants.PackageId, DisplayName = SubagentDescriptorSchema.SourceDisplayName });
    }

    public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListAsync(AgentProfileRecord? profile, CancellationToken token = default)
        => _descriptors.ListCapabilitiesAsync(profile, token);
    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken token = default)
        => _descriptors.ListToolsAsync(context, token);
    public ValueTask<AgentToolReadiness?> GetReadinessAsync(CYounics_Ai_Agent_Tool_Source_ToolSourceGetReadinessRequest request, CancellationToken token = default)
        => _descriptors.GetReadinessAsync(request.ToolId, request.Context, token);
    public ValueTask<AgentToolResult> ExecuteAsync(CYounics_Ai_Agent_Tool_Source_ToolSourceExecuteRequest request, CancellationToken token = default)
        => _orchestrator.ExecuteAsync(request.Context, request.Request, token);
    public ValueTask<AgentPromptContextContribution?> ContributeAsync(AgentPromptContextRequest request, CancellationToken token = default)
        => _descriptors.ContributeContextAsync(request, token);
    public ValueTask<AgentToolPresentation?> PresentationAsync(AgentToolPresentationRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(_renderer.ResolveToolPresentation(request)); }
    public ValueTask<AgentToolResult?> PreflightAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePreflightRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<AgentToolResult?>(null); }
    public ValueTask<AgentPermissionRequest?> PermissionAsync(CYounics_Ai_Agent_Tool_Source_ToolSourcePermissionRequest request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<AgentPermissionRequest?>(null); }
    public ValueTask AcknowledgeAsync(AgentPromptContextReceipt request, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    public async IAsyncEnumerable<AgentCapabilityCatalogChange> WatchAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        var channel = Channel.CreateBounded<AgentCapabilityCatalogChange>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        long revision = 0;
        void Changed() => channel.Writer.TryWrite(new() { Revision = Interlocked.Increment(ref revision) });
        _service.SubagentsChanged += Changed;
        try
        {
            await foreach (var change in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            { token.ThrowIfCancellationRequested(); yield return change; }
        }
        finally { _service.SubagentsChanged -= Changed; channel.Writer.TryComplete(); }
    }

    internal static string BuildDelegateTasksResultContent(IReadOnlyList<SubagentTaskResult> results)
        => SubagentBatchResultRenderer.BuildDelegateTasksResultContent(results);
}
