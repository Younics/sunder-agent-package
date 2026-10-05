using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using Sunder.Package.Agent.Rpc.Public.Chat;

namespace Sunder.Package.Agent.Rpc.Public;

// These shared behavior definitions retain their original required-member order
// and Flag aliases, distinct from the chat provider's same-named model values.
public sealed class AgentModelModeOptionDescriptor
{
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public required Flag DisablesReasoning { get; init; }
    public required DisplayText DisplayName { get; init; }
    public required Identifier ModeOptionId { get; init; }
}

public sealed class AgentModelSpeedOptionDescriptor
{
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public required DisplayText DisplayName { get; init; }
    public required Identifier SpeedOptionId { get; init; }
}

public sealed class AgentModelVariantDescriptor
{
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public required DisplayText DisplayName { get; init; }
    public SunderRpcOptional<AgentReasoningEffort> ReasoningEffort { get; init; }
    public required Identifier VariantId { get; init; }
}

public sealed class AgentBehaviorLoopContext
{
    public SunderRpcOptional<AgentWorkspaceBindingRecord> ExecutionBinding { get; init; }
    public required Identifier ModelId { get; init; }
    public SunderRpcOptional<AgentModelModeOptionDescriptor> ModelModeOption { get; init; }
    public SunderRpcOptional<AgentModelSpeedOptionDescriptor> ModelSpeedOption { get; init; }
    public SunderRpcOptional<AgentModelVariantDescriptor> ModelVariant { get; init; }
    public required AgentProfileRecord Profile { get; init; }
    public required Identifier ProviderId { get; init; }
    public required AgentProviderRunCapabilities RunCapabilities { get; init; }
    public required EntityId RunId { get; init; }
    public required Revision RunRevision { get; init; }
    public required Timestamp RunStartedAtUtc { get; init; }
    public required AgentRunCheckpointRecord RunningCheckpoint { get; init; }
    public required AgentSessionRecord Session { get; init; }
    public required ContentText UserMessage { get; init; }
    public required EntityId UserTurnId { get; init; }
    public SunderRpcOptional<AgentWorkspaceRecord> Workspace { get; init; }
}

public enum AgentBehaviorLoopCompletionKind
{
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("waiting-for-approval")] WaitingForApproval,
    [JsonStringEnumMemberName("stopped")] Stopped,
    [JsonStringEnumMemberName("interrupted")] Interrupted,
}

public sealed class AgentBehaviorLoopResult
{
    public required AgentRunCheckpointRecord Checkpoint { get; init; }
    public required AgentBehaviorLoopCompletionKind CompletionKind { get; init; }
}
