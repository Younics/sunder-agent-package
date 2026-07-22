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
        var inFlight = new Dictionary<string, Task<AgentToolResult>>(StringComparer.Ordinal);
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
        IDictionary<string, Task<AgentToolResult>> inFlight,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await ResolveBatchToolResultAsync(toolCall, availableToolsById, inFlight, cancellationToken);
        return new ExecutedToolResult(toolCall, result, stopwatch.ElapsedMilliseconds);
    }

    private async Task<AgentToolResult> ResolveBatchToolResultAsync(
        AgentToolCallRequest toolCall,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        IDictionary<string, Task<AgentToolResult>> inFlight,
        CancellationToken cancellationToken)
    {
        if (!IsCacheableReadOnlyTool(toolCall.ToolId, availableToolsById))
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
        return primary.IsError
            ? await ResolveToolResultAsync(toolCall, availableToolsById, cancellationToken)
            : CreateDuplicateReadOnlyToolResult(toolCall.ToolId, primary);
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
            var suspended = _sessionService.SuspendRun(_runLease, childJoin!, result.Summary)
                ?? throw new InvalidOperationException("The parent run changed before its child-join suspension could be persisted.");
            return new AgentToolCallOutcome(AgentToolCallOutcomeKind.WaitingForApproval, suspended.Checkpoint, result);
        }

        if (string.Equals(result.ErrorCode, AgentToolResultErrorCodes.ChildWaitingForApproval, StringComparison.OrdinalIgnoreCase))
        {
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
        var resultTurn = AppendToolResult(call.CallId, call.ToolId, call.ArgumentsJson, result);
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

    private async Task<AgentToolResult> ResolveToolResultAsync(
        AgentToolCallRequest call,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        CancellationToken cancellationToken)
    {
        if (!IsCacheableReadOnlyTool(call.ToolId, availableToolsById))
        {
            try
            {
                return await ExecuteToolAsync(call, availableToolsById[call.ToolId], cancellationToken);
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
                return CreateDuplicateReadOnlyToolResult(call.ToolId, cached);
            }
        }

        var executed = await ExecuteToolAsync(call, availableToolsById[call.ToolId], cancellationToken);
        if (!executed.IsError)
        {
            lock (_readOnlyToolResultCacheSync)
            {
                _readOnlyToolResultCache[key] = executed;
            }
        }
        return executed;
    }

    private Task<AgentToolResult> ExecuteToolAsync(
        AgentToolCallRequest call,
        AgentToolDescriptor descriptor,
        CancellationToken cancellationToken)
        => _toolService.ExecuteAsync(
            call.ToolId,
            call.ArgumentsJson,
            _session.SessionId,
            _profile.ProfileId,
            _workspace,
            allowOutsideConfiguredScope: false,
            runId: _runId,
            runRevision: _runRevision,
            userTurnId: _userTurnId,
            toolCallId: call.CallId,
            advertisedDescriptor: descriptor,
            cancellationToken: cancellationToken);

    private static bool IsCacheableReadOnlyTool(
        string toolId,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById)
        => availableToolsById.TryGetValue(toolId, out var descriptor) && descriptor.IsReadOnly;

    private static string BuildToolCallCacheKey(AgentToolCallRequest call)
        => string.Concat(call.ToolId, "\n", string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson.Trim());

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
            PresentationPayloadJson: cached.PresentationPayloadJson);

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
        long ElapsedMilliseconds);
}
