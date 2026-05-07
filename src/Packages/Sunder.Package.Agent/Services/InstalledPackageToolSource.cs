using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class InstalledPackageToolSource(IPackageExtensionCatalog extensionCatalog) : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentToolPresentationResolver
{
    private const string LocalSourceKind = "local";
    private const string LocalSourceId = "installed-packages";
    private const string LocalSourceDisplayName = "Installed Tools";

    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;

    public string SourceId => LocalSourceId;

    public string DisplayName => LocalSourceDisplayName;

    public string SourceKind => LocalSourceKind;

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var descriptors = ListTools()
            .Select(tool => WithSource(tool.Descriptor))
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>(descriptors);
    }

    public async ValueTask<AgentToolReadiness?> GetReadinessAsync(
        string toolId,
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        var tool = GetTool(toolId);
        return tool is null
            ? null
            : await tool.GetReadinessAsync(cancellationToken);
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        var tool = GetTool(request.ToolId);
        if (tool is null)
        {
            return new AgentToolResult(
                request.ToolId,
                $"Tool '{request.ToolId}' is not installed.",
                Content: $"### Tool unavailable\n\nTool '{request.ToolId}' is not installed.",
                IsError: true,
                ErrorCode: "tool-not-found");
        }

        var readiness = await tool.GetReadinessAsync(cancellationToken);
        if (readiness.Status != AgentToolReadinessStatus.Ready)
        {
            return new AgentToolResult(
                request.ToolId,
                readiness.Message,
                Content: $"### Tool not ready\n\n{readiness.Message}",
                IsError: true,
                ErrorCode: "tool-not-ready");
        }

        return await tool.ExecuteAsync(context, request, cancellationToken);
    }

    public async ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        var tool = GetTool(request.ToolId);
        return tool is IAgentPermissionAwareTool permissionAwareTool
            ? await permissionAwareTool.BuildPermissionRequestAsync(context, request, cancellationToken)
            : null;
    }

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
        => GetTool(request.ToolId) is IAgentToolPresentationResolver resolver
            ? resolver.ResolveToolPresentation(request)
            : null;

    private IReadOnlyList<IAgentTool> ListTools()
        => _extensionCatalog.GetExtensions(PackageExtensionPoints.Tools)
            .OrderBy(tool => tool.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private IAgentTool? GetTool(string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return null;
        }

        return ListTools().FirstOrDefault(tool => string.Equals(tool.Descriptor.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
    }

    private static AgentToolDescriptor WithSource(AgentToolDescriptor descriptor)
        => descriptor with
        {
            SourceKind = string.IsNullOrWhiteSpace(descriptor.SourceKind) ? LocalSourceKind : descriptor.SourceKind,
            SourceId = string.IsNullOrWhiteSpace(descriptor.SourceId) ? LocalSourceId : descriptor.SourceId,
            SourceDisplayName = string.IsNullOrWhiteSpace(descriptor.SourceDisplayName) ? LocalSourceDisplayName : descriptor.SourceDisplayName,
        };
}
