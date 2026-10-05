using System.Text.Json.Serialization;
using Sunder.Package.Agent.Rpc.Public.Chat;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public.Control;

public sealed class RunControlInvocation { public required EntityId InvocationId { get; init; } }

public sealed class RunControlRecentTurnsRequest
{
    public required RunControlInvocation Invocation { get; init; }
    public required Catalog.PageSize Limit { get; init; }
}

public sealed class RunControlSessionProjectionRequest
{
    public required RunControlInvocation Invocation { get; init; }
    public required AgentSessionPromptProjection Projection { get; init; }
}

public enum AgentRunActivityKind
{
    [JsonStringEnumMemberName("thinking")] Thinking,
    [JsonStringEnumMemberName("reasoning")] Reasoning,
    [JsonStringEnumMemberName("tool")] Tool,
    [JsonStringEnumMemberName("processing")] Processing,
}

public sealed class RunControlActivityRequest
{
    public required RunControlInvocation Invocation { get; init; }
    public required AgentRunActivityKind Kind { get; init; }
    public required TextValue Text { get; init; }
}

public sealed class AgentRunBudgetState
{
    public required Count ProviderCycles { get; init; }
    public required TokenCount SubmittedContextTokens { get; init; }
    public required Count ToolCalls { get; init; }
}

public sealed class AgentRunBudgetCharge
{
    public required Count ProviderCycles { get; init; }
    public required TokenCount SubmittedContextTokens { get; init; }
    public required Count ToolCalls { get; init; }
}

public sealed class RunControlBudgetChargeRequest
{
    public required AgentRunBudgetCharge Charge { get; init; }
    public required RunControlInvocation Invocation { get; init; }
}

public sealed class RunControlPromptContextRequest
{
    public required AgentPromptContextReceiptBlockCollection Blocks { get; init; }
    public required RunControlInvocation Invocation { get; init; }
}

public sealed class RunControlCheckpointRequest
{
    public required RunControlInvocation Invocation { get; init; }
    public required AgentRunStatus Status { get; init; }
    public SunderRpcOptional<DisplayText> Summary { get; init; }
}

public enum PackageLogLevel
{
    [JsonStringEnumMemberName("trace")] Trace,
    [JsonStringEnumMemberName("debug")] Debug,
    [JsonStringEnumMemberName("information")] Information,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("critical")] Critical,
}

[SunderRpcTransparent, SunderRpcFacet(Minimum = "0")]
public readonly record struct ElapsedMilliseconds { public required long Value { get; init; } }

public sealed class RunControlLogRequest
{
    public SunderRpcOptional<JsonValues> Attributes { get; init; }
    public SunderRpcOptional<ElapsedMilliseconds> ElapsedMilliseconds { get; init; }
    public required Identifier EventName { get; init; }
    public SunderRpcOptional<DisplayText> ExceptionMessage { get; init; }
    public SunderRpcOptional<TextValue> ExceptionType { get; init; }
    public required RunControlInvocation Invocation { get; init; }
    public required PackageLogLevel Level { get; init; }
    public required DisplayText Message { get; init; }
}

public sealed class RunControlAssistantTurnRequest
{
    public SunderRpcOptional<AgentTurnRecord> AssistantTurn { get; init; }
    public required ContentText Content { get; init; }
    public required RunControlInvocation Invocation { get; init; }
}

public sealed class RunControlCompleteTurnRequest
{
    public required AgentTurnRecord AssistantTurn { get; init; }
    public required RunControlInvocation Invocation { get; init; }
}

public sealed class RunControlLifecycleRequest
{
    public SunderRpcOptional<AgentRunCheckpointRecord> Checkpoint { get; init; }
    public required RunControlInvocation Invocation { get; init; }
    public required Flag IsInterrupted { get; init; }
    public required AgentLifecycleEventKind Kind { get; init; }
    public required AgentRunStatus Status { get; init; }
    public SunderRpcOptional<AgentTurnRecord> TriggerTurn { get; init; }
}

public sealed class AgentToolCallRequest
{
    public required JsonText ArgumentsJson { get; init; }
    public required Identifier CallId { get; init; }
    public required Identifier ToolId { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentToolCallRequestCollection { public required IReadOnlyList<AgentToolCallRequest> Value { get; init; } }

public sealed class RunControlToolRequest
{
    public SunderRpcOptional<AgentTurnRecord> AssistantTurn { get; init; }
    public required RunControlInvocation Invocation { get; init; }
    public required AgentToolCallRequest ToolCall { get; init; }
}

public sealed class RunControlToolsRequest
{
    public SunderRpcOptional<AgentTurnRecord> AssistantTurn { get; init; }
    public required RunControlInvocation Invocation { get; init; }
    public required AgentToolCallRequestCollection ToolCalls { get; init; }
}

public enum AgentToolCallOutcomeKind
{
    [JsonStringEnumMemberName("executed")] Executed,
    [JsonStringEnumMemberName("waiting-for-approval")] WaitingForApproval,
    [JsonStringEnumMemberName("denied")] Denied,
    [JsonStringEnumMemberName("failed")] Failed,
}

public sealed class AgentToolCallOutcome
{
    public SunderRpcOptional<AgentRunCheckpointRecord> Checkpoint { get; init; }
    public required AgentToolCallOutcomeKind Kind { get; init; }
    public SunderRpcOptional<AgentToolResult> Result { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentToolCallOutcomeCollection { public required IReadOnlyList<AgentToolCallOutcome> Value { get; init; } }

public sealed class RunControlDefaultLoopRequest
{
    public required AgentBehaviorLoopContext Context { get; init; }
    public required RunControlInvocation Invocation { get; init; }
}
