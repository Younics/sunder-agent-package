using System.Diagnostics;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed partial class AgentBehaviorLoopHost
{
    internal async Task<IReadOnlyList<ExecutedToolResult>> ExecuteToolBatchAsync(
        IReadOnlyList<AgentToolCallRequest> batch,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        CancellationToken cancellationToken)
    {
        var inFlight = new Dictionary<string, Task<ResolvedToolResult>>(StringComparer.Ordinal);
        var tasks = batch.Select(call => ExecuteToolCallForBatchAsync(
            call,
            availableToolsById,
            inFlight,
            cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private async Task<ExecutedToolResult> ExecuteToolCallForBatchAsync(
        AgentToolCallRequest toolCall,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        IDictionary<string, Task<ResolvedToolResult>> inFlight,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var resolved = await ResolveBatchToolResultAsync(toolCall, availableToolsById, inFlight, cancellationToken);
        return new ExecutedToolResult(
            toolCall,
            resolved.Result,
            stopwatch.ElapsedMilliseconds,
            resolved.WasReadOnlyCacheHit);
    }

    private async Task<ResolvedToolResult> ResolveBatchToolResultAsync(
        AgentToolCallRequest toolCall,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        IDictionary<string, Task<ResolvedToolResult>> inFlight,
        CancellationToken cancellationToken)
    {
        if (!IsCacheableReadOnlyTool(toolCall.ToolId, availableToolsById)
            || HasTransientResourceCapabilities(toolCall.CallId))
        {
            return await ResolveToolResultAsync(toolCall, availableToolsById, cancellationToken);
        }

        var key = BuildToolCallCacheKey(toolCall);
        if (!inFlight.TryGetValue(key, out var primaryTask))
        {
            primaryTask = ResolveToolResultAsync(toolCall, availableToolsById, cancellationToken);
            inFlight[key] = primaryTask;
            return await primaryTask;
        }

        var primary = await primaryTask;
        return primary.Result.IsError
            ? await ResolveToolResultAsync(toolCall, availableToolsById, cancellationToken)
            : new ResolvedToolResult(
                CreateDuplicateReadOnlyToolResult(toolCall.ToolId, primary.Result),
                WasReadOnlyCacheHit: true);
    }

    internal async Task<AgentToolCallOutcome> RecordExecutedToolResultAsync(
        ExecutedToolResult executed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentRun())
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var call = executed.ToolCall;
        var result = executed.Result;
        if (AgentToolSuspensionCompatibility.TryCreateChildJoin(call, result, _userTurnId, out var childJoin))
        {
            var suspended = _sessionService.SuspendRunAndCompleteToolExecution(
                _runLease,
                childJoin!,
                result.Summary,
                GetToolExecution(call.CallId).ExecutionId,
                result,
                "child-suspension-durable");
            return new AgentToolCallOutcome(AgentToolCallOutcomeKind.WaitingForApproval, suspended.Checkpoint, result);
        }

        if (string.Equals(result.ErrorCode, AgentToolResultErrorCodes.ChildWaitingForApproval, StringComparison.OrdinalIgnoreCase))
        {
            CompleteToolExecution(
                call,
                result,
                AgentToolExecutionStatus.Failed,
                AgentToolResultErrorCodes.ChildWaitingForApproval);
            var checkpoint = SaveCheckpoint(AgentRunStatus.Failed, "A waiting child task did not provide a durable child-session identity.");
            return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Failed, checkpoint, result);
        }

        LogEvent(
            result.IsError ? PackageLogLevel.Error : PackageLogLevel.Information,
            result.IsError ? "tool.execution.failed" : "tool.execution.completed",
            result.Summary,
            executed.ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool.id"] = call.ToolId,
                ["tool.backend_id"] = result.BackendId,
                ["tool.was_truncated"] = result.WasTruncated,
                ["tool.is_error"] = result.IsError,
                ["tool.error_code"] = result.ErrorCode,
                ["tool.content_length"] = result.Content?.Length ?? 0,
            });
        var resultTurn = CompleteToolExecution(
            call,
            result,
            result.IsError ? AgentToolExecutionStatus.Failed : AgentToolExecutionStatus.Completed,
            executed.WasReadOnlyCacheHit
                ? "read-only-cache-hit"
                : result.ErrorCode ?? (result.IsError ? "tool-failed" : "tool-completed"),
            executed.WasReadOnlyCacheHit);
        await PublishLifecycleEventAsync(
            AgentLifecycleEventKind.ToolResultRecorded,
            AgentRunStatus.Running,
            triggerTurn: resultTurn,
            cancellationToken: cancellationToken);
        SaveCheckpoint(
            AgentRunStatus.Running,
            result.IsError
                ? $"Tool '{call.ToolId}' returned an error result. Continuing provider execution."
                : $"Tool '{call.ToolId}' completed. Continuing provider execution.");
        return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Executed, Result: result);
    }

    internal static bool RequiresTerminalRecordingLast(ExecutedToolResult executed)
        => string.Equals(executed.Result.ErrorCode, AgentToolResultErrorCodes.ChildWaitingForApproval, StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<IReadOnlyList<AgentToolCallRequest>> BuildToolExecutionBatches(
        IReadOnlyList<AgentToolCallRequest> calls,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById)
    {
        var batches = new List<IReadOnlyList<AgentToolCallRequest>>();
        var parallel = new List<AgentToolCallRequest>();
        foreach (var call in calls)
        {
            var isParallelSafe = availableToolsById.TryGetValue(call.ToolId, out var descriptor)
                                 && descriptor.ConcurrencyMode == AgentToolConcurrencyMode.ParallelSafe;
            if (isParallelSafe)
            {
                parallel.Add(call);
                if (parallel.Count >= MaxParallelToolExecutions)
                {
                    batches.Add(parallel.ToArray());
                    parallel.Clear();
                }
                continue;
            }

            if (parallel.Count > 0)
            {
                batches.Add(parallel.ToArray());
                parallel.Clear();
            }
            batches.Add([call]);
        }

        if (parallel.Count > 0)
        {
            batches.Add(parallel.ToArray());
        }
        return batches;
    }

    private async Task<ResolvedToolResult> ResolveToolResultAsync(
        AgentToolCallRequest call,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        CancellationToken cancellationToken)
    {
        if (!IsCacheableReadOnlyTool(call.ToolId, availableToolsById)
            || HasTransientResourceCapabilities(call.CallId))
        {
            try
            {
                return new ResolvedToolResult(
                    await ExecuteToolAsync(call, availableToolsById[call.ToolId], cancellationToken),
                    WasReadOnlyCacheHit: false);
            }
            finally
            {
                InvalidateReadOnlyToolResultCache();
            }
        }

        var key = BuildToolCallCacheKey(call);
        lock (_readOnlyToolResultCacheSync)
        {
            if (_readOnlyToolResultCache.TryGetValue(key, out var cached))
            {
                return new ResolvedToolResult(
                    CreateDuplicateReadOnlyToolResult(call.ToolId, cached),
                    WasReadOnlyCacheHit: true);
            }
        }

        var executed = await ExecuteToolAsync(call, availableToolsById[call.ToolId], cancellationToken);
        if (!executed.IsError && !executed.RequiresPromptContextRefresh)
        {
            lock (_readOnlyToolResultCacheSync)
            {
                _readOnlyToolResultCache[key] = executed;
            }
        }
        return new ResolvedToolResult(executed, WasReadOnlyCacheHit: false);
    }

    private async Task<AgentToolResult> ExecuteToolAsync(
        AgentToolCallRequest call,
        AgentToolDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var allowOutsideConfiguredScope = _allowOutsideConfiguredScopeByCallId.GetValueOrDefault(call.CallId);
        var approvedResourceReferences = _approvedResourceReferencesByCallId.GetValueOrDefault(call.CallId) ?? [];
        var approvedResourceClaims = _approvedResourceClaimsByCallId.GetValueOrDefault(call.CallId) ?? [];
        var approvedResourceCapabilities = _approvedResourceCapabilitiesByCallId.GetValueOrDefault(call.CallId) ?? [];
        var execution = GetToolExecution(call.CallId);
        var invocation = _toolService.GetPreparedInvocation(execution.ExecutionId)
            ?? throw new InvalidOperationException(
                $"Tool '{call.ToolId}' no longer has its prepared package invocation reference.");
        var preflight = await _toolService.PreflightExecutionAsync(
            call.ToolId,
            call.ArgumentsJson,
            _session.SessionId,
            _profile.ProfileId,
            _workspace,
            allowOutsideConfiguredScope,
            _runId,
            _runRevision,
            _userTurnId,
            call.CallId,
            approvedResourceReferences: approvedResourceReferences,
            approvedResourceClaims: approvedResourceClaims,
            approvedResourceCapabilities: approvedResourceCapabilities,
            cancellationToken: cancellationToken,
            advertisedDescriptor: descriptor,
            advertisedOwnerPackageId: execution.OwnerPackageId,
            advertisedInvocation: invocation);
        if (preflight is not null)
        {
            _toolService.ReleasePreparedInvocation(execution.ExecutionId);
            return preflight;
        }

        var started = _sessionService.StartToolExecution(
            _runLease,
            execution.ExecutionId,
            execution.InvocationFingerprint);
        var executionTask = _toolService.ExecuteAsync(
            call.ToolId,
            call.ArgumentsJson,
            _session.SessionId,
            _profile.ProfileId,
            _workspace,
            allowOutsideConfiguredScope,
            runId: _runId,
            runRevision: _runRevision,
            userTurnId: _userTurnId,
            toolCallId: call.CallId,
            approvedResourceReferences: approvedResourceReferences,
            approvedResourceClaims: approvedResourceClaims,
            approvedResourceCapabilities: approvedResourceCapabilities,
            advertisedDescriptor: descriptor,
            advertisedOwnerPackageId: execution.OwnerPackageId,
            advertisedInvocation: invocation,
            cancellationToken: cancellationToken);
        _sessionService.PublishToolExecutionStarted(_runLease, started.ToolCallTurn);
        try
        {
            return await executionTask;
        }
        finally
        {
            _toolService.ReleasePreparedInvocation(execution.ExecutionId);
        }
    }

    private async Task<AgentToolResult> ExecuteApprovedToolAsync(
        AgentPendingPermissionRequestRecord pending,
        AgentToolDescriptor advertisedDescriptor,
        string advertisedOwnerPackageId,
        AgentToolInvocationReference advertisedInvocation,
        IReadOnlyList<string> approvedResourceReferences,
        IReadOnlyList<AgentResourceClaim> approvedResourceClaims,
        IReadOnlyList<string> approvedResourceCapabilities,
        CancellationToken cancellationToken)
    {
        var allowOutsideConfiguredScope = string.Equals(
            pending.BoundaryId,
            AgentPermissionBoundaryIds.OutsideConfiguredScope,
            StringComparison.OrdinalIgnoreCase);
        try
        {
            return await _toolService.ExecuteAsync(
                pending.ToolId ?? string.Empty,
                pending.ArgumentsJson,
                pending.SessionId,
                _profile.ProfileId,
                _workspace,
                allowOutsideConfiguredScope: allowOutsideConfiguredScope,
                runId: pending.RunId,
                runRevision: pending.RunRevision,
                userTurnId: pending.UserTurnId,
                toolCallId: pending.CallId,
                approvedResourceReferences: approvedResourceReferences,
                approvedResourceClaims: approvedResourceClaims,
                approvedResourceCapabilities: approvedResourceCapabilities,
                advertisedDescriptor: advertisedDescriptor,
                advertisedOwnerPackageId: advertisedOwnerPackageId,
                advertisedInvocation: advertisedInvocation,
                cancellationToken: cancellationToken);
        }
        finally
        {
            if (!advertisedDescriptor.IsReadOnly)
            {
                InvalidateReadOnlyToolResultCache();
            }
            if (pending.ToolExecutionId is { } executionId)
            {
                _toolService.ReleasePreparedInvocation(executionId);
            }
        }
    }

    private static bool IsCacheableReadOnlyTool(
        string toolId,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById)
        => availableToolsById.TryGetValue(toolId, out var descriptor) && descriptor.IsReadOnly;

    private bool HasTransientResourceCapabilities(string callId)
        => _approvedResourceCapabilitiesByCallId.GetValueOrDefault(callId)?.Count > 0;

    private string BuildToolCallCacheKey(AgentToolCallRequest call)
        => string.Concat(
            call.ToolId,
            "\n",
            _allowOutsideConfiguredScopeByCallId.GetValueOrDefault(call.CallId) ? "outside-approved" : "configured-scope",
            "\n",
            string.Join("\n", (_approvedResourceReferencesByCallId.GetValueOrDefault(call.CallId) ?? []).Order(StringComparer.Ordinal)),
            "\n",
            string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson.Trim());

    private static AgentToolResult CreateDuplicateReadOnlyToolResult(string toolId, AgentToolResult cached)
        => new(
            toolId,
            $"Skipped duplicate read-only tool call for '{toolId}' and reused the existing result.",
            Content: string.IsNullOrWhiteSpace(cached.Content)
                ? $"### Duplicate read-only tool call skipped\n\nTool '{toolId}' was already executed with the same arguments earlier in this run. Reuse the earlier result instead of repeating the call."
                : $"### Duplicate read-only tool call skipped\n\nTool '{toolId}' was already executed with the same arguments earlier in this run. Reusing the earlier result below.\n\n{cached.Content}",
            StructuredPayloadJson: cached.StructuredPayloadJson,
            Sources: cached.Sources,
            WasTruncated: cached.WasTruncated,
            BackendId: cached.BackendId,
            PresentationPayloadJson: cached.PresentationPayloadJson)
        {
            RequiresPromptContextRefresh = cached.RequiresPromptContextRefresh,
        };

    internal void InvalidateReadOnlyToolResultCache()
    {
        lock (_readOnlyToolResultCacheSync)
        {
            _readOnlyToolResultCache.Clear();
        }
    }

    internal sealed record ExecutedToolResult(
        AgentToolCallRequest ToolCall,
        AgentToolResult Result,
        long ElapsedMilliseconds,
        bool WasReadOnlyCacheHit);

    private sealed record ResolvedToolResult(
        AgentToolResult Result,
        bool WasReadOnlyCacheHit);
}
