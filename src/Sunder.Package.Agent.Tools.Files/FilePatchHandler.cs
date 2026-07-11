using System.Text;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FilePatchHandler
{
    public static async Task<AgentToolResult> ExecuteAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken)
    {
        if (!FileToolArguments.TryParsePatch(request.ArgumentsJson, out var args, out var error))
        {
            return FileToolResult.Error(request.ToolId, error!, "files-arguments-invalid");
        }

        IReadOnlyList<FilePatchOperation> operations;
        FilePatchPlan plan;
        try
        {
            operations = FilePatchParser.Parse(args.PatchText);
            plan = await FilePatchPlanner.PreflightAsync(target, context, operations, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FileToolResult.Error(request.ToolId, $"Patch preflight failed. No files were changed. {ex.Message}", "patch-preflight-failed");
        }

        var applied = new List<PlannedFilePatchOperation>();
        PlannedFilePatchOperation? currentOperation = null;
        try
        {
            foreach (var operation in plan.Operations)
            {
                currentOperation = operation;
                var result = await ApplyOperationAsync(target, context, operation, cancellationToken);
                if (result.IsError)
                {
                    return await BuildApplicationFailureAsync(target, context, request.ToolId, plan, applied, operation, result.Summary);
                }

                applied.Add(operation);
            }
        }
        catch (OperationCanceledException)
        {
            await CompensateAfterFailureAsync(target, context, applied, currentOperation, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            var failedOperation = plan.Operations[Math.Min(applied.Count, plan.Operations.Count - 1)];
            return await BuildApplicationFailureAsync(target, context, request.ToolId, plan, applied, failedOperation, ex.Message);
        }

        return new AgentToolResult(
            request.ToolId,
            FilePatchParser.BuildSummary(operations),
            Content: string.Join(Environment.NewLine, plan.Operations.Select(FormatSuccessSummary)),
            BackendId: FileToolResult.BackendId(target),
            PresentationPayloadJson: FileDiffPresentation.BuildPayload(plan.Operations.Select(operation => operation.PresentationFile).ToArray()));
    }

    private static async ValueTask<AgentFileMutationResult> ApplyOperationAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        PlannedFilePatchOperation operation,
        CancellationToken cancellationToken)
        => operation.Kind switch
        {
            FilePatchOperationKind.Add => await FileWriteHandler.WriteAsync(target, context, operation.Path, operation.NextContent!, overwrite: false, cancellationToken),
            FilePatchOperationKind.Update => await FileWriteHandler.WriteAsync(target, context, operation.Path, operation.NextContent!, overwrite: true, cancellationToken, operation.ExpectedContentHash),
            FilePatchOperationKind.Delete => await FileDeleteHandler.DeleteAsync(target, context, operation.Path, cancellationToken, operation.ExpectedContentHash),
            _ => throw new InvalidOperationException($"Unsupported patch operation for {operation.Path}."),
        };

    private static async Task<AgentToolResult> BuildApplicationFailureAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        string toolId,
        FilePatchPlan plan,
        IReadOnlyList<PlannedFilePatchOperation> applied,
        PlannedFilePatchOperation failed,
        string failure)
    {
        var compensationTargets = await IncludeFailedOperationIfMutatedAsync(target, context, applied, failed, CancellationToken.None);
        if (compensationTargets.Count == 0)
        {
            return FileToolResult.Error(
                toolId,
                $"Patch application failed at '{failed.Path}' before any operation completed. Preflight made no changes. {failure}",
                "patch-apply-failed");
        }

        var compensation = await CompensateAsync(target, context, compensationTargets, CancellationToken.None);
        var message = new StringBuilder()
            .Append("Patch was partially applied: ")
            .Append(applied.Count)
            .Append(" of ")
            .Append(plan.Operations.Count)
            .Append(" operations completed before '")
            .Append(failed.Path)
            .Append("' failed. ")
            .Append(failure.Trim())
            .AppendLine()
            .Append("Compensation restored ")
            .Append(FileToolResult.FormatCount(compensation.RestoredCount, "operation"))
            .Append('.');
        if (compensation.FullyRestored)
        {
            message.Append(" No applied patch changes remain.");
        }
        else
        {
            message.Append(" The target may remain partially modified. Compensation failures: ")
                .Append(string.Join("; ", compensation.Failures));
        }

        return FileToolResult.Error(toolId, message.ToString(), "patch-partial-application");
    }

    private static async Task<FilePatchCompensationResult> CompensateAfterFailureAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        IReadOnlyList<PlannedFilePatchOperation> applied,
        PlannedFilePatchOperation? failed,
        CancellationToken cancellationToken)
    {
        var targets = failed is null
            ? applied
            : await IncludeFailedOperationIfMutatedAsync(target, context, applied, failed, cancellationToken);
        return await CompensateAsync(target, context, targets, cancellationToken);
    }

    private static async Task<IReadOnlyList<PlannedFilePatchOperation>> IncludeFailedOperationIfMutatedAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        IReadOnlyList<PlannedFilePatchOperation> applied,
        PlannedFilePatchOperation failed,
        CancellationToken cancellationToken)
    {
        if (applied.Contains(failed) || !await FailedOperationReachedNextStateAsync(target, context, failed, cancellationToken))
        {
            return applied;
        }

        return [.. applied, failed];
    }

    private static async Task<bool> FailedOperationReachedNextStateAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        PlannedFilePatchOperation operation,
        CancellationToken cancellationToken)
    {
        var resource = await target.ResolveFileResourceAsync(context, operation.Path, cancellationToken);
        if (operation.Kind == FilePatchOperationKind.Delete)
        {
            return !resource.Exists;
        }

        return resource.Exists
               && await CurrentContentMatchesAsync(target, context, operation.Path, operation.NextContent!, cancellationToken);
    }

    private static async Task<FilePatchCompensationResult> CompensateAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        IReadOnlyList<PlannedFilePatchOperation> applied,
        CancellationToken cancellationToken)
    {
        var restored = 0;
        var failures = new List<string>();
        foreach (var operation in applied.Reverse())
        {
            try
            {
                var result = await CompensateOperationAsync(target, context, operation, cancellationToken);
                if (result.IsError)
                {
                    failures.Add($"{operation.Path}: {result.Summary}");
                }
                else
                {
                    restored++;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{operation.Path}: {ex.Message}");
            }
        }

        return new FilePatchCompensationResult(restored, failures);
    }

    private static async Task<AgentFileMutationResult> CompensateOperationAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        PlannedFilePatchOperation operation,
        CancellationToken cancellationToken)
    {
        switch (operation.Kind)
        {
            case FilePatchOperationKind.Add:
                return await FileDeleteHandler.DeleteAsync(
                    target,
                    context,
                    operation.Path,
                    cancellationToken,
                    FilePatchPlanner.ComputeContentHash(operation.NextContent!));

            case FilePatchOperationKind.Update:
                return await FileWriteHandler.WriteAsync(
                    target,
                    context,
                    operation.Path,
                    operation.OriginalContent!,
                    overwrite: true,
                    cancellationToken,
                    FilePatchPlanner.ComputeContentHash(operation.NextContent!));

            case FilePatchOperationKind.Delete:
                return await FileWriteHandler.WriteAsync(target, context, operation.Path, operation.OriginalContent!, overwrite: false, cancellationToken);

            default:
                return UnsafeCompensation(operation.Path);
        }
    }

    private static async Task<bool> CurrentContentMatchesAsync(
        IAgentExecutionTarget target,
        AgentExecutionTargetContext context,
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        var current = await target.ReadFileAsync(context, new AgentFileReadRequest(path), cancellationToken);
        return !current.IsError && !current.IsDirectory && string.Equals(current.Content, expected, StringComparison.Ordinal);
    }

    private static AgentFileMutationResult UnsafeCompensation(string path)
        => new(path, "Current target state no longer matches the applied patch; automatic compensation was skipped.", IsError: true, ErrorCode: "patch-compensation-unsafe");

    private static string FormatSuccessSummary(PlannedFilePatchOperation operation)
        => operation.Kind switch
        {
            FilePatchOperationKind.Add => $"Added {operation.Path}",
            FilePatchOperationKind.Update => $"Updated {operation.Path}",
            FilePatchOperationKind.Delete => $"Deleted {operation.Path}",
            _ => operation.Path,
        };
}
