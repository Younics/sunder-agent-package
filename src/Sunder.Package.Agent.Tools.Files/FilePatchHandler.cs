using Sunder.Agent.Execution.Common;
using System.Text;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Tools.Files;

internal static class FilePatchHandler
{
    public static async Task<AgentToolResult> ExecuteAsync(
        FileExecutionTarget target,
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

        try
        {
            var result = await ApplyPlanAsync(target, context, request.ToolId, operations, plan, cancellationToken);
            return !result.IsError || !plan.Operations.Any(operation => operation.Move is not null)
                ? result
                : result with { Content = result.Content + Environment.NewLine
                    + "Move steps are non-atomic: create the destination, then remove the source. Inspect reported source/destination recovery state before replanning; do not blindly retry." };
        }
        finally
        {
            await ReleasePostMutationAuthorityAsync(target, plan).ConfigureAwait(false);
        }
    }

    private static async Task<AgentToolResult> ApplyPlanAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        string toolId,
        IReadOnlyList<FilePatchOperation> operations,
        FilePatchPlan plan,
        CancellationToken cancellationToken)
    {
        var applied = new List<PlannedFilePatchOperation>();
        PlannedFilePatchOperation? currentOperation = null;
        try
        {
            foreach (var operation in plan.Operations)
            {
                currentOperation = operation;
                var result = await ApplyOperationAsync(target, context, operation, cancellationToken);
                operation.PostMutationResource = result.PostMutationResource;
                operation.RejectedWithoutMutation = result.IsError && result.PostMutationResource is null
                    && result.ErrorCode is FileOperation.FileExistsErrorCode or FileOperation.ContentChangedErrorCode;
                if (result.IsError)
                {
                    return await BuildApplicationFailureAsync(target, context, toolId, plan, applied, operation, result.Summary);
                }

                applied.Add(operation);
            }
        }
        catch (OperationCanceledException)
        {
            var compensation = await CompensateAfterFailureAsync(
                target,
                context,
                applied,
                currentOperation,
                CancellationToken.None,
                requireOriginalStateForNoMutation: true);
            if (CompensationIsConfirmedComplete(compensation))
            {
                throw;
            }

            return BuildCancellationFailure(toolId, plan, applied, currentOperation, compensation);
        }
        catch (SunderRpcException ex) when (ex.Error.DispatchState == SunderRpcDispatchState.PossiblyDispatched
            || ex.Error.RetrySafety == SunderRpcRetrySafety.Unsafe)
        {
            // Preserve transport uncertainty even when best-effort compensation succeeds.
            await CompensateAfterFailureAsync(
                target,
                context,
                applied,
                currentOperation,
                CancellationToken.None,
                requireOriginalStateForNoMutation: true);
            throw;
        }
        catch (Exception ex)
        {
            var failedOperation = plan.Operations[Math.Min(applied.Count, plan.Operations.Count - 1)];
            return await BuildApplicationFailureAsync(target, context, toolId, plan, applied, failedOperation, ex.Message);
        }

        return FileToolResult.Success(
            toolId,
            FilePatchParser.BuildSummary(operations),
            string.Join(Environment.NewLine, plan.DisplayOperations.Select(FormatSuccessSummary)), FileToolResult.BackendId(target),
            presentationPayloadJson: FileDiffPresentation.BuildPayload(plan.DisplayOperations.Select(operation => operation.PresentationFile).ToArray()));
    }

    private static async ValueTask<AgentFileMutationResult> ApplyOperationAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        PlannedFilePatchOperation operation,
        CancellationToken cancellationToken)
    {
        var operationContext = context with
        {
            ApprovedResourceReferences = [operation.ResourceReference],
            ResourceOperation = context.ResourceOperation is { } resourceOperation
                ? resourceOperation with { ResourceIndex = operation.ResourceIndex }
                : null,
            CapturePostMutationResource = true,
        };
        return operation.Kind switch
        {
            FilePatchOperationKind.Add => await FileWriteHandler.WriteAsync(target, operationContext, operation.Path, operation.NextContent!, overwrite: false, cancellationToken),
            FilePatchOperationKind.Update => await FileWriteHandler.WriteAsync(target, operationContext, operation.Path, operation.NextContent!, overwrite: true, cancellationToken, operation.ExpectedContentHash),
            FilePatchOperationKind.Delete => await FileDeleteHandler.DeleteAsync(target, operationContext, operation.Path, cancellationToken, operation.ExpectedContentHash),
            _ => throw new InvalidOperationException($"Unsupported patch operation for {operation.Path}."),
        };
    }

    private static async Task<AgentToolResult> BuildApplicationFailureAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        string toolId,
        FilePatchPlan plan,
        IReadOnlyList<PlannedFilePatchOperation> applied,
        PlannedFilePatchOperation failed,
        string failure)
    {
        var compensation = await CompensateAfterFailureAsync(
            target,
            context,
            applied,
            failed,
            CancellationToken.None,
            requireOriginalStateForNoMutation: false);
        if (compensation.AttemptedCount == 0 && compensation.ProbeFailure is null)
        {
            return FileToolResult.Error(
                toolId,
                $"Patch application failed at '{failed.Path}' before any operation completed. Preflight made no changes. {failure}",
                "patch-apply-failed");
        }

        var message = new StringBuilder();
        if (applied.Count == 0)
        {
            message.Append("Patch application failed at '")
                .Append(failed.Path)
                .Append("' before any operation reported completion. ");
        }
        else
        {
            message.Append("Patch was partially applied: ")
                .Append(applied.Count)
                .Append(" of ")
                .Append(plan.Operations.Count)
                .Append(" operations completed before '")
                .Append(failed.Path)
                .Append("' failed. ");
        }
        message.Append(failure.Trim());
        AppendCompensationDiagnostics(message, compensation);

        if (compensation.Result.FullyRestored && compensation.ProbeFailure is null)
        {
            message.Append(" No applied patch changes remain.");
        }
        else
        {
            message.Append(" The target may remain partially modified.");
        }

        return FileToolResult.Error(toolId, message.ToString(), "patch-partial-application");
    }

    private static async Task<FilePatchCompensationAttempt> CompensateAfterFailureAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        IReadOnlyList<PlannedFilePatchOperation> applied,
        PlannedFilePatchOperation? failed,
        CancellationToken cancellationToken,
        bool requireOriginalStateForNoMutation)
    {
        IReadOnlyList<PlannedFilePatchOperation> targets = applied;
        FilePatchCompensationProbeFailure? probeFailure = null;
        if (failed is not null && !applied.Contains(failed))
        {
            try
            {
                if (await FailedOperationReachedNextStateAsync(
                        target,
                        context,
                        failed,
                        cancellationToken,
                        requireOriginalStateForNoMutation))
                {
                    targets = [.. applied, failed];
                }
            }
            catch (Exception exception)
            {
                probeFailure = new FilePatchCompensationProbeFailure(failed.Path, exception.Message);
            }
        }

        var result = await CompensateAsync(target, context, targets, cancellationToken);
        return new FilePatchCompensationAttempt(targets.Count, result, probeFailure);
    }

    private static async Task<bool> FailedOperationReachedNextStateAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        PlannedFilePatchOperation operation,
        CancellationToken cancellationToken,
        bool requireOriginalStateForNoMutation)
    {
        if (operation.RejectedWithoutMutation) return false;
        if (operation.PostMutationResource is { } postMutationResource)
        {
            var reachedNextState = operation.Kind == FilePatchOperationKind.Delete
                ? !postMutationResource.Exists
                : postMutationResource.Exists;
            return !reachedNextState && requireOriginalStateForNoMutation
                ? throw UncertainCanceledOperationState(operation.Path)
                : reachedNextState;
        }

        var resource = await target.Service.ResolveFileAsync(new() { Context = context, Path = operation.Path }, cancellationToken);
        var readContext = context with
        {
            ApprovedResourceReferences = [resource.CanonicalReference],
        };
        if (operation.Kind == FilePatchOperationKind.Delete)
        {
            if (!resource.Exists)
            {
                if (operation.Move is not null)
                    throw new InvalidOperationException($"Move source '{operation.Path}' removal ownership could not be confirmed; the destination was retained for recovery.");
                return true;
            }
            if (!requireOriginalStateForNoMutation)
            {
                return false;
            }

            var current = await ReadCurrentContentAsync(
                target,
                readContext,
                operation.Path,
                cancellationToken,
                requireCompleteRead: true);
            if (string.Equals(current, operation.OriginalContent, StringComparison.Ordinal))
            {
                return false;
            }
            return requireOriginalStateForNoMutation
                ? throw UncertainCanceledOperationState(operation.Path)
                : false;
        }

        if (!resource.Exists)
        {
            if (operation.Kind == FilePatchOperationKind.Add)
            {
                return false;
            }
            return requireOriginalStateForNoMutation
                ? throw UncertainCanceledOperationState(operation.Path)
                : false;
        }

        // A destination created by somebody else can have identical text. A
        // failed/unacknowledged create is not proof that this call owns it.
        if (operation.Move is not null && operation.Kind == FilePatchOperationKind.Add)
            throw new InvalidOperationException($"Move destination '{operation.Path}' ownership could not be confirmed; any destination was retained for recovery.");

        var currentContent = await ReadCurrentContentAsync(
            target,
            readContext,
            operation.Path,
            cancellationToken,
            requireCompleteRead: requireOriginalStateForNoMutation);
        if (string.Equals(currentContent, operation.NextContent, StringComparison.Ordinal))
        {
            return true;
        }
        if (operation.Kind == FilePatchOperationKind.Update
            && string.Equals(currentContent, operation.OriginalContent, StringComparison.Ordinal))
        {
            return false;
        }
        return requireOriginalStateForNoMutation
            ? throw UncertainCanceledOperationState(operation.Path)
            : false;
    }

    private static async Task<FilePatchCompensationResult> CompensateAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        IReadOnlyList<PlannedFilePatchOperation> applied,
        CancellationToken cancellationToken)
    {
        var restored = new List<string>();
        var failures = new List<FilePatchCompensationFailure>();
        foreach (var operation in applied.Reverse())
        {
            try
            {
                var result = await CompensateOperationAsync(target, context, operation, cancellationToken);
                if (result.IsError)
                {
                    failures.Add(new FilePatchCompensationFailure(operation.Path, result.Summary));
                }
                else
                {
                    if (operation.Move is { } move && operation.Kind == FilePatchOperationKind.Delete)
                        move.SourceRestored = true;
                    restored.Add(operation.Path);
                }
            }
            catch (Exception ex)
            {
                failures.Add(new FilePatchCompensationFailure(operation.Path, ex.Message));
            }
        }

        return new FilePatchCompensationResult(restored, failures);
    }

    private static async Task<AgentFileMutationResult> CompensateOperationAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        PlannedFilePatchOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation.Move is { } move && operation.Kind == FilePatchOperationKind.Add && !move.SourceRestored)
        {
            var sourceContext = context with
            {
                ApprovedResourceReferences = [move.SourceResourceReference],
                ResourceOperation = context.ResourceOperation is { } sourceOperation
                    ? sourceOperation with { ResourceIndex = move.SourceResourceIndex } : null,
            };
            try
            {
                var sourceContent = await ReadCurrentContentAsync(target, sourceContext, move.SourcePath,
                    cancellationToken, requireCompleteRead: true);
                if (!string.Equals(sourceContent, move.OriginalContent, StringComparison.Ordinal))
                    return RetainMoveDestination(move, "the source no longer matches its preflight snapshot");
            }
            catch (Exception exception)
            {
                return RetainMoveDestination(move, "the source could not be verified: " + exception.Message);
            }
        }
        var resource = operation.PostMutationResource
                       ?? await target.Service.ResolveFileAsync(new() { Context = context, Path = operation.Path }, cancellationToken);
        var operationContext = CreateCompensationContext(context, operation, resource);
        switch (operation.Kind)
        {
            case FilePatchOperationKind.Add:
                return await FileDeleteHandler.DeleteAsync(
                    target,
                    operationContext,
                    operation.Path,
                    cancellationToken,
                    FileOperation.ComputeContentHash(operation.NextContent!));

            case FilePatchOperationKind.Update:
                return await FileWriteHandler.WriteAsync(
                    target,
                    operationContext,
                    operation.Path,
                    operation.OriginalContent!,
                    overwrite: true,
                    cancellationToken,
                    FileOperation.ComputeContentHash(operation.NextContent!));

            case FilePatchOperationKind.Delete:
                return await FileWriteHandler.WriteAsync(target, operationContext, operation.Path, operation.OriginalContent!, overwrite: false, cancellationToken);

            default:
                return UnsafeCompensation(operation.Path);
        }
    }

    private static AgentExecutionTargetContext CreateCompensationContext(
        AgentExecutionTargetContext context,
        PlannedFilePatchOperation operation,
        AgentResolvedResource resource)
    {
        var deleteCompensation = operation.Kind == FilePatchOperationKind.Add;
        var resourceReference = deleteCompensation
            ? resource.DeleteCanonicalReference ?? resource.CanonicalReference
            : resource.CanonicalReference;
        var resourceClaim = deleteCompensation
            ? resource.DeleteResourceClaim ?? resource.ResourceClaim
            : resource.ResourceClaim;
        var capabilities = deleteCompensation
            ? resource.DeleteAuthorityReferences.Concat(resource.AuthorityReferences)
            : resource.AuthorityReferences;
        return context with
        {
            ApprovedResourceReferences = [resourceReference],
            ApprovedResourceClaims = resourceClaim is null ? [] : [resourceClaim],
            ApprovedResourceCapabilities = capabilities.Distinct(StringComparer.Ordinal).ToArray(),
            ResourceOperation = context.ResourceOperation is { } resourceOperation
                ? resourceOperation with { ResourceIndex = operation.ResourceIndex }
                : null,
            CapturePostMutationResource = false,
        };
    }

    private static async ValueTask ReleasePostMutationAuthorityAsync(
        FileExecutionTarget target,
        FilePatchPlan plan)
    {
        if (!FilesExecutionFacets.SupportsFacet(target, AgentExecutionFacetIds.ResourceAuthority))
        {
            return;
        }

        var capabilities = plan.Operations
            .Select(static operation => operation.PostMutationResource)
            .Where(static resource => resource is not null)
            .SelectMany(static resource => resource!.AuthorityReferences.Concat(resource.DeleteAuthorityReferences))
            .Where(static capability => !string.IsNullOrWhiteSpace(capability))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (capabilities.Length > 0)
        {
            if (target.Reference.RetirementToken.IsCancellationRequested)
            {
                return;
            }
            try
            {
                await target.Service.ReleaseResourceAuthorityAsync(capabilities, CancellationToken.None).ConfigureAwait(false);
            }
            catch (SunderRpcException exception) when (exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                // Retired authority is revoked; retain the original patch failure and its dispatch metadata.
            }
            catch (OperationCanceledException) when (target.Reference.RetirementToken.IsCancellationRequested)
            {
                // The exact activation's authority is already revoked.
            }
        }
    }

    private static async Task<string?> ReadCurrentContentAsync(
        FileExecutionTarget target,
        AgentExecutionTargetContext context,
        string path,
        CancellationToken cancellationToken,
        bool requireCompleteRead)
    {
        var current = await target.Service.ReadFileAsync(new() { Context = context, Request = new() { Path = path, Offset = null, Limit = null } }, cancellationToken);
        if (current.IsError || current.IsDirectory || current.WasTruncated)
        {
            if (!requireCompleteRead)
            {
                return null;
            }
            throw new InvalidOperationException(
                $"The current state of '{path}' could not be verified after the interrupted mutation. "
                + (current.ErrorMessage ?? "A complete regular-file read was unavailable."));
        }
        return current.Content;
    }

    private static InvalidOperationException UncertainCanceledOperationState(string path)
        => new($"The current state of '{path}' matches neither the preflight state nor the planned patch state.");

    private static AgentFileMutationResult UnsafeCompensation(string path)
        => new() { Path = path, Summary = "Current target state no longer matches the applied patch; automatic compensation was skipped.",
            IsError = true, ErrorCode = "patch-compensation-unsafe", PostMutationResource = null };

    private static AgentFileMutationResult RetainMoveDestination(FilePatchMove move, string reason)
        => new() { Path = move.DestinationPath, IsError = true, ErrorCode = "patch-compensation-unsafe", PostMutationResource = null,
            Summary = $"Move destination '{move.DestinationPath}' retained for recovery because {reason}; source '{move.SourcePath}' was not confirmed restored." };

    private static AgentToolResult BuildCancellationFailure(
        string toolId,
        FilePatchPlan plan,
        IReadOnlyList<PlannedFilePatchOperation> applied,
        PlannedFilePatchOperation? currentOperation,
        FilePatchCompensationAttempt compensation)
    {
        var message = new StringBuilder()
            .Append("Patch execution was canceled after ")
            .Append(applied.Count)
            .Append(" of ")
            .Append(plan.Operations.Count)
            .Append(" operations reported completion");
        if (currentOperation is not null)
        {
            message.Append(" while applying '")
                .Append(currentOperation.Path)
                .Append('\'');
        }
        message.Append('.');
        AppendCompensationDiagnostics(message, compensation);
        message.Append(" The target may remain partially modified.");
        return FileToolResult.Error(toolId, message.ToString(), "patch-partial-application");
    }

    private static bool CompensationIsConfirmedComplete(FilePatchCompensationAttempt compensation)
        => compensation.ProbeFailure is null
           && compensation.Result.FullyRestored
           && compensation.Result.RestoredCount == compensation.AttemptedCount;

    private static void AppendCompensationDiagnostics(
        StringBuilder message,
        FilePatchCompensationAttempt compensation)
    {
        message.AppendLine()
            .Append("Compensation restored ")
            .Append(FileToolResult.FormatCount(compensation.Result.RestoredCount, "operation"))
            .Append(". Completed rollbacks: ")
            .Append(FormatRollbackPaths(compensation.Result.RestoredPaths))
            .Append(". Failed rollbacks: ")
            .Append(compensation.Result.Failures.Count == 0
                ? "none"
                : string.Join(
                    "; ",
                    compensation.Result.Failures.Select(static failure => $"'{failure.Path}': {failure.Failure}")))
            .Append('.');
        if (compensation.ProbeFailure is { } probeFailure)
        {
            message.Append(" The operation '")
                .Append(probeFailure.Path)
                .Append("' could not be verified for compensation because diagnostic probing was unavailable: ")
                .Append(probeFailure.Failure)
                .Append('.');
        }
    }

    private static string FormatRollbackPaths(IReadOnlyList<string> paths)
        => paths.Count == 0
            ? "none"
            : string.Join(", ", paths.Select(static path => $"'{path}'"));

    private sealed record FilePatchCompensationAttempt(
        int AttemptedCount,
        FilePatchCompensationResult Result,
        FilePatchCompensationProbeFailure? ProbeFailure);

    private static string FormatSuccessSummary(PlannedFilePatchOperation operation)
        => operation.Move is { } move
            ? $"Moved {move.SourcePath} to {move.DestinationPath} (non-atomic write/delete; file identity and metadata are not guaranteed to be preserved)"
            : operation.Kind switch
        {
            FilePatchOperationKind.Add => $"Added {operation.Path}",
            FilePatchOperationKind.Update => $"Updated {operation.Path}",
            FilePatchOperationKind.Delete => $"Deleted {operation.Path}",
            _ => operation.Path,
        };
}
