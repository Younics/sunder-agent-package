using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FilePermissionPlanner
{
    private static IReadOnlyList<AgentPermissionBoundaryDescriptor> Boundaries { get; } =
    [
        new(AgentPermissionBoundaryIds.ConfiguredScope, "Files inside configured workspace paths", "Paths resolved by the selected execution target inside configured workspace paths.", AgentPermissionDecision.Allow),
        new(AgentPermissionBoundaryIds.OutsideConfiguredScope, "Files outside configured workspace paths", "Paths resolved by the selected execution target outside configured workspace paths.", AgentPermissionDecision.Ask),
        new(AgentPermissionBoundaryIds.Unknown, "Files whose workspace scope is unknown", "Requests that could not be classified by the selected execution target.", AgentPermissionDecision.Ask),
    ];

    public static IReadOnlyList<AgentPermissionActionDescriptor> Actions { get; } =
    [
        new("files.read", "Read files", "Read files and directories.", Boundaries),
        new("files.search", "Search files", "Search file names and contents.", Boundaries),
        new("files.mutate", "Modify files", "Create, overwrite, or edit files.", Boundaries),
    ];

    public static async ValueTask<AgentPermissionRequest?> BuildAsync(
        IAgentExecutionTarget? target,
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var actionId = request.ToolId.ToLowerInvariant() switch
        {
            "read" => "files.read",
            "grep" or "glob" => "files.search",
            "write" or "edit" or "apply_patch" => "files.mutate",
            _ => "files.read",
        };
        var scope = string.Equals(request.ToolId, "apply_patch", StringComparison.OrdinalIgnoreCase)
            ? await ResolvePatchScopeAsync(target, context, request.ArgumentsJson, cancellationToken)
            : await ResolvePathScopeAsync(target, context, FileToolArguments.ResolvePermissionPath(request.ToolId, request.ArgumentsJson), cancellationToken);

        return new AgentPermissionRequest(
            actionId,
            scope.BoundaryId,
            $"{request.ToolId} {scope.SummaryTarget}",
            ToolId: request.ToolId,
            Path: scope.Path,
            WorkspaceId: context.Workspace?.WorkspaceId,
            BindingId: context.ExecutionBinding?.BindingId,
            ResourceDisplayName: scope.ResourceDisplayName,
            ResourceReference: scope.ResourceReference,
            IsMutation: actionId == "files.mutate");
    }

    private static async ValueTask<FilePermissionScope> ResolvePathScopeAsync(
        IAgentExecutionTarget? target,
        AgentToolExecutionContext context,
        string? path,
        CancellationToken cancellationToken)
    {
        if (!CanResolve(target, context, path))
        {
            return FilePermissionScope.Unknown(path);
        }

        var resource = await target!.ResolveFileResourceAsync(CreateTargetContext(context), path!, cancellationToken);
        return FilePermissionScope.FromResource(path!, resource);
    }

    private static async ValueTask<FilePermissionScope> ResolvePatchScopeAsync(
        IAgentExecutionTarget? target,
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        if (target is null || context.Workspace is null || context.ExecutionBinding is null
            || !FileToolArguments.TryParsePatch(argumentsJson, out var args, out _))
        {
            return FilePermissionScope.Unknown();
        }

        IReadOnlyList<FilePatchOperation> operations;
        try
        {
            operations = FilePatchParser.Parse(args.PatchText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FilePermissionScope.Unknown();
        }

        var paths = operations.Select(operation => operation.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return FilePermissionScope.Unknown();
        }

        var hasUnknown = false;
        var hasOutside = false;
        AgentResolvedResource? singleResource = null;
        foreach (var path in paths)
        {
            try
            {
                var resource = await target.ResolveFileResourceAsync(CreateTargetContext(context), path, cancellationToken);
                singleResource = paths.Length == 1 ? resource : null;
                if (string.Equals(resource.PermissionBoundaryId, AgentPermissionBoundaryIds.OutsideConfiguredScope, StringComparison.OrdinalIgnoreCase))
                {
                    hasOutside = true;
                }
                else if (!string.Equals(resource.PermissionBoundaryId, AgentPermissionBoundaryIds.ConfiguredScope, StringComparison.OrdinalIgnoreCase))
                {
                    hasUnknown = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                hasUnknown = true;
            }
        }

        var boundaryId = hasOutside
            ? AgentPermissionBoundaryIds.OutsideConfiguredScope
            : hasUnknown ? AgentPermissionBoundaryIds.Unknown : AgentPermissionBoundaryIds.ConfiguredScope;
        return new FilePermissionScope(
            boundaryId,
            paths.Length == 1 ? paths[0] : $"{paths.Length} workspace files",
            paths.Length == 1 ? paths[0] : null,
            singleResource?.DisplayName,
            singleResource?.CanonicalReference);
    }

    private static bool CanResolve(IAgentExecutionTarget? target, AgentToolExecutionContext context, string? path)
        => target is not null
           && context.Workspace is not null
           && context.ExecutionBinding is not null
           && !string.IsNullOrWhiteSpace(path);

    private static AgentExecutionTargetContext CreateTargetContext(AgentToolExecutionContext context)
        => new(
            context.SessionId,
            context.ProfileId,
            context.Workspace!,
            context.ExecutionBinding!,
            AllowOutsideConfiguredScope: true);
}

internal sealed record FilePermissionScope(
    string BoundaryId,
    string SummaryTarget,
    string? Path,
    string? ResourceDisplayName,
    string? ResourceReference)
{
    public static FilePermissionScope Unknown(string? path = null)
        => new(
            AgentPermissionBoundaryIds.Unknown,
            string.IsNullOrWhiteSpace(path) ? "workspace files" : path,
            path,
            ResourceDisplayName: null,
            ResourceReference: null);

    public static FilePermissionScope FromResource(string path, AgentResolvedResource resource)
        => new(resource.PermissionBoundaryId, path, path, resource.DisplayName, resource.CanonicalReference);
}
