using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

internal sealed record AgentToolExecutionRecord(
    Guid ExecutionId,
    Guid SessionId,
    Guid RunId,
    long RunRevision,
    string CallId,
    string ToolId,
    string InvocationFingerprint,
    bool IsReadOnly,
    AgentToolExecutionStatus Status,
    DateTimeOffset PreparedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? OutcomeCode,
    string? OutcomeSummary,
    string? OwnerPackageId = null,
    string? ToolSchemaId = null,
    string? ToolSchemaVersion = null,
    string? ExecutionTargetOwnerPackageId = null);

internal sealed record AgentToolExecutionPreparation(
    AgentToolCallRequest ToolCall,
    bool IsReadOnly,
    string InvocationFingerprint,
    string? OwnerPackageId = null,
    string? ToolSchemaId = null,
    string? ToolSchemaVersion = null,
    string? ExecutionTargetOwnerPackageId = null);

internal sealed record AgentToolExecutionPreparationResult(
    AgentToolExecutionRecord Execution,
    AgentTurnRecord ToolCallTurn);

internal sealed record AgentToolExecutionStartResult(
    AgentToolExecutionRecord Execution,
    AgentTurnRecord ToolCallTurn);

internal sealed record AgentToolExecutionCompletionResult(
    AgentToolExecutionRecord Execution,
    AgentTurnRecord ToolResultTurn);

internal sealed record AgentToolExecutionSuspensionResult(
    AgentRunSuspensionResult Suspension,
    AgentToolExecutionCompletionResult Completion);
