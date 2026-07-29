using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FilePatchPlanner
{
    public static async Task<FilePatchPlan> PreflightAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        IReadOnlyList<FilePatchOperation> operations,
        CancellationToken cancellationToken)
    {
        if (operations.Count > FilePatchParser.MaximumOperations)
        {
            throw new InvalidOperationException(
                $"Patches support at most {FilePatchParser.MaximumOperations} file operations.");
        }

        var duplicate = operations.GroupBy(operation => operation.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Patch path appears more than once: {duplicate.Key}");
        }

        var planned = new List<PlannedFilePatchOperation>(operations.Count);
        for (var resourceIndex = 0; resourceIndex < operations.Count; resourceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = operations[resourceIndex];
            var resource = await target.ResolveFileResourceAsync(context, operation.Path, cancellationToken);
            planned.Add(operation.Kind switch
            {
                FilePatchOperationKind.Add => PlanAdd(operation, resourceIndex, resource),
                FilePatchOperationKind.Update => await PlanUpdateAsync(target, context, operation, resourceIndex, resource, cancellationToken),
                FilePatchOperationKind.Delete => await PlanDeleteAsync(target, context, operation, resourceIndex, resource, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported patch operation for {operation.Path}."),
            });
        }

        return new FilePatchPlan(planned);
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
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        FilePatchOperation operation,
        int resourceIndex,
        AgentResolvedResource resource,
        CancellationToken cancellationToken)
    {
        var current = await ReadExistingFileAsync(target, context, operation.Path, resource, cancellationToken);
        var mutationResource = await target.ResolveFileResourceAsync(context, operation.Path, cancellationToken);
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
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        FilePatchOperation operation,
        int resourceIndex,
        AgentResolvedResource resource,
        CancellationToken cancellationToken)
    {
        var current = await ReadExistingFileAsync(target, context, operation.Path, resource, cancellationToken);
        var mutationResource = await target.ResolveFileResourceAsync(context, operation.Path, cancellationToken);
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
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        string path,
        AgentResolvedResource resource,
        CancellationToken cancellationToken)
    {
        if (!resource.Exists)
        {
            throw new InvalidOperationException($"Patch target does not exist: {path}");
        }

        var current = await target.ReadFileAsync(context, new AgentFileReadRequest(path), cancellationToken);
        if (current.IsError)
        {
            throw new InvalidOperationException(current.ErrorMessage ?? $"Unable to read patch target: {path}");
        }

        if (current.IsDirectory)
        {
            throw new InvalidOperationException($"Patch target must be a file: {path}");
        }

        return current.Content;
    }

}
