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

    private readonly IPackageExtensionInvocationCatalog _invocationCatalog =
        AgentExtensionInvocation.Require(extensionCatalog);

    public string SourceId => LocalSourceId;

    public string DisplayName => LocalSourceDisplayName;

    public string SourceKind => LocalSourceKind;

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var descriptors = ListTools()
            .Select(tool => tool.Descriptor)
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
            : await InvokeAsync(
                tool,
                cancellationToken,
                static (instance, token) => instance.GetReadinessAsync(token));
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

        AgentToolReadiness readiness;
        try
        {
            readiness = await InvokeAsync(
                tool,
                cancellationToken,
                static (instance, token) => instance.GetReadinessAsync(token));
        }
        catch (AgentPackageUnavailableException ex)
        {
            return PackageUnavailable(request.ToolId, ex.Message);
        }
        if (readiness.Status != AgentToolReadinessStatus.Ready)
        {
            return new AgentToolResult(
                request.ToolId,
                readiness.Message,
                Content: $"### Tool not ready\n\n{readiness.Message}",
                IsError: true,
                ErrorCode: "tool-not-ready");
        }

        try
        {
            return await InvokeAsync(
                tool,
                cancellationToken,
                (instance, token) => instance.ExecuteAsync(context, request, token));
        }
        catch (AgentPackageUnavailableException ex)
        {
            return PackageUnavailable(request.ToolId, ex.Message);
        }
    }

    public async ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        var tool = GetTool(request.ToolId);
        return tool?.SupportsPermission == true
            ? await InvokeAsync(
                tool,
                cancellationToken,
                (instance, token) => ((IAgentPermissionAwareTool)instance)
                    .BuildPermissionRequestAsync(context, request, token))
            : null;
    }

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
    {
        var tool = GetTool(request.ToolId);
        if (tool?.SupportsPresentation != true || !tool.Reference.TryAcquire(out var lease))
        {
            return null;
        }

        using (lease)
        {
            return lease.RetirementToken.IsCancellationRequested
                ? null
                : ((IAgentToolPresentationResolver)lease.Contribution)
                    .ResolveToolPresentation(request);
        }
    }

    private IReadOnlyList<InstalledToolReference> ListTools()
        => _invocationCatalog.GetExtensionReferences(PackageExtensionPoints.Tools)
            .Select(CreateReference)
            .OfType<InstalledToolReference>()
            .OrderBy(tool => tool.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private InstalledToolReference? GetTool(string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return null;
        }

        return ListTools().FirstOrDefault(tool => string.Equals(
            tool.Descriptor.ToolId,
            toolId,
            StringComparison.OrdinalIgnoreCase));
    }

    private static InstalledToolReference? CreateReference(
        IPackageExtensionReference<IAgentTool> reference)
    {
        if (!reference.TryAcquire(out var lease))
        {
            return null;
        }

        using (lease)
        {
            var descriptor = WithSource(lease.Contribution.Descriptor);
            return lease.RetirementToken.IsCancellationRequested
                ? null
                : new InstalledToolReference(
                    reference,
                    lease.PackageId,
                    descriptor,
                    lease.Contribution is IAgentPermissionAwareTool,
                    lease.Contribution is IAgentToolPresentationResolver);
        }
    }

    private static ValueTask<TResult> InvokeAsync<TResult>(
        InstalledToolReference tool,
        CancellationToken cancellationToken,
        Func<IAgentTool, CancellationToken, ValueTask<TResult>> callback)
        => AgentExtensionInvocation.InvokeAsync(
            new AgentExtensionReference<IAgentTool, AgentToolDescriptor>(
                tool.Reference,
                tool.PackageId,
                tool.Descriptor),
            cancellationToken,
            callback);

    private static AgentToolResult PackageUnavailable(string toolId, string message)
        => new(
            toolId,
            message,
            Content: $"### Tool package unavailable\n\n{message}",
            IsError: true,
            ErrorCode: AgentToolResultErrorCodes.PackageUnavailable);

    private static AgentToolDescriptor WithSource(AgentToolDescriptor descriptor)
        => descriptor with
        {
            SourceKind = string.IsNullOrWhiteSpace(descriptor.SourceKind) ? LocalSourceKind : descriptor.SourceKind,
            SourceId = string.IsNullOrWhiteSpace(descriptor.SourceId) ? LocalSourceId : descriptor.SourceId,
            SourceDisplayName = string.IsNullOrWhiteSpace(descriptor.SourceDisplayName) ? LocalSourceDisplayName : descriptor.SourceDisplayName,
            Aliases = descriptor.Aliases?.ToArray(),
        };

    private sealed record InstalledToolReference(
        IPackageExtensionReference<IAgentTool> Reference,
        string PackageId,
        AgentToolDescriptor Descriptor,
        bool SupportsPermission,
        bool SupportsPresentation);
}
