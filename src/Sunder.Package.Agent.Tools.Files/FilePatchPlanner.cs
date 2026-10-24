using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FilePatchPlanner
{
    public static async Task<FilePatchPlan> PreflightAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        IReadOnlyList<FilePatchOperation> operations,
        CancellationToken cancellationToken)
    {
        if (operations.Count > FilePatchParser.MaximumOperations)
        {
            throw new InvalidOperationException(
                $"Patches support at most {FilePatchParser.MaximumOperations} file operations.");
        }

        var paths = FilePatchPaths.Affected(operations);
        var duplicate = paths.GroupBy(path => path.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Patch path appears more than once: {duplicate.Key}");
        }
        var resources = new Dictionary<string, (int Index, AgentResolvedResource Resource)>(StringComparer.Ordinal);
        var canonicalPaths = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < paths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = paths[index].Path;
            var resource = await target.Service.ResolveFileAsync(new() { Context = ForResource(context, index), Path = path }, cancellationToken);
            var canonical = resource.ResourceClaim is { } claim
                ? claim.NamespaceId + "\0" + claim.LogicalPath
                : resource.CanonicalReference;
            if (!canonicalPaths.Add(canonical))
                throw new InvalidOperationException($"Patch paths resolve to the same target: {path}");
            resources.Add(path, (index, resource));
        }

        var planned = new List<PlannedFilePatchOperation>(paths.Count);
        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (resourceIndex, resource) = resources[operation.Path];
            var operationContext = ForResource(context, resourceIndex);
            if (operation.MoveTo is { } destination)
            {
                var (destinationIndex, destinationResource) = resources[destination];
                planned.AddRange(await PlanMoveAsync(target, operationContext, operation, resourceIndex, resource,
                    destinationIndex, destinationResource, cancellationToken));
                continue;
            }
            planned.Add(operation.Kind switch
            {
                FilePatchOperationKind.Add => PlanAdd(operation, resourceIndex, resource),
                FilePatchOperationKind.Update => await PlanUpdateAsync(target, operationContext, operation, resourceIndex, resource, cancellationToken),
                FilePatchOperationKind.Delete => await PlanDeleteAsync(target, operationContext, operation, resourceIndex, resource, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported patch operation for {operation.Path}."),
            });
        }

        return new FilePatchPlan(planned);
    }

    private static AgentExecutionTargetContext ForResource(AgentExecutionTargetContext context, int index)
        => context with { ResourceOperation = context.ResourceOperation is { } operation ? operation with { ResourceIndex = index } : null };

    private static async Task<IReadOnlyList<PlannedFilePatchOperation>> PlanMoveAsync(
        FileExecutionTarget target, AgentExecutionTargetContext context, FilePatchOperation operation,
        int sourceIndex, AgentResolvedResource source, int destinationIndex, AgentResolvedResource destination,
        CancellationToken cancellationToken)
    {
        if (destination.Exists)
            throw new InvalidOperationException($"Move destination already exists: {operation.MoveTo}");
        var current = await ReadExistingFileAsync(target, context, operation.Path, source, cancellationToken);
        var next = FileDiffPresentation.ApplyHunks(current, operation.Hunks, out var lines, allowUnchanged: true);
        var mutationSource = await target.Service.ResolveFileAsync(new() { Context = context, Path = operation.Path }, cancellationToken);
        var move = new FilePatchMove(operation.Path, operation.MoveTo!, sourceIndex, mutationSource.CanonicalReference, current);
        // OpenCode-style patch move, not a native rename: publish the destination
        // create-only, then remove the exact preflight source with a content CAS.
        return
        [
            new(FilePatchOperationKind.Add, operation.MoveTo!, destinationIndex, null, next, null,
                destination.CanonicalReference, FileDiffPresentation.UpdatedFile(operation.MoveTo!,
                    [new("collapsed", null, $"Moved from {operation.Path} (non-atomic write/delete)"), .. lines])) { Move = move },
            new(FilePatchOperationKind.Delete, operation.Path, sourceIndex, current, null, FileOperation.ComputeContentHash(current),
                mutationSource.DeleteCanonicalReference ?? mutationSource.CanonicalReference, FileDiffPresentation.DeletedFile(operation.Path)) { Move = move },
        ];
    }

    private static PlannedFilePatchOperation PlanAdd(
        FilePatchOperation operation,
        int resourceIndex,
        AgentResolvedResource resource)
    {
        if (resource.Exists)
        {
            throw new InvalidOperationException($"Cannot add '{operation.Path}' because the path already exists.");
        }

        var content = operation.Content ?? string.Empty;
        return new PlannedFilePatchOperation(
            operation.Kind,
            operation.Path,
            resourceIndex,
            OriginalContent: null,
            NextContent: content,
            ExpectedContentHash: null,
            resource.CanonicalReference,
            FileDiffPresentation.AddedFile(operation.Path, content));
    }

    private static async Task<PlannedFilePatchOperation> PlanUpdateAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        FilePatchOperation operation,
        int resourceIndex,
        AgentResolvedResource resource,
        CancellationToken cancellationToken)
    {
        var current = await ReadExistingFileAsync(target, context, operation.Path, resource, cancellationToken);
        var mutationResource = await target.Service.ResolveFileAsync(new() { Context = context, Path = operation.Path }, cancellationToken);
        var next = FileDiffPresentation.ApplyHunks(current, operation.Hunks, out var diffLines);
        return new PlannedFilePatchOperation(
            operation.Kind,
            operation.Path,
            resourceIndex,
            current,
            next,
            FileOperation.ComputeContentHash(current),
            mutationResource.CanonicalReference,
            FileDiffPresentation.UpdatedFile(operation.Path, diffLines));
    }

    private static async Task<PlannedFilePatchOperation> PlanDeleteAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        FilePatchOperation operation,
        int resourceIndex,
        AgentResolvedResource resource,
        CancellationToken cancellationToken)
    {
        var current = await ReadExistingFileAsync(target, context, operation.Path, resource, cancellationToken);
        var mutationResource = await target.Service.ResolveFileAsync(new() { Context = context, Path = operation.Path }, cancellationToken);
        return new PlannedFilePatchOperation(
            operation.Kind,
            operation.Path,
            resourceIndex,
            current,
            NextContent: null,
            FileOperation.ComputeContentHash(current),
            mutationResource.CanonicalReference,
            FileDiffPresentation.DeletedFile(operation.Path));
    }

    private static async Task<string> ReadExistingFileAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        string path,
        AgentResolvedResource resource,
        CancellationToken cancellationToken)
    {
        if (!resource.Exists)
        {
            throw new InvalidOperationException($"Patch target does not exist: {path}");
        }

        var current = await target.Service.ReadFileAsync(new() { Context = context, Request = new() { Path = path, Offset = null, Limit = null } }, cancellationToken);
        if (current.IsError)
        {
            throw new InvalidOperationException(current.ErrorMessage ?? $"Unable to read patch target: {path}");
        }

        if (current.IsDirectory)
        {
            throw new InvalidOperationException($"Patch target must be a file: {path}");
        }
        if (current.WasTruncated)
            throw new InvalidOperationException($"Patch preflight requires a complete file snapshot: {path}");

        return current.Content;
    }

}
