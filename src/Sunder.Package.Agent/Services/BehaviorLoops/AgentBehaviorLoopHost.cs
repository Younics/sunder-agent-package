using System.Diagnostics;
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
    AgentWorkspaceBindingRecord? executionBinding,
    AgentDurableRunLease runLease,
    IAgentBehaviorLoop defaultBehaviorLoop,
    Func<bool> isCurrentRun) : IAgentBehaviorLoopRuntime, IAgentInnerBehaviorLoopRuntime, IAgentRunActivitySink, IAgentRunBudgetRuntime, IAgentSessionContextSelectionRuntime, IAgentPromptContextAcknowledgmentRuntime
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
    private readonly AgentWorkspaceBindingRecord? _executionBinding = executionBinding;
    private readonly AgentDurableRunLease _runLease = runLease;
    private readonly IAgentBehaviorLoop _defaultBehaviorLoop = defaultBehaviorLoop;
    private readonly Func<bool> _isCurrentRun = isCurrentRun;
    private AgentToolBatchCoordinator? _toolBatchCoordinator;
    private AgentPermissionSuspensionCoordinator? _permissionSuspensionCoordinator;
    private IReadOnlyDictionary<string, AgentToolDescriptor>? _availableToolsById;
    private IReadOnlyDictionary<string, AgentOwnedRuntimeTool>? _availableOwnedToolsById;
    private readonly Dictionary<string, AgentToolResult> _readOnlyToolResultCache = new(StringComparer.Ordinal);
    private readonly object _readOnlyToolResultCacheSync = new();
    private readonly Dictionary<string, AgentToolExecutionRecord> _toolExecutionsByCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _allowOutsideConfiguredScopeByCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _approvedResourceReferencesByCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<AgentResourceClaim>> _approvedResourceClaimsByCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _approvedResourceCapabilitiesByCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _executionTargetConfigurationGenerationByCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, AgentTurnRecord> _openAssistantTurns = [];

    public bool IsCurrentRun() => _isCurrentRun();

    AgentRunBudgetState IAgentRunBudgetRuntime.GetRunBudgetState()
        => _sessionService.GetRun(_runId)?.BudgetState
           ?? throw new AgentRunTranscriptWriteRejectedException();

    AgentRunBudgetState IAgentRunBudgetRuntime.ChargeRunBudget(AgentRunBudgetCharge charge)
        => _sessionService.ChargeRunBudget(_runLease, charge);

    private AgentToolBatchCoordinator ToolBatchCoordinator
        => _toolBatchCoordinator ??= new AgentToolBatchCoordinator(this);

    private AgentPermissionSuspensionCoordinator PermissionSuspensionCoordinator
        => _permissionSuspensionCoordinator ??= new AgentPermissionSuspensionCoordinator(this);

    public ValueTask<AgentToolCallOutcome> InvokeToolAsync(
        AgentToolCallRequest toolCall,
        AgentTurnRecord? assistantTurn,
        CancellationToken cancellationToken = default)
        => ToolBatchCoordinator.InvokeToolAsync(toolCall, assistantTurn, cancellationToken);

    public ValueTask<IReadOnlyList<AgentToolCallOutcome>> InvokeToolsAsync(
        IReadOnlyList<AgentToolCallRequest> toolCalls,
        AgentTurnRecord? assistantTurn,
        CancellationToken cancellationToken = default)
        => ToolBatchCoordinator.InvokeToolsAsync(toolCalls, assistantTurn, cancellationToken);

    internal Task<AgentToolCallOutcome?> EvaluateToolPermissionAsync(
        AgentToolCallRequest toolCall,
        AgentTurnRecord? assistantTurn,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        CancellationToken cancellationToken)
        => PermissionSuspensionCoordinator.EvaluateAsync(
            toolCall,
            assistantTurn,
            availableToolsById,
            cancellationToken);

    public ValueTask<AgentToolCallOutcome> HandleApprovedToolCallAsync(
        AgentPendingPermissionRequestRecord pending,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<bool>> beginExecutionAsync)
        => new(PermissionSuspensionCoordinator.ResumeAsync(
            pending,
            cancellationToken,
            beginExecutionAsync));

    public ValueTask<AgentBehaviorLoopResult> RunDefaultLoopAsync(
        AgentBehaviorLoopContext context,
        CancellationToken cancellationToken = default)
        => _defaultBehaviorLoop.RunAsync(context, this, cancellationToken);

    public IReadOnlyList<AgentTurnRecord> ListTurns() => _sessionService.ListTurns(_session.SessionId);

    public IReadOnlyList<AgentTurnRecord> ListRecentTurns(int limit) => _sessionService.ListRecentTurns(_session.SessionId, limit);

    public async ValueTask<IReadOnlyList<AgentRuntimeTool>> ListReadyToolsAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        LogEvent(PackageLogLevel.Debug, "tools.ready_list.start", "Listing ready tools.");
        try
        {
            var ownedTools = await _toolService.ListReadyOwnedRuntimeToolsAsync(
                _profile,
                _session.SessionId,
                _workspace,
                cancellationToken);
            _availableOwnedToolsById = ownedTools
                .ToDictionary(tool => tool.RuntimeTool.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase);
            _availableToolsById = ownedTools
                .Select(tool => tool.RuntimeTool.Descriptor)
                .ToDictionary(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase);
            LogEvent(
                PackageLogLevel.Debug,
                "tools.ready_list.completed",
                $"{ownedTools.Count} ready tool(s)",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tool.count"] = ownedTools.Count,
                });
            return ownedTools.Select(static tool => tool.RuntimeTool).ToArray();
        }
        catch (OperationCanceledException)
        {
            LogEvent(PackageLogLevel.Debug, "tools.ready_list.canceled", "Ready tool listing was canceled.", elapsedMilliseconds: stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            LogEvent(PackageLogLevel.Warning, "tools.ready_list.failed", "Ready tool listing failed.", stopwatch.ElapsedMilliseconds, exception: ex);
            throw;
        }
    }

    public ValueTask<IChatClient> CreateChatClientAsync(
        AgentChatClientContext context,
        CancellationToken cancellationToken = default)
        => _provider.CreateChatClientAsync(context with
        {
            EventLogger = _eventLogger,
            CorrelationAttributes = BuildProviderCorrelationAttributes(context.CorrelationAttributes),
        }, cancellationToken);

    public AgentRunCheckpointRecord SaveCheckpoint(AgentRunStatus status, string? summary)
    {
        if (status is AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Interrupted or AgentRunStatus.Stopped)
        {
            CompleteOpenAssistantTurn();
        }

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

    internal async Task<AgentToolCallOutcome?> EvaluateToolPermissionCoreAsync(
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
        if (!_toolService.IsCurrentWorkspaceExecutionContext(_workspace, _executionBinding))
        {
            return await RecordSecurityDenialAsync(
                toolCall,
                "The workspace paths or execution binding changed after this run started.",
                AgentToolSecurityErrorCodes.PermissionContextChanged,
                cancellationToken);
        }

        var preparedExecution = GetToolExecution(toolCall.CallId);
        var preparedInvocation = _toolService.GetPreparedInvocation(preparedExecution.ExecutionId);
        if (string.IsNullOrWhiteSpace(preparedExecution.OwnerPackageId)
            || preparedInvocation is null)
        {
            return await RecordSecurityDenialAsync(
                toolCall,
                $"Tool '{toolCall.ToolId}' did not have one authoritative package owner when execution was prepared.",
                AgentToolSecurityErrorCodes.AmbiguousOwnership,
                cancellationToken);
        }

        var permissionStopwatch = Stopwatch.StartNew();
        LogEvent(PackageLogLevel.Debug, "tool.permission.start", "Evaluating tool permission.", attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
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
            advertisedOwnerPackageId: preparedExecution.OwnerPackageId,
            advertisedInvocation: preparedInvocation,
            expectedExecutionBinding: _executionBinding,
            enforceExpectedExecutionContext: true,
            cancellationToken: cancellationToken);
        if (permissionResolution is null)
        {
            return await RecordSecurityDenialAsync(
                toolCall,
                $"Tool '{toolCall.ToolId}' is no longer in the ready, assigned tool catalog for this run.",
                AgentToolSecurityErrorCodes.NotAdvertised,
                cancellationToken);
        }
        if (!_toolService.IsCurrentWorkspaceExecutionContext(_workspace, _executionBinding))
        {
            return await RecordSecurityDenialAsync(
                toolCall,
                "The workspace paths or execution binding changed during permission planning.",
                AgentToolSecurityErrorCodes.PermissionContextChanged,
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
        _allowOutsideConfiguredScopeByCallId[toolCall.CallId] = false;
        _approvedResourceReferencesByCallId[toolCall.CallId] = [];
        _approvedResourceClaimsByCallId[toolCall.CallId] = [];
        _approvedResourceCapabilitiesByCallId[toolCall.CallId] = [];
        _executionTargetConfigurationGenerationByCallId[toolCall.CallId] =
            permissionResolution.ExecutionTargetConfigurationGeneration;
        LogEvent(
                PackageLogLevel.Debug,
            "tool.permission.completed",
            permissionRequest?.Summary ?? "No permission request required.",
            permissionStopwatch.ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool.id"] = toolCall.ToolId,
                ["permission.action_id"] = permissionRequest?.ActionId,
                ["permission.boundary_id"] = permissionRequest?.BoundaryId,
                ["permission.scope_classification_basis"] = permissionRequest?.ScopeClassificationBasis,
                ["permission.is_mutation"] = permissionRequest?.IsMutation,
            });
        if (permissionRequest is not null)
        {
            _toolService.BindPreparedResourceCapabilities(
                preparedExecution.ExecutionId,
                permissionRequest,
                preparedInvocation);
            var permissionEvaluation = _permissionService.Evaluate(_session.SessionId, permissionRequest);
            _allowOutsideConfiguredScopeByCallId[toolCall.CallId] =
                permissionEvaluation.Decision == AgentPermissionDecision.Allow
                && string.Equals(
                    permissionRequest.BoundaryId,
                    AgentPermissionBoundaryIds.OutsideConfiguredScope,
                    StringComparison.OrdinalIgnoreCase);
            if (permissionEvaluation.Decision == AgentPermissionDecision.Allow)
            {
                _approvedResourceReferencesByCallId[toolCall.CallId] = GetResourceReferences(permissionRequest);
                _approvedResourceClaimsByCallId[toolCall.CallId] = GetResourceClaims(permissionRequest);
                _approvedResourceCapabilitiesByCallId[toolCall.CallId] = GetResourceCapabilities(permissionRequest);
            }
            LogEvent(
                permissionEvaluation.Decision == AgentPermissionDecision.Allow ? PackageLogLevel.Information : PackageLogLevel.Warning,
                "tool.permission.evaluated",
                permissionEvaluation.Decision.ToString(),
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["permission.action_id"] = permissionRequest.ActionId,
                    ["permission.boundary_id"] = permissionRequest.BoundaryId,
                    ["permission.decision"] = permissionEvaluation.Decision,
                    ["permission.decision_source"] = permissionEvaluation.Source,
                    ["permission.base_decision"] = permissionEvaluation.BaseDecision,
                    ["permission.source_session_id"] = permissionEvaluation.SourceSessionId,
                    ["permission.reason"] = permissionEvaluation.Reason,
                });
            if (permissionEvaluation.Decision == AgentPermissionDecision.Deny)
            {
                var denialResult = new AgentToolResult(
                    toolCall.ToolId,
                    permissionEvaluation.Reason,
                    Content: $"### Permission denied\n\n{permissionRequest.Summary}",
                    IsError: true,
                    ErrorCode: "permission-denied");
                _toolService.ReleasePreparedInvocation(preparedExecution.ExecutionId);
                CompleteToolExecution(
                    toolCall,
                    denialResult,
                    AgentToolExecutionStatus.Failed,
                    "permission-denied");
                var deniedTurn = UpsertAssistantTurn(assistantTurn, $"### Permission denied\n\n{permissionRequest.Summary}");
                deniedTurn = CompleteAssistantTurn(deniedTurn);
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
                var chatBinding = (_profile.ModelBindings ?? []).FirstOrDefault(binding => string.Equals(
                    binding.CapabilityKind,
                    AgentModelCapabilityKinds.Chat,
                    StringComparison.OrdinalIgnoreCase));
                var executionSnapshot = AgentPermissionFingerprint.CreateExecutionSnapshot(
                    _runId,
                    _runRevision,
                    permissionResolution.Descriptor,
                    toolCall.CallId,
                    toolCall.ArgumentsJson,
                    _workspace,
                    permissionResolution.ExecutionBinding,
                    permissionResolution.ExecutionTarget,
                    permissionRequest,
                    _profile,
                    _provider.Descriptor.ProviderId,
                    chatBinding?.ModelId,
                    preparedExecution.OwnerPackageId,
                    preparedInvocation.ExecutionTargetOwnerPackageId,
                    permissionEvaluation,
                    permissionResolution.ExecutionTargetConfigurationGeneration);
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
                        permissionRequest,
                        _profile,
                        _provider.Descriptor.ProviderId,
                        chatBinding?.ModelId,
                        preparedExecution.OwnerPackageId,
                        preparedInvocation.ExecutionTargetOwnerPackageId,
                        permissionResolution.ExecutionTargetConfigurationGeneration),
                    ExecutionSnapshotJson: executionSnapshot)
                {
                    ToolExecutionId = GetToolExecution(toolCall.CallId).ExecutionId,
                    ResourceClaims = GetResourceClaims(permissionRequest),
                };
                try
                {
                    var suspension = _sessionService.SavePendingPermissionRequestAndSuspendRun(
                        pendingRequest,
                        _runLease);
                    if (suspension is null)
                    {
                        throw new InvalidOperationException("The run changed before its permission suspension could be persisted.");
                    }
                    return new AgentToolCallOutcome(
                        AgentToolCallOutcomeKind.WaitingForApproval,
                        suspension.Checkpoint);
                }
                catch
                {
                    _toolService.ReleasePreparedInvocation(preparedExecution.ExecutionId);
                    throw;
                }
            }
        }

        return null;
    }

    internal async Task<AgentToolCallOutcome> HandleApprovedToolCallCoreAsync(
        AgentPendingPermissionRequestRecord pending,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<bool>> beginExecutionAsync)
    {
        var preparedExecution = pending.ToolExecutionId is { } preparedExecutionId
            ? _sessionService.GetToolExecution(preparedExecutionId)
            : null;
        if (preparedExecution is not null
            && IsSamePendingExecution(preparedExecution, pending)
            && _sessionService.GetToolExecutionResult(preparedExecution.ExecutionId) is { } recordedResult)
        {
            _toolService.ReleasePreparedInvocation(preparedExecution.ExecutionId);
            return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Executed, Result: recordedResult);
        }

        var workspace = _workspace;
        if (workspace is null
            || !_toolService.IsCurrentWorkspaceExecutionContext(workspace, _executionBinding))
        {
            return await RecordApprovedSecurityDenialAsync(
                pending,
                "The workspace paths or execution binding changed after permission was requested.",
                AgentToolSecurityErrorCodes.PermissionContextChanged,
                cancellationToken);
        }
        if (string.IsNullOrWhiteSpace(pending.ToolId)
            || preparedExecution is null
            || string.IsNullOrWhiteSpace(preparedExecution.OwnerPackageId)
            || !string.Equals(preparedExecution.ToolId, pending.ToolId, StringComparison.Ordinal))
        {
            return await RecordApprovedSecurityDenialAsync(
                pending,
                $"Approved tool '{pending.ToolId}' is no longer in the ready, assigned tool catalog.",
                AgentToolSecurityErrorCodes.NotAdvertised,
                cancellationToken);
        }

        if (!AgentPermissionFingerprint.TryReadExecutionSnapshot(
                pending.ExecutionSnapshotJson,
                out var advertisedDescriptor,
                out var expectedExecutionTargetConfigurationGeneration)
            || advertisedDescriptor is null
            || !string.Equals(advertisedDescriptor.ToolId, pending.ToolId, StringComparison.OrdinalIgnoreCase)
            || advertisedDescriptor.IsReadOnly != preparedExecution.IsReadOnly)
        {
            return await RecordApprovedSecurityDenialAsync(
                pending,
                "The approved tool execution snapshot is unavailable or invalid.",
                AgentToolSecurityErrorCodes.PermissionContextChanged,
                cancellationToken);
        }
        if (!await _toolService.IsCurrentExecutionTargetConfigurationAsync(
                pending.SessionId,
                _profile.ProfileId,
                workspace,
                _executionBinding,
                expectedExecutionTargetConfigurationGeneration,
                preparedExecution.ExecutionTargetOwnerPackageId,
                cancellationToken).ConfigureAwait(false))
        {
            return await RecordApprovedSecurityDenialAsync(
                pending,
                "The execution-target configuration changed after permission was requested.",
                AgentToolSecurityErrorCodes.PermissionContextChanged,
                cancellationToken);
        }

        var preparedInvocation = await GetOrRebindPreparedInvocationAsync(
            preparedExecution,
            advertisedDescriptor,
            expectedExecutionTargetConfigurationGeneration,
            workspace,
            cancellationToken).ConfigureAwait(false);
        if (preparedInvocation is null)
        {
            return await RecordApprovedSecurityDenialAsync(
                pending,
                $"Approved tool '{pending.ToolId}' no longer has its exact prepared package owners.",
                AgentToolSecurityErrorCodes.NotAdvertised,
                cancellationToken);
        }

        var permissionResolution = await _toolService.ResolvePermissionRequirementAsync(
            pending.ToolId,
            pending.ArgumentsJson,
            pending.SessionId,
            _profile.ProfileId,
            workspace,
            pending.RunId,
            pending.RunRevision,
            pending.UserTurnId,
            pending.CallId,
            advertisedDescriptor,
            preparedExecution.OwnerPackageId,
            preparedInvocation,
            cancellationToken,
            issueOutsideResourceAuthority: false,
            expectedExecutionBinding: _executionBinding,
            enforceExpectedExecutionContext: true,
            requireReadiness: false);
        if (permissionResolution is null || !string.IsNullOrWhiteSpace(permissionResolution.DeniedReason))
        {
            return await RecordApprovedSecurityDenialAsync(
                pending,
                permissionResolution?.DeniedReason
                    ?? $"Approved tool '{pending.ToolId}' is no longer available from its advertised source.",
                AgentToolSecurityErrorCodes.PermissionContextChanged,
                cancellationToken);
        }
        _executionTargetConfigurationGenerationByCallId[pending.CallId] =
            permissionResolution.ExecutionTargetConfigurationGeneration;

        var currentFingerprint = AgentPermissionFingerprint.Create(
            pending.RunId,
            pending.RunRevision,
            permissionResolution.Descriptor,
            pending.CallId,
            pending.ArgumentsJson,
            workspace,
            permissionResolution.ExecutionBinding,
            permissionResolution.ExecutionTarget,
            permissionResolution.PermissionRequest,
            _profile,
            _provider.Descriptor.ProviderId,
            (_profile.ModelBindings ?? []).FirstOrDefault(binding => string.Equals(
                binding.CapabilityKind,
                AgentModelCapabilityKinds.Chat,
                StringComparison.OrdinalIgnoreCase))?.ModelId,
            preparedExecution.OwnerPackageId,
            preparedInvocation.ExecutionTargetOwnerPackageId,
            permissionResolution.ExecutionTargetConfigurationGeneration);
        if (string.IsNullOrWhiteSpace(pending.ExecutionFingerprint)
            || !string.Equals(pending.ExecutionFingerprint, currentFingerprint, StringComparison.Ordinal))
        {
            return await RecordApprovedSecurityDenialAsync(
                pending,
                "The approved tool execution context changed after permission was requested.",
                AgentToolSecurityErrorCodes.PermissionContextChanged,
                cancellationToken);
        }

        SaveCheckpoint(AgentRunStatus.Running, $"Executing approved tool '{pending.ToolId}'.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentRun())
        {
            throw new OperationCanceledException(
                "The approved tool execution was stopped before it began.",
                cancellationToken);
        }

        var executionStopwatch = Stopwatch.StartNew();
        LogEvent(PackageLogLevel.Information, "tool.approved_execution.start", "Executing approved tool.", attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool.id"] = pending.ToolId,
        });
        var allowOutsideConfiguredScope = string.Equals(
            pending.BoundaryId,
            AgentPermissionBoundaryIds.OutsideConfiguredScope,
            StringComparison.OrdinalIgnoreCase);
        var approvedResourceReferences = GetResourceReferences(permissionResolution.PermissionRequest);
        var approvedResourceClaims = GetResourceClaims(permissionResolution.PermissionRequest);
        IReadOnlyList<string> approvedResourceCapabilities = [];
        if (allowOutsideConfiguredScope)
        {
            approvedResourceCapabilities = _toolService.GetPreparedResourceCapabilities(
                preparedExecution.ExecutionId) ?? [];
            if (approvedResourceClaims.Count == 0 || approvedResourceCapabilities.Count == 0)
            {
                return await RecordApprovedSecurityDenialAsync(
                    pending,
                    "Outside-scope resource authority is no longer available in this process; explicit reapproval is required.",
                    AgentToolResultErrorCodes.PermissionReapprovalRequired,
                    cancellationToken);
            }
        }
        var preflightResult = await _toolService.PreflightExecutionAsync(
            pending.ToolId ?? string.Empty,
            pending.ArgumentsJson,
            pending.SessionId,
            _profile.ProfileId,
            _workspace,
            allowOutsideConfiguredScope,
            pending.RunId,
            pending.RunRevision,
            pending.UserTurnId,
            pending.CallId,
            approvedResourceReferences: approvedResourceReferences,
            approvedResourceClaims: approvedResourceClaims,
            approvedResourceCapabilities: approvedResourceCapabilities,
            cancellationToken: cancellationToken,
            advertisedDescriptor: advertisedDescriptor,
            advertisedOwnerPackageId: preparedExecution.OwnerPackageId,
            advertisedInvocation: preparedInvocation,
            expectedExecutionBinding: _executionBinding,
            expectedExecutionTargetConfigurationGeneration:
                permissionResolution.ExecutionTargetConfigurationGeneration,
            enforceExpectedExecutionContext: true);
        if (preflightResult is not null)
        {
            _toolService.ReleasePreparedInvocation(preparedExecution.ExecutionId);
            var deferredTurn = CompleteToolExecution(
                pending,
                preflightResult,
                AgentToolExecutionStatus.Failed,
                preflightResult.ErrorCode ?? "tool-preflight-skipped",
                AgentPendingPermissionStatus.Failed);
            await PublishLifecycleEventAsync(
                AgentLifecycleEventKind.ToolResultRecorded,
                AgentRunStatus.Running,
                triggerTurn: deferredTurn,
                cancellationToken: cancellationToken);
            SaveCheckpoint(AgentRunStatus.Running, preflightResult.Summary);
            return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Executed, Result: preflightResult);
        }
        if (!await beginExecutionAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sessionService.GetToolExecutionResult(preparedExecution.ExecutionId) is { } concurrentResult)
            {
                _toolService.ReleasePreparedInvocation(preparedExecution.ExecutionId);
                return new AgentToolCallOutcome(AgentToolCallOutcomeKind.Executed, Result: concurrentResult);
            }
            return await RecordApprovedSecurityDenialAsync(
                pending,
                "The approved tool execution context changed immediately before execution.",
                AgentToolSecurityErrorCodes.PermissionContextChanged,
                cancellationToken);
        }

        var toolResult = await ExecuteApprovedToolAsync(
            pending,
            permissionResolution.Descriptor,
            preparedExecution.OwnerPackageId,
            preparedInvocation,
            approvedResourceReferences,
            approvedResourceClaims,
            approvedResourceCapabilities,
            cancellationToken);
        LogEvent(
            toolResult.IsError ? PackageLogLevel.Error : PackageLogLevel.Information,
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
            var suspended = pending.ToolExecutionId is { } executionId
                ? _sessionService.SuspendRunAndCompleteToolExecution(
                    _runLease,
                    childJoin!,
                    toolResult.Summary,
                    executionId,
                    toolResult,
                    "child-suspension-durable",
                    pending)
                : _sessionService.SuspendRun(
                    _runLease,
                    childJoin!,
                    toolResult.Summary)
                  ?? throw new InvalidOperationException("The approved run changed before its child-join suspension could be persisted.");

            return new AgentToolCallOutcome(
                AgentToolCallOutcomeKind.WaitingForApproval,
                suspended.Checkpoint,
                toolResult);
        }

        var toolResultTurn = CompleteToolExecution(
            pending,
            toolResult,
            toolResult.IsError ? AgentToolExecutionStatus.Failed : AgentToolExecutionStatus.Completed,
            toolResult.ErrorCode ?? (toolResult.IsError ? "tool-failed" : "tool-completed"),
            toolResult.IsError
                ? AgentPendingPermissionStatus.Failed
                : AgentPendingPermissionStatus.Executed);

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

}
