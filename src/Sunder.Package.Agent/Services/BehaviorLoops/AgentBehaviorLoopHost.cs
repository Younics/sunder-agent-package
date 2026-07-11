using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed partial class AgentBehaviorLoopHost(
    AgentSessionService sessionService,
    AgentToolService toolService,
    AgentPermissionService permissionService,
    AgentMemoryCoordinator memoryCoordinator,
    IPackageEventLogger eventLogger,
    IAgentChatProvider provider,
    AgentSessionRecord session,
    AgentProfileRecord profile,
    AgentWorkspaceRecord? workspace,
    Guid runId,
    long runRevision,
    DateTimeOffset runStartedAtUtc,
    string userMessage,
    Guid userTurnId,
    AgentDurableRunLease runLease,
    IAgentBehaviorLoop defaultBehaviorLoop,
    Func<bool> isCurrentRun) : IAgentBehaviorLoopRuntime, IAgentInnerBehaviorLoopRuntime, IAgentRunActivitySink
{
    private const int MaxParallelToolExecutions = 4;

    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentToolService _toolService = toolService;
    private readonly AgentPermissionService _permissionService = permissionService;
    private readonly AgentMemoryCoordinator _memoryCoordinator = memoryCoordinator;
    private readonly IPackageEventLogger _eventLogger = eventLogger;
    private readonly IAgentChatProvider _provider = provider;
    private readonly AgentSessionRecord _session = session;
    private readonly AgentProfileRecord _profile = profile;
    private readonly AgentWorkspaceRecord? _workspace = workspace;
    private readonly Guid _runId = runId;
    private readonly long _runRevision = runRevision;
    private readonly DateTimeOffset _runStartedAtUtc = runStartedAtUtc;
    private readonly string _userMessage = userMessage;
    private readonly Guid _userTurnId = userTurnId;
    private readonly AgentDurableRunLease _runLease = runLease;
    private readonly IAgentBehaviorLoop _defaultBehaviorLoop = defaultBehaviorLoop;
    private readonly Func<bool> _isCurrentRun = isCurrentRun;
    private IReadOnlyDictionary<string, AgentToolDescriptor>? _availableToolsById;
    private readonly Dictionary<string, AgentToolResult> _readOnlyToolResultCache = new(StringComparer.Ordinal);
    private readonly object _readOnlyToolResultCacheSync = new();

    public bool IsCurrentRun() => _isCurrentRun();

    public ValueTask<AgentBehaviorLoopResult> RunDefaultLoopAsync(
        AgentBehaviorLoopContext context,
        CancellationToken cancellationToken = default)
        => _defaultBehaviorLoop.RunAsync(context, this, cancellationToken);

    public IReadOnlyList<AgentTurnRecord> ListTurns() => _sessionService.ListTurns(_session.SessionId);

    public IReadOnlyList<AgentTurnRecord> ListRecentTurns(int limit) => _sessionService.ListRecentTurns(_session.SessionId, limit);

    public async ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        LogEvent(AgentLogLevel.Debug, "memory.context.start", "Building memory context.");
        try
        {
            var context = await _memoryCoordinator.BuildInstructionContextAsync(
                _session,
                _profile,
                _runId,
                _runRevision,
                _userMessage,
                _runStartedAtUtc,
                cancellationToken);

            LogEvent(
                AgentLogLevel.Debug,
                "memory.context.completed",
                context.HasSupplementaryContext ? "supplementary context included" : "no supplementary context",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["memory.has_supplementary_context"] = context.HasSupplementaryContext,
                    ["memory.system_instruction_length"] = context.SystemInstructions?.Length ?? 0,
                    ["memory.prompt_context_block_count"] = context.PromptContextBlocks?.Count ?? 0,
                    ["memory.recall_entry_count"] = context.RecallResult?.Entries.Count ?? 0,
                });
            return new AgentBehaviorInstructionContext(context.SystemInstructions, context.HasSupplementaryContext);
        }
        catch (OperationCanceledException)
        {
            LogEvent(AgentLogLevel.Debug, "memory.context.canceled", "Memory context build was canceled.", elapsedMilliseconds: stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            LogEvent(AgentLogLevel.Warning, "memory.context.failed", "Memory context build failed.", stopwatch.ElapsedMilliseconds, exception: ex);
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<AgentRuntimeTool>> ListReadyToolsAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        LogEvent(AgentLogLevel.Debug, "tools.ready_list.start", "Listing ready tools.");
        try
        {
            var tools = await _toolService.ListReadyRuntimeToolsAsync(_profile, _session.SessionId, _workspace, cancellationToken);
            _availableToolsById = tools
                .Select(tool => tool.Descriptor)
                .ToDictionary(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase);
            LogEvent(
                AgentLogLevel.Debug,
                "tools.ready_list.completed",
                $"{tools.Count} ready tool(s)",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tool.count"] = tools.Count,
                });
            return tools;
        }
        catch (OperationCanceledException)
        {
            LogEvent(AgentLogLevel.Debug, "tools.ready_list.canceled", "Ready tool listing was canceled.", elapsedMilliseconds: stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            LogEvent(AgentLogLevel.Warning, "tools.ready_list.failed", "Ready tool listing failed.", stopwatch.ElapsedMilliseconds, exception: ex);
            throw;
        }
    }

    public ValueTask<IChatClient> CreateChatClientAsync(
        AgentChatClientContext context,
        CancellationToken cancellationToken = default)
        => _provider.CreateChatClientAsync(context with
        {
            EventSink = new ProviderEventSink(_eventLogger),
            CorrelationAttributes = BuildProviderCorrelationAttributes(context.CorrelationAttributes),
        }, cancellationToken);

    public AgentRunCheckpointRecord SaveCheckpoint(AgentRunStatus status, string? summary)
    {
        var transition = _sessionService.TryTransitionRun(_runLease, status, summary);
        if (transition is not null)
        {
            return transition.Checkpoint;
        }

        var latest = _sessionService.GetLatestCheckpoint(_session.SessionId);
        if (latest is not null
            && latest.RunRevision == _runRevision
            && latest.Status is AgentRunStatus.Interrupted
                or AgentRunStatus.Stopped
                or AgentRunStatus.Completed
                or AgentRunStatus.Failed)
        {
            return latest;
        }

        throw new InvalidOperationException(
            $"Run '{_runId}' rejected a stale or illegal transition to '{status}'.");
    }

    public void ReportRunActivity(AgentRunActivityKind kind, string text)
        => _sessionService.ReportRunActivity(_session.SessionId, _runRevision, kind, text);

    private IReadOnlyDictionary<string, object?> BuildProviderCorrelationAttributes(IReadOnlyDictionary<string, object?>? existingAttributes)
    {
        var attributes = existingAttributes is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(existingAttributes, StringComparer.Ordinal);
        attributes["session.id"] = _session.SessionId;
        attributes["run.id"] = _runId;
        attributes["run.revision"] = _runRevision;
        attributes["profile.id"] = _profile.ProfileId;
        return attributes;
    }

    public void LogEvent(
        AgentLogLevel level,
        string eventName,
        string message,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null)
    {
        var mergedAttributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["session.id"] = _session.SessionId,
            ["run.id"] = _runId,
            ["run.revision"] = _runRevision,
            ["profile.id"] = _profile.ProfileId,
        };
        if (elapsedMilliseconds is not null)
        {
            mergedAttributes["duration.ms"] = elapsedMilliseconds.Value;
        }

        if (attributes is not null)
        {
            foreach (var attribute in attributes)
            {
                mergedAttributes[attribute.Key] = attribute.Value;
            }
        }

        _ = WriteEventSafelyAsync(level, eventName, message, mergedAttributes, exception);
    }

    private async Task WriteEventSafelyAsync(
        AgentLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?> attributes,
        Exception? exception)
    {
        try
        {
            await _eventLogger.WriteAsync(ToPackageLogLevel(level), eventName, message, attributes, exception);
        }
        catch
        {
            // Logging must never interrupt agent execution.
        }
    }

    public AgentTurnRecord UpsertAssistantTurn(AgentTurnRecord? assistantTurn, string content)
    {
        if (assistantTurn is null)
        {
            return _sessionService.AppendTextTurn(
                _runLease,
                AgentMessageRole.Assistant,
                content);
        }

        return string.Equals(RenderTextContent(assistantTurn), content, StringComparison.Ordinal)
            ? assistantTurn
            : _sessionService.UpdateTextTurn(_runLease, assistantTurn.TurnId, content);
    }

    private static string RenderTextContent(AgentTurnRecord turn)
        => string.Join("\n\n", turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.Text && !string.IsNullOrWhiteSpace(item.TextContent))
            .Select(item => item.TextContent!.Trim()));

    public ValueTask PublishLifecycleEventAsync(
        AgentLifecycleEventKind kind,
        AgentRunStatus status,
        AgentTurnRecord? triggerTurn = null,
        AgentRunCheckpointRecord? checkpoint = null,
        bool isInterrupted = false,
        CancellationToken cancellationToken = default)
        => new(_memoryCoordinator.PublishLifecycleEventAsync(
            kind,
            _session,
            _profile,
            _runId,
            _runRevision,
            status,
            _runStartedAtUtc,
            _userMessage,
            triggerTurn,
            checkpoint,
            isInterrupted,
            cancellationToken));

    private async Task<AgentToolCallOutcome?> EvaluateToolPermissionAsync(
        AgentToolCallRequest toolCall,
        AgentTurnRecord? assistantTurn,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        CancellationToken cancellationToken)
    {
        if (!availableToolsById.TryGetValue(toolCall.ToolId, out var advertisedDescriptor))
        {
            return await RecordSecurityDenialAsync(
                toolCall,
                $"Tool '{toolCall.ToolId}' was not advertised as ready and assigned for this run.",
                AgentToolSecurityErrorCodes.NotAdvertised,
                cancellationToken);
        }

        var permissionStopwatch = Stopwatch.StartNew();
        LogEvent(AgentLogLevel.Debug, "tool.permission.start", "Evaluating tool permission.", attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool.id"] = toolCall.ToolId,
        });
        var permissionResolution = await _toolService.ResolvePermissionRequirementAsync(
            toolCall.ToolId,
            toolCall.ArgumentsJson,
            _session.SessionId,
            _profile.ProfileId,
            _workspace,
            runId: _runId,
            runRevision: _runRevision,
            userTurnId: _userTurnId,
            toolCallId: toolCall.CallId,
            advertisedDescriptor: advertisedDescriptor,
            cancellationToken: cancellationToken);
        if (permissionResolution is null)
        {
            return await RecordSecurityDenialAsync(
                toolCall,
                $"Tool '{toolCall.ToolId}' is no longer in the ready, assigned tool catalog for this run.",
                AgentToolSecurityErrorCodes.NotAdvertised,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(permissionResolution.DeniedReason))
        {
            return await RecordSecurityDenialAsync(
                toolCall,
                permissionResolution.DeniedReason,
                AgentToolSecurityErrorCodes.PermissionContextInsufficient,
                cancellationToken);
        }

        var permissionRequest = permissionResolution.PermissionRequest;
        LogEvent(
                AgentLogLevel.Debug,
            "tool.permission.completed",
            permissionRequest?.Summary ?? "No permission request required.",
            permissionStopwatch.ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool.id"] = toolCall.ToolId,
                ["permission.action_id"] = permissionRequest?.ActionId,
                ["permission.boundary_id"] = permissionRequest?.BoundaryId,
                ["permission.is_mutation"] = permissionRequest?.IsMutation,
            });
        if (permissionRequest is not null)
        {
            var permissionEvaluation = _permissionService.Evaluate(_session.SessionId, permissionRequest);
            LogEvent(
                permissionEvaluation.Decision == AgentPermissionDecision.Allow ? AgentLogLevel.Information : AgentLogLevel.Warning,
                "tool.permission.evaluated",
                permissionEvaluation.Decision.ToString(),
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["permission.action_id"] = permissionRequest.ActionId,
                    ["permission.boundary_id"] = permissionRequest.BoundaryId,
                    ["permission.decision"] = permissionEvaluation.Decision,
                    ["permission.reason"] = permissionEvaluation.Reason,
                });
            if (permissionEvaluation.Decision == AgentPermissionDecision.Deny)
            {
                var deniedTurn = UpsertAssistantTurn(assistantTurn, $"### Permission denied\n\n{permissionRequest.Summary}");
                var deniedCheckpoint = SaveCheckpoint(AgentRunStatus.Failed, permissionEvaluation.Reason);
                await PublishLifecycleEventAsync(
                    AgentLifecycleEventKind.RunFailed,
                    AgentRunStatus.Failed,
                    triggerTurn: deniedTurn,
                    checkpoint: deniedCheckpoint,
                    cancellationToken: cancellationToken);
                return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Denied, deniedCheckpoint);
            }

            if (permissionEvaluation.Decision == AgentPermissionDecision.Ask)
            {
                _sessionService.AppendToolCallTurn(
                    _runLease,
                    AgentMessageRole.Assistant,
                    toolCall.CallId,
                    toolCall.ToolId,
                    toolCall.ArgumentsJson);
                var pendingRequest = new AgentPendingPermissionRequestRecord(
                    Guid.NewGuid().ToString("N"),
                    _session.SessionId,
                    _runId,
                    _runRevision,
                    _profile.ProfileId,
                    _userTurnId,
                    _userMessage,
                    toolCall.CallId,
                    permissionRequest.ActionId,
                    permissionRequest.BoundaryId,
                    permissionRequest.Summary,
                    toolCall.ToolId,
                    toolCall.ArgumentsJson,
                    permissionRequest.Command,
                    permissionRequest.Path,
                    permissionRequest.WorkspaceId,
                    permissionRequest.BindingId,
                    permissionRequest.ResourceDisplayName,
                    permissionRequest.ResourceReference,
                    permissionRequest.IsMutation,
                    DateTimeOffset.UtcNow,
                    _session.ParentSessionId,
                    _session.RootSessionId ?? _session.SessionId,
                    ExecutionFingerprint: AgentPermissionFingerprint.Create(
                        _runId,
                        _runRevision,
                        permissionResolution.Descriptor,
                        toolCall.CallId,
                        toolCall.ArgumentsJson,
                        _workspace,
                        permissionResolution.ExecutionBinding,
                        permissionResolution.ExecutionTarget,
                        permissionRequest));
                if (_permissionService.SavePendingRequestAndSuspendRun(
                        pendingRequest,
                        _runLease) is null)
                {
                    throw new InvalidOperationException("The run changed before its permission suspension could be persisted.");
                }


                var waitingCheckpoint = _sessionService.GetLatestCheckpoint(_session.SessionId)
                    ?? throw new InvalidOperationException("The permission suspension checkpoint was not persisted.");
                return new AgentToolCallOutcome(AgentToolCallOutcomeKind.WaitingForApproval, waitingCheckpoint);
            }
        }

        return null;
    }

    private async Task<AgentToolCallOutcome> RecordSecurityDenialAsync(
        AgentToolCallRequest toolCall,
        string summary,
        string errorCode,
        CancellationToken cancellationToken)
    {
        _sessionService.AppendToolCallTurn(
            _runLease,
            AgentMessageRole.Assistant,
            toolCall.CallId,
            toolCall.ToolId,
            toolCall.ArgumentsJson);
        var result = new AgentToolResult(
            toolCall.ToolId,
            summary,
            Content: $"### Tool denied\n\n{summary}",
            IsError: true,
            ErrorCode: errorCode);
        var resultTurn = AppendToolResult(toolCall.CallId, toolCall.ToolId, toolCall.ArgumentsJson, result);
        var checkpoint = SaveCheckpoint(AgentRunStatus.Failed, summary);
        await PublishLifecycleEventAsync(
            AgentLifecycleEventKind.RunFailed,
            AgentRunStatus.Failed,
            triggerTurn: resultTurn,
            checkpoint: checkpoint,
            cancellationToken: cancellationToken);
        return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Denied, checkpoint, result);
    }

    private void RecordToolCallStart(AgentToolCallRequest toolCall)
    {
        SaveCheckpoint(AgentRunStatus.Running, $"Executing tool '{toolCall.ToolId}'.");
        _sessionService.AppendToolCallTurn(
            _runLease,
            AgentMessageRole.Assistant,
            toolCall.CallId,
            toolCall.ToolId,
            toolCall.ArgumentsJson);

        LogEvent(AgentLogLevel.Information, "tool.execution.start", "Executing tool.", attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool.id"] = toolCall.ToolId,
        });
    }

    private async Task<IReadOnlyList<ExecutedToolResult>> ExecuteToolBatchAsync(
        IReadOnlyList<AgentToolCallRequest> batch,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        CancellationToken cancellationToken)
    {
        var inFlightReadOnlyResults = new Dictionary<string, Task<AgentToolResult>>(StringComparer.Ordinal);
        var tasks = new Task<ExecutedToolResult>[batch.Count];
        for (var index = 0; index < batch.Count; index++)
        {
            tasks[index] = ExecuteToolCallForBatchAsync(
                batch[index],
                availableToolsById,
                inFlightReadOnlyResults,
                cancellationToken);
        }

        return await Task.WhenAll(tasks);
    }

    private async Task<ExecutedToolResult> ExecuteToolCallForBatchAsync(
        AgentToolCallRequest toolCall,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        IDictionary<string, Task<AgentToolResult>> inFlightReadOnlyResults,
        CancellationToken cancellationToken)
    {
        var executionStopwatch = Stopwatch.StartNew();

        var toolResult = await ResolveBatchToolResultAsync(
            toolCall,
            availableToolsById,
            inFlightReadOnlyResults,
            cancellationToken);
        return new ExecutedToolResult(toolCall, toolResult, executionStopwatch.ElapsedMilliseconds);
    }

    private async Task<AgentToolResult> ResolveBatchToolResultAsync(
        AgentToolCallRequest toolCall,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        IDictionary<string, Task<AgentToolResult>> inFlightReadOnlyResults,
        CancellationToken cancellationToken)
    {
        if (!IsCacheableReadOnlyTool(toolCall.ToolId, availableToolsById))
        {
            return await ResolveToolResultAsync(toolCall, availableToolsById, _readOnlyToolResultCache, cancellationToken);
        }

        var cacheKey = BuildToolCallCacheKey(toolCall);
        if (!inFlightReadOnlyResults.TryGetValue(cacheKey, out var primaryResultTask))
        {
            primaryResultTask = ResolveToolResultAsync(toolCall, availableToolsById, _readOnlyToolResultCache, cancellationToken);
            inFlightReadOnlyResults[cacheKey] = primaryResultTask;
            return await primaryResultTask;
        }

        var primaryResult = await primaryResultTask;
        return primaryResult.IsError
            ? await ResolveToolResultAsync(toolCall, availableToolsById, _readOnlyToolResultCache, cancellationToken)
            : CreateDuplicateReadOnlyToolResult(toolCall.ToolId, primaryResult);
    }

    private async Task<AgentToolCallOutcome> RecordExecutedToolResultAsync(
        ExecutedToolResult executedResult,
        CancellationToken cancellationToken)
    {
        var toolCall = executedResult.ToolCall;
        var toolResult = executedResult.Result;
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentRun())
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (AgentToolSuspensionCompatibility.TryCreateChildJoin(
                toolCall,
                toolResult,
                _userTurnId,
                out var childJoin))
        {
            var suspended = _sessionService.SuspendRun(
                _runLease,
                childJoin!,
                toolResult.Summary);
            if (suspended is null)
            {
                throw new InvalidOperationException("The parent run changed before its child-join suspension could be persisted.");
            }

            return new AgentToolCallOutcome(
                AgentToolCallOutcomeKind.WaitingForApproval,
                suspended.Checkpoint,
                toolResult);
        }

        if (string.Equals(
                toolResult.ErrorCode,
                AgentToolResultErrorCodes.ChildWaitingForApproval,
                StringComparison.OrdinalIgnoreCase))
        {
            var failedCheckpoint = SaveCheckpoint(
                AgentRunStatus.Failed,
                "A waiting child task did not provide a durable child-session identity.");
            return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Failed, failedCheckpoint, toolResult);
        }

        LogEvent(
            toolResult.IsError ? AgentLogLevel.Error : AgentLogLevel.Information,
            toolResult.IsError ? "tool.execution.failed" : "tool.execution.completed",
            toolResult.Summary,
            executedResult.ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool.id"] = toolCall.ToolId,
                ["tool.backend_id"] = toolResult.BackendId,
                ["tool.was_truncated"] = toolResult.WasTruncated,
                ["tool.is_error"] = toolResult.IsError,
                ["tool.error_code"] = toolResult.ErrorCode,
                ["tool.content_length"] = toolResult.Content?.Length ?? 0,
            });

        var toolResultTurn = AppendToolResult(toolCall.CallId, toolCall.ToolId, toolCall.ArgumentsJson, toolResult);
        await PublishLifecycleEventAsync(
            AgentLifecycleEventKind.ToolResultRecorded,
            AgentRunStatus.Running,
            triggerTurn: toolResultTurn,
            cancellationToken: cancellationToken);

        if (toolResult.IsError)
        {
            SaveCheckpoint(AgentRunStatus.Running, $"Tool '{toolCall.ToolId}' returned an error result. Continuing provider execution.");
            return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Executed, Result: toolResult);
        }

        SaveCheckpoint(AgentRunStatus.Running, $"Tool '{toolCall.ToolId}' completed. Continuing provider execution.");
        return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Executed, Result: toolResult);
    }

    private static bool RequiresTerminalRecordingLast(ExecutedToolResult executedResult)
        => string.Equals(
            executedResult.Result.ErrorCode,
            AgentToolResultErrorCodes.ChildWaitingForApproval,
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<IReadOnlyList<AgentToolCallRequest>> BuildToolExecutionBatches(
        IReadOnlyList<AgentToolCallRequest> toolCalls,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById)
    {
        var batches = new List<IReadOnlyList<AgentToolCallRequest>>();
        var parallelBatch = new List<AgentToolCallRequest>();

        foreach (var toolCall in toolCalls)
        {
            if (IsParallelSafeTool(toolCall.ToolId, availableToolsById))
            {
                parallelBatch.Add(toolCall);
                if (parallelBatch.Count >= MaxParallelToolExecutions)
                {
                    batches.Add(parallelBatch.ToArray());
                    parallelBatch.Clear();
                }

                continue;
            }

            if (parallelBatch.Count > 0)
            {
                batches.Add(parallelBatch.ToArray());
                parallelBatch.Clear();
            }

            batches.Add([toolCall]);
        }

        if (parallelBatch.Count > 0)
        {
            batches.Add(parallelBatch.ToArray());
        }

        return batches;
    }

    private static bool IsParallelSafeTool(
        string toolId,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById)
        => availableToolsById.TryGetValue(toolId, out var descriptor)
           && descriptor.ConcurrencyMode == AgentToolConcurrencyMode.ParallelSafe;

    public async ValueTask<AgentToolCallOutcome> HandleApprovedToolCallAsync(
        AgentPendingPermissionRequestRecord pending,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<bool>> beginExecutionAsync)
    {
        var availableToolsById = await GetAvailableToolsByIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(pending.ToolId)
            || !availableToolsById.TryGetValue(pending.ToolId, out var advertisedDescriptor))
        {
            return await RecordApprovedSecurityDenialAsync(
                pending,
                $"Approved tool '{pending.ToolId}' is no longer in the ready, assigned tool catalog.",
                AgentToolSecurityErrorCodes.NotAdvertised,
                cancellationToken);
        }

        var permissionResolution = await _toolService.ResolvePermissionRequirementAsync(
            pending.ToolId,
            pending.ArgumentsJson,
            pending.SessionId,
            _profile.ProfileId,
            _workspace,
            pending.RunId,
            pending.RunRevision,
            pending.UserTurnId,
            pending.CallId,
            advertisedDescriptor,
            cancellationToken);
        if (permissionResolution is null || !string.IsNullOrWhiteSpace(permissionResolution.DeniedReason))
        {
            return await RecordApprovedSecurityDenialAsync(
                pending,
                permissionResolution?.DeniedReason
                    ?? $"Approved tool '{pending.ToolId}' is no longer available from its advertised source.",
                AgentToolSecurityErrorCodes.PermissionContextChanged,
                cancellationToken);
        }

        var currentFingerprint = AgentPermissionFingerprint.Create(
            pending.RunId,
            pending.RunRevision,
            permissionResolution.Descriptor,
            pending.CallId,
            pending.ArgumentsJson,
            _workspace,
            permissionResolution.ExecutionBinding,
            permissionResolution.ExecutionTarget,
            permissionResolution.PermissionRequest);
        if (string.IsNullOrWhiteSpace(pending.ExecutionFingerprint)
            || !string.Equals(pending.ExecutionFingerprint, currentFingerprint, StringComparison.Ordinal))
        {
            return await RecordApprovedSecurityDenialAsync(
                pending,
                "The approved tool execution context changed after permission was requested.",
                AgentToolSecurityErrorCodes.PermissionContextChanged,
                cancellationToken);
        }

        if (!await beginExecutionAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new OperationCanceledException(
                "The approved tool execution was stopped before it began.",
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        SaveCheckpoint(AgentRunStatus.Running, $"Executing approved tool '{pending.ToolId}'.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentRun())
        {
            throw new OperationCanceledException(
                "The approved tool execution was stopped before it began.",
                cancellationToken);
        }

        var executionStopwatch = Stopwatch.StartNew();
        LogEvent(AgentLogLevel.Information, "tool.approved_execution.start", "Executing approved tool.", attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool.id"] = pending.ToolId,
        });
        var toolResult = await ExecuteApprovedToolAsync(
            pending,
            permissionResolution.Descriptor,
            cancellationToken);
        LogEvent(
            toolResult.IsError ? AgentLogLevel.Error : AgentLogLevel.Information,
            toolResult.IsError ? "tool.approved_execution.failed" : "tool.approved_execution.completed",
            toolResult.Summary,
            executionStopwatch.ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool.id"] = pending.ToolId,
                ["tool.backend_id"] = toolResult.BackendId,
                ["tool.was_truncated"] = toolResult.WasTruncated,
                ["tool.is_error"] = toolResult.IsError,
                ["tool.error_code"] = toolResult.ErrorCode,
                ["tool.content_length"] = toolResult.Content?.Length ?? 0,
            });
        var approvedToolCall = new AgentToolCallRequest(
            pending.CallId,
            pending.ToolId ?? string.Empty,
            pending.ArgumentsJson);
        if (AgentToolSuspensionCompatibility.TryCreateChildJoin(
                approvedToolCall,
                toolResult,
                pending.UserTurnId,
                out var childJoin))
        {
            var suspended = _sessionService.SuspendRun(
                _runLease,
                childJoin!,
                toolResult.Summary);
            if (suspended is null)
            {
                throw new InvalidOperationException("The approved run changed before its child-join suspension could be persisted.");
            }

            return new AgentToolCallOutcome(
                AgentToolCallOutcomeKind.WaitingForApproval,
                suspended.Checkpoint,
                toolResult);
        }

        var toolResultTurn = AppendToolResult(pending.CallId, pending.ToolId ?? string.Empty, pending.ArgumentsJson, toolResult);

        await PublishLifecycleEventAsync(
            AgentLifecycleEventKind.ToolResultRecorded,
            AgentRunStatus.Running,
            triggerTurn: toolResultTurn,
            cancellationToken: cancellationToken);

        if (!toolResult.IsError)
        {
            return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Executed, Result: toolResult);
        }

        SaveCheckpoint(AgentRunStatus.Running, $"Tool '{pending.ToolId}' returned an error result. Continuing provider execution.");
        return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Executed, Result: toolResult);
    }

    private async Task<AgentToolCallOutcome> RecordApprovedSecurityDenialAsync(
        AgentPendingPermissionRequestRecord pending,
        string summary,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var result = new AgentToolResult(
            pending.ToolId ?? string.Empty,
            summary,
            Content: $"### Approved tool not executed\n\n{summary}",
            IsError: true,
            ErrorCode: errorCode);
        var resultTurn = AppendToolResult(
            pending.CallId,
            pending.ToolId ?? string.Empty,
            pending.ArgumentsJson,
            result);
        var checkpoint = SaveCheckpoint(AgentRunStatus.Failed, summary);
        await PublishLifecycleEventAsync(
            AgentLifecycleEventKind.RunFailed,
            AgentRunStatus.Failed,
            triggerTurn: resultTurn,
            checkpoint: checkpoint,
            cancellationToken: cancellationToken);
        return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Denied, checkpoint, result);
    }

    private AgentTurnRecord AppendToolResult(string callId, string toolId, string? argumentsJson, AgentToolResult toolResult)
        => _sessionService.AppendToolResultTurn(
            _runLease,
            callId,
            toolId,
            argumentsJson,
            toolResult.Content,
            toolResult.Summary,
            toolResult.StructuredPayloadJson,
            toolResult.Sources is null ? null : JsonSerializer.Serialize(toolResult.Sources),
            toolResult.WasTruncated,
            toolResult.IsError,
            toolResult.ErrorCode,
            toolResult.BackendId,
            toolResult.PresentationPayloadJson);

    private async ValueTask<IReadOnlyDictionary<string, AgentToolDescriptor>> GetAvailableToolsByIdAsync(CancellationToken cancellationToken)
    {
        if (_availableToolsById is null)
        {
            await ListReadyToolsAsync(cancellationToken);
        }

        return _availableToolsById ?? new Dictionary<string, AgentToolDescriptor>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<AgentToolResult> ResolveToolResultAsync(
        AgentToolCallRequest requestedToolCall,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        IDictionary<string, AgentToolResult> readOnlyToolResultCache,
        CancellationToken cancellationToken)
    {
        if (IsCacheableReadOnlyTool(requestedToolCall.ToolId, availableToolsById))
        {
            var cacheKey = BuildToolCallCacheKey(requestedToolCall);
            lock (_readOnlyToolResultCacheSync)
            {
                if (readOnlyToolResultCache.TryGetValue(cacheKey, out var cachedResult))
                {
                    return CreateDuplicateReadOnlyToolResult(requestedToolCall.ToolId, cachedResult);
                }
            }

            var executedResult = await ExecuteToolAsync(
                requestedToolCall,
                availableToolsById[requestedToolCall.ToolId],
                cancellationToken);
            if (!executedResult.IsError)
            {
                lock (_readOnlyToolResultCacheSync)
                {
                    readOnlyToolResultCache[cacheKey] = executedResult;
                }
            }

            return executedResult;
        }

        return await ExecuteToolAsync(
            requestedToolCall,
            availableToolsById[requestedToolCall.ToolId],
            cancellationToken);
    }

    private async Task<AgentToolResult> ExecuteToolAsync(
        AgentToolCallRequest requestedToolCall,
        AgentToolDescriptor advertisedDescriptor,
        CancellationToken runCancellationToken)
    {
        return await _toolService.ExecuteAsync(
            requestedToolCall.ToolId,
            requestedToolCall.ArgumentsJson,
            _session.SessionId,
            _profile.ProfileId,
            _workspace,
            allowOutsideConfiguredScope: false,
            runId: _runId,
            runRevision: _runRevision,
            userTurnId: _userTurnId,
            toolCallId: requestedToolCall.CallId,
            advertisedDescriptor: advertisedDescriptor,
            cancellationToken: runCancellationToken);
    }

    private async Task<AgentToolResult> ExecuteApprovedToolAsync(
        AgentPendingPermissionRequestRecord pending,
        AgentToolDescriptor advertisedDescriptor,
        CancellationToken cancellationToken)
    {
        var allowOutsideConfiguredScope = string.Equals(pending.BoundaryId, AgentPermissionBoundaryIds.OutsideConfiguredScope, StringComparison.OrdinalIgnoreCase);
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
            advertisedDescriptor: advertisedDescriptor,
            cancellationToken: cancellationToken);
    }

    private static bool IsCacheableReadOnlyTool(
        string toolId,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById)
        => availableToolsById.TryGetValue(toolId, out var descriptor)
           && descriptor.IsReadOnly;

    private static string BuildToolCallCacheKey(AgentToolCallRequest toolCall)
        => string.Concat(toolCall.ToolId, "\n", string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson.Trim());

    private static AgentToolResult CreateDuplicateReadOnlyToolResult(string toolId, AgentToolResult cachedResult)
    {
        var content = string.IsNullOrWhiteSpace(cachedResult.Content)
            ? $"### Duplicate read-only tool call skipped\n\nTool '{toolId}' was already executed with the same arguments earlier in this run. Reuse the earlier result instead of repeating the call."
            : $"### Duplicate read-only tool call skipped\n\nTool '{toolId}' was already executed with the same arguments earlier in this run. Reusing the earlier result below.\n\n{cachedResult.Content}";

        return new AgentToolResult(
            toolId,
            $"Skipped duplicate read-only tool call for '{toolId}' and reused the existing result.",
            Content: content,
            StructuredPayloadJson: cachedResult.StructuredPayloadJson,
            Sources: cachedResult.Sources,
            WasTruncated: cachedResult.WasTruncated,
            IsError: false,
            ErrorCode: null,
            BackendId: cachedResult.BackendId,
            PresentationPayloadJson: cachedResult.PresentationPayloadJson);
    }

    private sealed record ExecutedToolResult(
        AgentToolCallRequest ToolCall,
        AgentToolResult Result,
        long ElapsedMilliseconds);

}
