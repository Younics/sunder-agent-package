using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    // Historical request shapes used only to author/inspect scenario JSON. They
    // carry no dispatcher, client, provider factory or production CLR identity.
    // The generated handlers validate them against the immutable descriptor oracle.
    internal sealed record BehaviorLoopRunRequest(Guid InvocationId, AgentRpcProviderHandle RunControl, AgentBehaviorLoopContext Context);
    private sealed record RunControlInvocation(Guid InvocationId);
    private sealed record RunControlRecentTurnsRequest(RunControlInvocation Invocation, int Limit);
    private sealed record RunControlSessionProjectionRequest(RunControlInvocation Invocation, AgentSessionPromptProjection Projection);
    private sealed record RunControlActivityRequest(RunControlInvocation Invocation, AgentRunActivityKind Kind, string Text);
    private sealed record RunControlBudgetChargeRequest(RunControlInvocation Invocation, AgentRunBudgetCharge Charge);
    private sealed record RunControlPromptContextRequest(RunControlInvocation Invocation, IReadOnlyList<AgentPromptContextReceiptBlock> Blocks);
    private sealed record RunControlCheckpointRequest(RunControlInvocation Invocation, AgentRunStatus Status, string? Summary);
    private sealed record RunControlLogRequest(RunControlInvocation Invocation, PackageLogLevel Level, string EventName, string Message,
        long? ElapsedMilliseconds, IReadOnlyDictionary<string, JsonElement>? Attributes, string? ExceptionType, string? ExceptionMessage);
    private sealed record RunControlAssistantTurnRequest(RunControlInvocation Invocation, AgentTurnRecord? AssistantTurn, string Content);
    private sealed record RunControlCompleteTurnRequest(RunControlInvocation Invocation, AgentTurnRecord AssistantTurn);
    private sealed record RunControlLifecycleRequest(RunControlInvocation Invocation, AgentLifecycleEventKind Kind, AgentRunStatus Status,
        AgentTurnRecord? TriggerTurn, AgentRunCheckpointRecord? Checkpoint, bool IsInterrupted);
    private sealed record RunControlToolRequest(RunControlInvocation Invocation, AgentToolCallRequest ToolCall, AgentTurnRecord? AssistantTurn);
    private sealed record RunControlToolsRequest(RunControlInvocation Invocation, IReadOnlyList<AgentToolCallRequest> ToolCalls, AgentTurnRecord? AssistantTurn);
    private sealed record RunControlDefaultLoopRequest(RunControlInvocation Invocation, AgentBehaviorLoopContext Context);
    private sealed record RunControlChatContext(string ProviderId, string ModelId, IReadOnlyDictionary<string, JsonElement>? CorrelationAttributes);
    private sealed record RunControlChatRequest(RunControlInvocation Invocation, RunControlChatContext Context, IReadOnlyList<AgentChatMessage> Messages, AgentChatOptions Options);

    internal sealed record PromptContextTurnWire(string UserMessage, string? WorkingSummary);
    internal sealed record PromptContextRequestWire(AgentSessionContextRecord Session, AgentRunContextRecord Run, PromptContextTurnWire Turn,
        IReadOnlyList<AgentTurnRecord> Turns, IReadOnlyList<AgentTurnRecord> RecentLiveBufferTurns, AgentPromptContextPlan ContextPlan,
        AgentProfileRecord? Profile, AgentWorkspaceRecord? Workspace, AgentWorkspaceBindingRecord? ExecutionBinding,
        IReadOnlyList<AgentToolDescriptor> AvailableTools, AgentMemoryConsistencyBarrier? MemoryConsistencyBarrier,
        long TranscriptEpoch, AgentRpcProviderHandle? ExecutionTarget)
    {
        // The frozen descriptor still includes the released continuity-summary field.
#pragma warning disable CS0618
        internal static PromptContextRequestWire FromRequest(AgentPromptContextRequest request)
            => new(request.Session, request.Run, new(request.Turn.UserMessage, request.Turn.WorkingSummary), request.Turns,
                request.RecentLiveBufferTurns, request.ContextPlan, request.Profile, request.Workspace, request.ExecutionBinding,
                request.AvailableTools, request.MemoryConsistencyBarrier, request.TranscriptEpoch, request.ExecutionTargetReference?.ToHandle());
#pragma warning restore CS0618

        internal AgentPromptContextRequest DomainRequestWithoutTarget()
        {
            if (ExecutionTarget is not null) throw new InvalidOperationException("The direct domain oracle cannot resolve target authority.");
            return new(Session, Run, new(Session, Run, Turn.UserMessage, Turn.WorkingSummary), Turns, RecentLiveBufferTurns, ContextPlan)
            {
                Profile = Profile, Workspace = Workspace, ExecutionBinding = ExecutionBinding, AvailableTools = AvailableTools,
                MemoryConsistencyBarrier = MemoryConsistencyBarrier, TranscriptEpoch = TranscriptEpoch,
            };
        }
    }
}
