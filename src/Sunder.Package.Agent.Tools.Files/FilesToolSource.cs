using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tools.Files;

public sealed class FilesToolSource(IPackageExtensionCatalog extensionCatalog)
    : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentPermissionSurface, IAgentSystemPromptContributor, IAgentToolPresentationResolver
{
    public string SourceId => FileToolDescriptorRegistry.SourceId;

    public string DisplayName => FileToolDescriptorRegistry.DisplayName;

    public string SourceKind => "workspace";

    public string SurfaceId => "files";

    public string ContributorId => FileToolDescriptorRegistry.SourceId;

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
        => FileToolPresentationAdapter.Resolve(request);

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(FileToolDescriptorRegistry.Descriptors);
    }

    public async ValueTask<AgentToolReadiness?> GetReadinessAsync(
        string toolId,
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        if (!FileToolDescriptorRegistry.Contains(toolId))
        {
            return null;
        }

        if (context.Workspace is null)
        {
            return new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, "File tools require a selected workspace.");
        }

        var target = ResolveTarget(context.ExecutionBinding);
        if (target is null || context.ExecutionBinding is null)
        {
            return new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, "The selected workspace is not bound to an installed execution target.");
        }

        var readiness = await target.GetReadinessAsync(
            new AgentExecutionTargetContext(context.SessionId, context.Profile?.ProfileId, context.Workspace, context.ExecutionBinding),
            cancellationToken);
        return readiness.Status == AgentExecutionTargetReadinessStatus.Ready && target.Descriptor.SupportsFiles
            ? new AgentToolReadiness(toolId, AgentToolReadinessStatus.Ready, "Workspace file tools are ready.")
            : new AgentToolReadiness(toolId, AgentToolReadinessStatus.Failed, readiness.Message);
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        if (context.Workspace is null)
        {
            return FileToolResult.Error(request.ToolId, "File tools require a selected workspace.", "files-workspace-required");
        }

        var target = ResolveTarget(context.ExecutionBinding);
        if (target is null || context.ExecutionBinding is null)
        {
            return FileToolResult.Error(request.ToolId, "The selected workspace is not bound to an installed execution target.", "files-target-required");
        }

        var targetContext = new AgentExecutionTargetContext(
            context.SessionId,
            context.ProfileId,
            context.Workspace,
            context.ExecutionBinding,
            context.AllowOutsideConfiguredScope);
        try
        {
            return request.ToolId.ToLowerInvariant() switch
            {
                "read" => await FileReadHandler.ExecuteAsync(target, targetContext, request, cancellationToken),
                "write" => await FileWriteHandler.ExecuteWriteAsync(target, targetContext, request, cancellationToken),
                "edit" => await FileWriteHandler.ExecuteEditAsync(target, targetContext, request, cancellationToken),
                "apply_patch" => await FilePatchHandler.ExecuteAsync(target, targetContext, request, cancellationToken),
                "grep" => await FileSearchHandler.ExecuteGrepAsync(target, targetContext, request, cancellationToken),
                "glob" => await FileSearchHandler.ExecuteGlobAsync(target, targetContext, request, cancellationToken),
                _ => FileToolResult.Error(request.ToolId, $"Unknown file tool '{request.ToolId}'.", "files-tool-unknown"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FileToolResult.Error(request.ToolId, ex.Message, "files-execution");
        }
    }

    public ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
        => FilePermissionPlanner.BuildAsync(ResolveTarget(context.ExecutionBinding), context, request, cancellationToken);

    public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
        => FilePermissionPlanner.Actions;

    public ValueTask<IReadOnlyList<AgentSystemPromptBlock>> ContributeAsync(
        AgentSystemPromptRequest request,
        CancellationToken cancellationToken = default)
        => FileSystemPromptBuilder.BuildAsync(ResolveTarget(request.ExecutionBinding), request, cancellationToken);

    private IAgentExecutionTarget? ResolveTarget(AgentWorkspaceBindingRecord? binding)
        => extensionCatalog.GetExtensions(PackageExtensionPoints.ExecutionTargets)
            .FirstOrDefault(target => binding is not null
                                      && (string.Equals(target.Descriptor.TargetId, binding.ContributionId, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(target.Descriptor.TargetKind, binding.ContributionId, StringComparison.OrdinalIgnoreCase)));
}
