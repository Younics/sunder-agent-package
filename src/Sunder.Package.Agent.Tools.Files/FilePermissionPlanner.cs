using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FilePermissionPlanner
{
    private const int PatchAuthorityUsesPerPath = 2;
    private const int MaximumPatchPaths = 64;
    private const int MaximumResourceCapabilities = 128;

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
        FilePermissionScope scope;
        if (string.Equals(request.ToolId, "apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            scope = await ResolvePatchScopeAsync(
                target,
                context,
                actionId,
                request.ArgumentsJson,
                cancellationToken);
        }
        else if (!TryResolveValidatedPath(request, out var path))
        {
            scope = FilePermissionScope.Unknown();
        }
        else
        {
            scope = await ResolvePathScopeAsync(
                target,
                context,
                actionId,
                path!,
                AuthorityUseCount(request.ToolId),
                cancellationToken);
        }

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
            IsMutation: actionId == "files.mutate")
        {
            ResourceClaims = scope.ResourceClaims,
            ResourceCapabilities = scope.ResourceCapabilities,
            ResourceReferences = scope.ResourceReferences,
            ScopeClassificationBasis = scope.ScopeClassificationBasis,
        };
    }

    private static async ValueTask<FilePermissionScope> ResolvePathScopeAsync(
        IAgentExecutionTarget? target,
        AgentToolExecutionContext context,
        string actionId,
        string path,
        int authorityUseCount,
        CancellationToken cancellationToken)
    {
        if (!CanResolve(target, context, path))
        {
            return FilePermissionScope.Unknown(path);
        }

        var resource = await target!.ResolveFileResourceAsync(
            CreateTargetContext(context, actionId, resourceIndex: 0, authorityUseCount),
            path,
            cancellationToken);
        return FilePermissionScope.FromResource(path, resource);
    }

    private static async ValueTask<FilePermissionScope> ResolvePatchScopeAsync(
        IAgentExecutionTarget? target,
        AgentToolExecutionContext context,
        string actionId,
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
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0)
        {
            return FilePermissionScope.Unknown();
        }
        if (operations.Count > FilePatchParser.MaximumOperations || paths.Length > MaximumPatchPaths)
        {
            return FilePermissionScope.Unknown();
        }

        var hasUnknown = false;
        var hasOutside = false;
        AgentResolvedResource? singleResource = null;
        var resourceReferences = new List<string>();
        var resourceClaims = new List<AgentResourceClaim>();
        var classificationBases = new HashSet<AgentPermissionScopeClassificationBasis>();
        using var plannedAuthority = new PlannedResourceAuthority(target, MaximumResourceCapabilities);
        for (var resourceIndex = 0; resourceIndex < paths.Length; resourceIndex++)
        {
            var path = paths[resourceIndex];
            try
            {
                var resource = await target.ResolveFileResourceAsync(
                    CreateTargetContext(
                        context,
                        actionId,
                        resourceIndex,
                        PatchAuthorityUsesPerPath),
                    path,
                    cancellationToken);
                plannedAuthority.Add(
                    resource.DeleteAuthorityReferences.Concat(resource.AuthorityReferences));
                classificationBases.Add(resource.ScopeClassificationBasis);
                singleResource = paths.Length == 1 ? resource : null;
                var isDelete = operations
                    .Where(operation => string.Equals(operation.Path, path, StringComparison.Ordinal))
                    .All(operation => operation.Kind == FilePatchOperationKind.Delete);
                var operationReferences = isDelete
                    ? new[] { resource.DeleteCanonicalReference, resource.CanonicalReference }
                    : [resource.CanonicalReference];
                foreach (var resourceReference in operationReferences
                             .Where(reference => !string.IsNullOrWhiteSpace(reference))
                             .Distinct(StringComparer.Ordinal))
                {
                    resourceReferences.Add(resourceReference!);
                }
                foreach (var claim in new[] { resource.DeleteResourceClaim, resource.ResourceClaim }
                             .Where(claim => claim is not null)
                             .Distinct())
                {
                    resourceClaims.Add(claim!);
                }
                var operationBoundaries = isDelete
                    ? new[] { resource.DeletePermissionBoundaryId ?? resource.PermissionBoundaryId, resource.PermissionBoundaryId }
                    : [resource.PermissionBoundaryId];
                if (operationBoundaries.Any(boundary => string.Equals(
                        boundary,
                        AgentPermissionBoundaryIds.OutsideConfiguredScope,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    hasOutside = true;
                }
                else if (operationBoundaries.Any(boundary => !string.Equals(
                             boundary,
                             AgentPermissionBoundaryIds.ConfiguredScope,
                             StringComparison.OrdinalIgnoreCase)))
                {
                    hasUnknown = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return FilePermissionScope.Unknown();
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
            resourceReferences.Count == 1 ? resourceReferences[0] : null,
            resourceReferences.Distinct(StringComparer.Ordinal).ToArray(),
            resourceClaims
                .DistinctBy(static claim => (claim.NamespaceId, claim.ResourceIndex, claim.LogicalPath))
                .OrderBy(static claim => claim.ResourceIndex)
                .ToArray(),
            plannedAuthority.Detach(),
            classificationBases.Count == 1
                ? classificationBases.Single()
                : AgentPermissionScopeClassificationBasis.Mixed);
    }

    private static bool CanResolve(IAgentExecutionTarget? target, AgentToolExecutionContext context, string? path)
        => target is not null
           && context.Workspace is not null
           && context.ExecutionBinding is not null
           && !string.IsNullOrWhiteSpace(path);

    private static AgentExecutionTargetContext CreateTargetContext(
        AgentToolExecutionContext context,
        string actionId,
        int resourceIndex,
        int authorityUseCount)
    {
        var operation = context.ResourceOperation is { } current
            ? current with
            {
                ActionId = actionId,
                ResourceIndex = resourceIndex,
                AuthorityUseCount = authorityUseCount,
                CanIssueOutsideAuthority = current.CanIssueOutsideAuthority,
            }
            : null;
        return new(
            context.SessionId,
            context.ProfileId,
            context.Workspace!,
            context.ExecutionBinding!,
            AllowOutsideConfiguredScope: true)
        {
            ResourceOperation = operation,
            ExpectedConfigurationGeneration = context.ExecutionTargetConfigurationGeneration,
        };
    }

    private static int AuthorityUseCount(string toolId)
        => toolId.Equals("edit", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

    private sealed class PlannedResourceAuthority(
        IAgentExecutionTarget target,
        int maximumCapabilities) : IDisposable
    {
        private readonly HashSet<string> _capabilities = new(StringComparer.Ordinal);
        private bool _detached;

        public void Add(IEnumerable<string> capabilities)
        {
            foreach (var capability in capabilities.Where(static value => !string.IsNullOrWhiteSpace(value)))
            {
                _capabilities.Add(capability);
            }
            if (_capabilities.Count > maximumCapabilities)
            {
                throw new InvalidOperationException(
                    $"Patch planning supports at most {maximumCapabilities} resource capabilities.");
            }
        }

        public IReadOnlyList<string> Detach()
        {
            _detached = true;
            return _capabilities.ToArray();
        }

        public void Dispose()
        {
            if (_detached || _capabilities.Count == 0)
            {
                return;
            }
            if (AgentExecutionTargetRpc.SupportsFacet(target, AgentExecutionFacetIds.ResourceAuthority)
                && target is IAgentResourceAuthorityExecutionTarget authorityTarget)
            {
                authorityTarget.ReleaseResourceAuthority(_capabilities.ToArray());
            }
        }
    }

    private static bool TryResolveValidatedPath(
        AgentToolRequest request,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? path)
    {
        path = null;
        switch (request.ToolId.ToLowerInvariant())
        {
            case "read":
                if (!FileToolArguments.TryParseRead(request.ArgumentsJson, out var read, out _))
                {
                    return false;
                }
                path = read.Path;
                return true;
            case "write":
                if (!FileToolArguments.TryParseWrite(request.ArgumentsJson, out var write, out _))
                {
                    return false;
                }
                path = write.Path;
                return true;
            case "edit":
                if (!FileToolArguments.TryParseEdit(request.ArgumentsJson, out var edit, out _))
                {
                    return false;
                }
                path = edit.Path;
                return true;
            case "grep":
                if (!FileToolArguments.TryParseGrep(request.ArgumentsJson, out var grep, out _))
                {
                    return false;
                }
                path = string.IsNullOrWhiteSpace(grep.Path) ? "." : grep.Path;
                return HostSecureFileSearch.TryValidateRequest(
                    new AgentFileSearchRequest(path, AgentFileSearchKind.Grep, grep.Pattern, grep.Include),
                    out _);
            case "glob":
                if (!FileToolArguments.TryParseGlob(request.ArgumentsJson, out var glob, out _))
                {
                    return false;
                }
                path = string.IsNullOrWhiteSpace(glob.Path) ? "." : glob.Path;
                return HostSecureFileSearch.TryValidateRequest(
                    new AgentFileSearchRequest(path, AgentFileSearchKind.Glob, glob.Pattern),
                    out _);
            default:
                return false;
        }
    }
}

internal sealed record FilePermissionScope(
    string BoundaryId,
    string SummaryTarget,
    string? Path,
    string? ResourceDisplayName,
    string? ResourceReference,
    IReadOnlyList<string> ResourceReferences,
    IReadOnlyList<AgentResourceClaim> ResourceClaims,
    IReadOnlyList<string> ResourceCapabilities,
    AgentPermissionScopeClassificationBasis ScopeClassificationBasis)
{
    public static FilePermissionScope Unknown(string? path = null)
        => new(
            AgentPermissionBoundaryIds.Unknown,
            string.IsNullOrWhiteSpace(path) ? "workspace files" : path,
            path,
            ResourceDisplayName: null,
            ResourceReference: null,
            ResourceReferences: [],
            ResourceClaims: [],
            ResourceCapabilities: [],
            ScopeClassificationBasis: AgentPermissionScopeClassificationBasis.Unresolved);

    public static FilePermissionScope FromResource(string path, AgentResolvedResource resource)
        => new(
            resource.PermissionBoundaryId,
            path,
            path,
            resource.DisplayName,
            resource.CanonicalReference,
            [resource.CanonicalReference],
            resource.ResourceClaim is null ? [] : [resource.ResourceClaim],
            resource.AuthorityReferences,
            resource.ScopeClassificationBasis);
}
