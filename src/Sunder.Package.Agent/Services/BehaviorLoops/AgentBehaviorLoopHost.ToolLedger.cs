using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed partial class AgentBehaviorLoopHost
{
    private AgentTurnRecord CompleteToolExecution(
        AgentToolCallRequest call,
        AgentToolResult result,
        AgentToolExecutionStatus status,
        string outcomeCode,
        bool allowPreparedReadOnlyCompletion = false)
        => _sessionService.CompleteToolExecution(
            _runLease,
            GetToolExecution(call.CallId).ExecutionId,
            status,
            result,
            outcomeCode,
            allowPreparedReadOnlyCompletion);

    private AgentTurnRecord CompleteToolExecution(
        AgentPendingPermissionRequestRecord pending,
        AgentToolResult result,
        AgentToolExecutionStatus status,
        string outcomeCode,
        AgentPendingPermissionStatus permissionStatus)
        => pending.ToolExecutionId is { } executionId
            ? _sessionService.CompleteToolExecution(
                _runLease,
                executionId,
                status,
                result,
                outcomeCode,
                permissionRequest: pending,
                permissionStatus: permissionStatus)
            : _sessionService.AppendToolResultTurn(
                _runLease,
                pending.CallId,
                pending.ToolId ?? string.Empty,
                pending.ArgumentsJson,
                result.Content,
                result.Summary,
                result.StructuredPayloadJson,
                result.Sources is null ? null : JsonSerializer.Serialize(result.Sources),
                result.WasTruncated,
                result.IsError,
                result.ErrorCode,
                result.BackendId,
                result.PresentationPayloadJson);

    internal Task PrepareToolExecutionsAsync(
        IReadOnlyList<AgentToolCallRequest> toolCalls,
        IReadOnlyDictionary<string, AgentToolDescriptor> availableToolsById,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invocationsByCallId = new Dictionary<string, AgentToolInvocationReference>(StringComparer.Ordinal);
        var preparations = toolCalls.Select(call =>
        {
            var isAdvertised = availableToolsById.TryGetValue(call.ToolId, out var descriptor);
            AgentOwnedRuntimeTool? advertisedOwnedTool = null;
            _availableOwnedToolsById?.TryGetValue(call.ToolId, out advertisedOwnedTool);
            var hasAdvertisedOwner = advertisedOwnedTool is not null;
            var hasVerifiedOwner = isAdvertised
                                    && hasAdvertisedOwner
                                    && AgentToolService.IsInvocationAvailable(advertisedOwnedTool!.Invocation)
                                    && IsSamePreparedTool(
                                        descriptor!,
                                        advertisedOwnedTool.RuntimeTool.Descriptor);
            if (hasVerifiedOwner)
            {
                invocationsByCallId[call.CallId] = advertisedOwnedTool!.Invocation;
            }
            return new AgentToolExecutionPreparation(
                call,
                isAdvertised && descriptor!.IsReadOnly,
                AgentToolInvocationFingerprint.Create(call.ToolId, call.ArgumentsJson),
                hasVerifiedOwner ? advertisedOwnedTool!.OwnerPackageId : null,
                hasVerifiedOwner ? advertisedOwnedTool!.ToolSchemaId : null,
                hasVerifiedOwner ? advertisedOwnedTool!.ToolSchemaVersion : null,
                hasVerifiedOwner
                    ? advertisedOwnedTool!.Invocation.ExecutionTargetOwnerPackageId
                    : null);
        }).ToArray();
        var persisted = _sessionService.PrepareToolExecutions(_runLease, preparations);
        foreach (var result in persisted)
        {
            _toolExecutionsByCallId.Add(result.Execution.CallId, result.Execution);
            if (invocationsByCallId.TryGetValue(result.Execution.CallId, out var invocation))
            {
                _toolService.BindPreparedInvocation(result.Execution.ExecutionId, invocation);
            }
        }
        return Task.CompletedTask;
    }

    private static bool IsSamePreparedTool(
        AgentToolDescriptor advertised,
        AgentToolDescriptor current)
        => string.Equals(advertised.ToolId, current.ToolId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(advertised.SourceKind, current.SourceKind, StringComparison.OrdinalIgnoreCase)
           && string.Equals(advertised.SourceId, current.SourceId, StringComparison.OrdinalIgnoreCase)
           && advertised.IsReadOnly == current.IsReadOnly;

    private async ValueTask<AgentToolInvocationReference?> GetOrRebindPreparedInvocationAsync(
        AgentToolExecutionRecord execution,
        AgentToolDescriptor advertisedDescriptor,
        string? executionTargetConfigurationGeneration,
        AgentWorkspaceRecord workspace,
        CancellationToken cancellationToken)
    {
        var prepared = _toolService.GetPreparedInvocation(execution.ExecutionId);
        if (prepared is not null)
        {
            return IsExactPreparedOwner(execution, advertisedDescriptor, prepared)
                ? prepared
                : null;
        }

        if (execution.Status != AgentToolExecutionStatus.Prepared
            || string.IsNullOrWhiteSpace(execution.OwnerPackageId))
        {
            return null;
        }

        var current = await _toolService.ResolvePreparedInvocationAsync(
            advertisedDescriptor,
            execution.OwnerPackageId,
            execution.ExecutionTargetOwnerPackageId,
            _profile,
            _session.SessionId,
            workspace,
            executionTargetConfigurationGeneration,
            cancellationToken).ConfigureAwait(false);
        if (current is null || !IsExactPreparedOwner(execution, advertisedDescriptor, current))
        {
            return null;
        }

        _toolService.BindPreparedInvocation(execution.ExecutionId, current);
        return current;
    }

    private static bool IsExactPreparedOwner(
        AgentToolExecutionRecord execution,
        AgentToolDescriptor advertisedDescriptor,
        AgentToolInvocationReference invocation)
        => !string.IsNullOrWhiteSpace(execution.OwnerPackageId)
           && string.Equals(execution.OwnerPackageId, invocation.OwnerPackageId, StringComparison.Ordinal)
           && string.Equals(
               execution.ExecutionTargetOwnerPackageId,
               invocation.ExecutionTargetOwnerPackageId,
               StringComparison.Ordinal)
           && IsSamePreparedTool(advertisedDescriptor, invocation.Descriptor)
           && AgentToolService.IsInvocationAvailable(invocation);

    private static bool IsSamePendingExecution(
        AgentToolExecutionRecord execution,
        AgentPendingPermissionRequestRecord pending)
        => execution.ExecutionId == pending.ToolExecutionId
           && execution.SessionId == pending.SessionId
           && execution.RunId == pending.RunId
           && execution.RunRevision == pending.RunRevision
           && string.Equals(execution.CallId, pending.CallId, StringComparison.Ordinal)
           && string.Equals(execution.ToolId, pending.ToolId, StringComparison.Ordinal)
           && string.Equals(
               execution.InvocationFingerprint,
               AgentToolInvocationFingerprint.Create(pending.ToolId ?? string.Empty, pending.ArgumentsJson),
               StringComparison.Ordinal);

    internal AgentToolExecutionRecord GetToolExecution(string callId)
        => _toolExecutionsByCallId.TryGetValue(callId, out var execution)
            ? execution
            : throw new InvalidOperationException($"Tool call '{callId}' has no prepared execution ledger row.");

    internal async ValueTask<IReadOnlyDictionary<string, AgentToolDescriptor>> GetAvailableToolsByIdAsync(
        CancellationToken cancellationToken)
    {
        if (_availableToolsById is null)
        {
            await ListReadyToolsAsync(cancellationToken);
        }

        return _availableToolsById ?? new Dictionary<string, AgentToolDescriptor>(StringComparer.OrdinalIgnoreCase);
    }
}
