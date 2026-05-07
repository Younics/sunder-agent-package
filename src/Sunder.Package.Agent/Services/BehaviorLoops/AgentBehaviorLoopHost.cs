using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed class AgentBehaviorLoopHost(
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
    Func<bool> isCurrentRun) : IAgentBehaviorLoopRuntime
{
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
    private readonly Func<bool> _isCurrentRun = isCurrentRun;
    private IReadOnlyDictionary<string, AgentToolDescriptor>? _availableToolsById;
    private readonly Dictionary<string, AgentToolResult> _readOnlyToolResultCache = new(StringComparer.Ordinal);
    private readonly object _readOnlyToolResultCacheSync = new();

    public bool IsCurrentRun() => _isCurrentRun();

    public IReadOnlyList<AgentTurnRecord> ListTurns() => _sessionService.ListTurns(_session.SessionId);

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
        => _sessionService.SaveCheckpoint(_session.SessionId, _runRevision, status, summary);

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

        try
        {
            _eventLogger.WriteAsync(ToPackageLogLevel(level), eventName, message, mergedAttributes, exception).GetAwaiter().GetResult();
        }
        catch
        {
            // Logging must never interrupt agent execution.
        }
    }

    private static PackageLogLevel ToPackageLogLevel(AgentLogLevel level)
        => level switch
        {
            AgentLogLevel.Trace => PackageLogLevel.Trace,
            AgentLogLevel.Debug => PackageLogLevel.Debug,
            AgentLogLevel.Information => PackageLogLevel.Information,
            AgentLogLevel.Warning => PackageLogLevel.Warning,
            AgentLogLevel.Error => PackageLogLevel.Error,
            AgentLogLevel.Critical => PackageLogLevel.Critical,
            _ => PackageLogLevel.Information,
        };

    private sealed class ProviderEventSink(IPackageEventLogger eventLogger) : IAgentProviderEventSink
    {
        public ValueTask WriteAsync(
            AgentLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
            => eventLogger.WriteAsync(ToPackageLogLevel(level), eventName, message, attributes, exception, cancellationToken);
    }

    public AgentTurnRecord UpsertAssistantTurn(AgentTurnRecord? assistantTurn, string content)
        => assistantTurn is null
            ? _sessionService.AppendTextTurn(_session.SessionId, AgentMessageRole.Assistant, content)
            : _sessionService.UpdateTextTurn(assistantTurn.TurnId, content);

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

    public async ValueTask<AgentToolCallOutcome> InvokeToolAsync(
        AgentToolCallRequest toolCall,
        AgentTurnRecord? assistantTurn,
        CancellationToken cancellationToken = default)
    {
        var availableToolsById = await GetAvailableToolsByIdAsync(cancellationToken);
        var permissionStopwatch = Stopwatch.StartNew();
        LogEvent(AgentLogLevel.Debug, "tool.permission.start", "Evaluating tool permission.", attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool.id"] = toolCall.ToolId,
        });
        var permissionRequest = await _toolService.BuildPermissionRequestAsync(
            toolCall.ToolId,
            toolCall.ArgumentsJson,
            _session.SessionId,
            _profile.ProfileId,
            _workspace,
            runId: _runId,
            runRevision: _runRevision,
            userTurnId: _userTurnId,
            toolCallId: toolCall.CallId,
            cancellationToken: cancellationToken);
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
                    _session.SessionId,
                    AgentMessageRole.Assistant,
                    toolCall.CallId,
                    toolCall.ToolId,
                    toolCall.ArgumentsJson);
                _permissionService.SavePendingRequest(new AgentPendingPermissionRequestRecord(
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
                    _session.RootSessionId ?? _session.SessionId));
                var waitingCheckpoint = SaveCheckpoint(AgentRunStatus.WaitingForApproval, permissionRequest.Summary);
                return new AgentToolCallOutcome(AgentToolCallOutcomeKind.WaitingForApproval, waitingCheckpoint);
            }
        }

        SaveCheckpoint(AgentRunStatus.Running, $"Executing tool '{toolCall.ToolId}'.");
        _sessionService.AppendToolCallTurn(
            _session.SessionId,
            AgentMessageRole.Assistant,
            toolCall.CallId,
            toolCall.ToolId,
            toolCall.ArgumentsJson);

        var executionStopwatch = Stopwatch.StartNew();
        LogEvent(AgentLogLevel.Information, "tool.execution.start", "Executing tool.", attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool.id"] = toolCall.ToolId,
        });
        var toolResult = await ResolveToolResultAsync(
            toolCall,
            availableToolsById,
            _readOnlyToolResultCache,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentRun())
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (string.Equals(toolResult.ErrorCode, AgentToolResultErrorCodes.ChildWaitingForApproval, StringComparison.OrdinalIgnoreCase))
        {
            var waitingCheckpoint = SaveCheckpoint(AgentRunStatus.WaitingForApproval, toolResult.Summary);
            return new AgentToolCallOutcome(AgentToolCallOutcomeKind.WaitingForApproval, waitingCheckpoint, toolResult);
        }

        LogEvent(
            toolResult.IsError ? AgentLogLevel.Error : AgentLogLevel.Information,
            toolResult.IsError ? "tool.execution.failed" : "tool.execution.completed",
            toolResult.Summary,
            executionStopwatch.ElapsedMilliseconds,
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
            if (IsRecoverableToolExecutionError(toolResult))
            {
                SaveCheckpoint(AgentRunStatus.Running, $"Tool '{toolCall.ToolId}' returned an error result. Continuing provider execution.");
                return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Executed, Result: toolResult);
            }

            var toolFailureContent = string.IsNullOrWhiteSpace(toolResult.Content)
                ? $"### Tool execution failed\n\n{toolResult.Summary}"
                : toolResult.Content;
            var failedTurn = UpsertAssistantTurn(assistantTurn, toolFailureContent);
            var failedCheckpoint = SaveCheckpoint(
                string.Equals(toolResult.ErrorCode, "permission-ask", StringComparison.OrdinalIgnoreCase)
                    ? AgentRunStatus.WaitingForApproval
                    : AgentRunStatus.Failed,
                toolResult.ErrorCode ?? $"Tool '{toolCall.ToolId}' failed.");
            await PublishLifecycleEventAsync(
                AgentLifecycleEventKind.RunFailed,
                AgentRunStatus.Failed,
                triggerTurn: failedTurn,
                checkpoint: failedCheckpoint,
                cancellationToken: cancellationToken);
            return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Failed, failedCheckpoint, toolResult);
        }

        SaveCheckpoint(AgentRunStatus.Running, $"Tool '{toolCall.ToolId}' completed. Continuing provider execution.");
        return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Executed, Result: toolResult);
    }

    public async ValueTask<AgentToolCallOutcome> HandleApprovedToolCallAsync(
        AgentPendingPermissionRequestRecord pending,
        CancellationToken cancellationToken = default)
    {
        SaveCheckpoint(AgentRunStatus.Running, $"Executing approved tool '{pending.ToolId}'.");

        var executionStopwatch = Stopwatch.StartNew();
        LogEvent(AgentLogLevel.Information, "tool.approved_execution.start", "Executing approved tool.", attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool.id"] = pending.ToolId,
        });
        var toolResult = await ExecuteApprovedToolAsync(pending, cancellationToken);
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

        if (IsRecoverableToolExecutionError(toolResult))
        {
            SaveCheckpoint(AgentRunStatus.Running, $"Tool '{pending.ToolId}' returned an error result. Continuing provider execution.");
            return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Executed, Result: toolResult);
        }

        var failedCheckpoint = SaveCheckpoint(AgentRunStatus.Failed, toolResult.Summary);
        await PublishLifecycleEventAsync(
            AgentLifecycleEventKind.RunFailed,
            AgentRunStatus.Failed,
            triggerTurn: toolResultTurn,
            checkpoint: failedCheckpoint,
            cancellationToken: CancellationToken.None);
        return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Failed, failedCheckpoint, toolResult);
    }

    private static bool IsRecoverableToolExecutionError(AgentToolResult toolResult)
        => string.Equals(toolResult.ErrorCode, AgentToolResultErrorCodes.ShellNonZeroExit, StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolResult.ErrorCode, AgentToolResultErrorCodes.ShellTimeout, StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolResult.ErrorCode, AgentToolResultErrorCodes.SubagentRunFailed, StringComparison.OrdinalIgnoreCase);

    private AgentTurnRecord AppendToolResult(string callId, string toolId, string? argumentsJson, AgentToolResult toolResult)
        => _sessionService.AppendToolResultTurn(
            _session.SessionId,
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
            toolResult.BackendId);

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

            var executedResult = await ExecuteToolAsync(requestedToolCall, cancellationToken);
            if (!executedResult.IsError)
            {
                lock (_readOnlyToolResultCacheSync)
                {
                    readOnlyToolResultCache[cacheKey] = executedResult;
                }
            }

            return executedResult;
        }

        return await ExecuteToolAsync(requestedToolCall, cancellationToken);
    }

    private async Task<AgentToolResult> ExecuteToolAsync(
        AgentToolCallRequest requestedToolCall,
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
            cancellationToken: runCancellationToken);
    }

    private async Task<AgentToolResult> ExecuteApprovedToolAsync(
        AgentPendingPermissionRequestRecord pending,
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
            BackendId: cachedResult.BackendId);
    }

}
