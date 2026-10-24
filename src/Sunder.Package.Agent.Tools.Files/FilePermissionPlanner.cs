using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FilePermissionPlanner
{
    private const int PatchAuthorityUsesPerPath = 2;
    private const int MaximumResourceCapabilities = 128;

    private static IReadOnlyList<AgentPermissionBoundaryDescriptor> Boundaries { get; } =
    [
        new() { BoundaryId = "configured-scope", DisplayName = "Files inside configured workspace paths", Description = "Paths resolved by the selected execution target inside configured workspace paths.", DefaultDecision = AgentPermissionDecision.Allow },
        new() { BoundaryId = "outside-configured-scope", DisplayName = "Files outside configured workspace paths", Description = "Paths resolved by the selected execution target outside configured workspace paths.", DefaultDecision = AgentPermissionDecision.Ask },
        new() { BoundaryId = "unknown", DisplayName = "Files whose workspace scope is unknown", Description = "Requests that could not be classified by the selected execution target.", DefaultDecision = AgentPermissionDecision.Ask },
    ];

    public static IReadOnlyList<AgentPermissionActionDescriptor> Actions { get; } =
    [
        new() { ActionId = "files.read", DisplayName = "Read files", Description = "Read files and directories.", Boundaries = Boundaries },
        new() { ActionId = "files.search", DisplayName = "Search files", Description = "Search file names and contents.", Boundaries = Boundaries },
        new() { ActionId = "files.mutate", DisplayName = "Modify files", Description = "Create, overwrite, edit, move, or delete files.", Boundaries = Boundaries },
    ];

    public static async ValueTask<AgentPermissionRequest?> BuildAsync(
        FileExecutionTarget? target,
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

        return new AgentPermissionRequest
        {
            ActionId = actionId, BoundaryId = scope.BoundaryId, Summary = $"{request.ToolId} {scope.SummaryTarget}",
            ToolId = request.ToolId, Path = scope.Path, Command = null, WorkspaceId = context.Workspace?.WorkspaceId,
            BindingId = context.ExecutionBinding?.BindingId, ResourceDisplayName = scope.ResourceDisplayName,
            ResourceReference = scope.ResourceReference, IsMutation = actionId == "files.mutate",
            ResourceClaims = scope.ResourceClaims,
            ResourceCapabilities = scope.ResourceCapabilities,
            ResourceReferences = scope.ResourceReferences,
            ScopeClassificationBasis = scope.ScopeClassificationBasis,
        };
    }

    private static async ValueTask<FilePermissionScope> ResolvePathScopeAsync(
        FileExecutionTarget? target,
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

        var resource = await target!.Service.ResolveFileAsync(new()
        { Context = CreateTargetContext(context, actionId, resourceIndex: 0, authorityUseCount), Path = path }, cancellationToken);
        return FilePermissionScope.FromResource(path, resource);
    }

    private static async ValueTask<FilePermissionScope> ResolvePatchScopeAsync(
        FileExecutionTarget? target,
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
        IReadOnlyList<FilePatchPath> affected;
        try
        {
            operations = FilePatchParser.Parse(args.PatchText);
            affected = FilePatchPaths.Affected(operations);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FilePermissionScope.Unknown();
        }

        var paths = affected.Select(path => path.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0)
        {
            return FilePermissionScope.Unknown();
        }
        if (operations.Count > FilePatchParser.MaximumOperations || paths.Length > FilePatchPaths.MaximumPaths)
        {
            return FilePermissionScope.Unknown();
        }

        var hasUnknown = false;
        var hasOutside = false;
        AgentResolvedResource? singleResource = null;
        var resourceReferences = new List<string>();
        var resourceClaims = new List<AgentResourceClaim>();
        var classificationBases = new HashSet<AgentPermissionScopeClassificationBasis>();
        await using var plannedAuthority = new PlannedResourceAuthority(target, MaximumResourceCapabilities);
        for (var resourceIndex = 0; resourceIndex < paths.Length; resourceIndex++)
        {
            var path = paths[resourceIndex];
            try
            {
                var resource = await target.Service.ResolveFileAsync(new()
                {
                    Context = CreateTargetContext(
                        context,
                        actionId,
                        resourceIndex,
                        PatchAuthorityUsesPerPath),
                    Path = path,
                }, cancellationToken);
                plannedAuthority.Add(
                    resource.DeleteAuthorityReferences.Concat(resource.AuthorityReferences));
                classificationBases.Add(resource.ScopeClassificationBasis);
                singleResource = paths.Length == 1 ? resource : null;
                var isDelete = affected
                    .Where(affectedPath => string.Equals(affectedPath.Path, path, StringComparison.Ordinal))
                    .All(affectedPath => affectedPath.IsDelete);
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
                        "outside-configured-scope",
                        StringComparison.OrdinalIgnoreCase)))
                {
                    hasOutside = true;
                }
                else if (operationBoundaries.Any(boundary => !string.Equals(
                             boundary,
                             "configured-scope",
                             StringComparison.OrdinalIgnoreCase)))
                {
                    hasUnknown = true;
                }
            }
            catch (SunderRpcException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return FilePermissionScope.Unknown();
            }
        }

        var boundaryId = hasOutside
            ? "outside-configured-scope"
            : hasUnknown ? "unknown" : "configured-scope";
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

    private static bool CanResolve(FileExecutionTarget? target, AgentToolExecutionContext context, string? path)
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
        return FileExecutionTarget.Context(context.SessionId, context.ProfileId, context.Workspace!, context.ExecutionBinding!) with
        {
            AllowOutsideConfiguredScope = true,
            ResourceOperation = operation,
            ExpectedConfigurationGeneration = context.ExecutionTargetConfigurationGeneration,
        };
    }

    private static int AuthorityUseCount(string toolId)
        => toolId.Equals("edit", StringComparison.OrdinalIgnoreCase) || toolId.Equals("write", StringComparison.OrdinalIgnoreCase) ? 2 : 1;

    private sealed class PlannedResourceAuthority(
        FileExecutionTarget target,
        int maximumCapabilities) : IAsyncDisposable
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

        public async ValueTask DisposeAsync()
        {
            if (_detached || _capabilities.Count == 0)
            {
                return;
            }
            if (FilesExecutionFacets.SupportsFacet(target, AgentExecutionFacetIds.ResourceAuthority))
            {
                await target.Service.ReleaseResourceAuthorityAsync(_capabilities.ToArray(), CancellationToken.None).ConfigureAwait(false);
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
                return FileSearchRequestValidation.TryValidateRequest(
                    new AgentFileSearchRequest { Path = path, Kind = AgentFileSearchKind.Grep, Pattern = grep.Pattern, Include = grep.Include,
                        Literal = grep.Literal, CaseSensitive = grep.CaseSensitive, Limit = grep.Limit, Hidden = false },
                    out _);
            case "glob":
                if (!FileToolArguments.TryParseGlob(request.ArgumentsJson, out var glob, out _))
                {
                    return false;
                }
                path = string.IsNullOrWhiteSpace(glob.Path) ? "." : glob.Path;
                return FileSearchRequestValidation.TryValidateRequest(
                    new AgentFileSearchRequest { Path = path, Kind = AgentFileSearchKind.Glob, Pattern = glob.Pattern, Include = null,
                        Hidden = glob.Hidden, Limit = glob.Limit, Literal = false, CaseSensitive = true },
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
            "unknown",
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
