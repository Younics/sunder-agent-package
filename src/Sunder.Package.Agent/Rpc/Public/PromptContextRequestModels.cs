using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

public sealed class AgentPromptContextPlan
{
    public required TextValue Intent { get; init; }
    public required CharacterCount MaxChars { get; init; }
    public required Count MaxEntryCount { get; init; }
    public SunderRpcOptional<TextValues> PreferredCategories { get; init; }
    public required TextValue QueryText { get; init; }
    public SunderRpcOptional<DisplayText> Reason { get; init; }
    public required Flag ShouldContribute { get; init; }
}

public sealed class PromptContextTurnWire
{
    public required ContentText UserMessage { get; init; }
    public SunderRpcOptional<DisplayText> WorkingSummary { get; init; }
}

public sealed class PromptContextRequestWire
{
    public required AgentToolDescriptorCollection AvailableTools { get; init; }
    public required AgentPromptContextPlan ContextPlan { get; init; }
    public SunderRpcOptional<AgentWorkspaceBindingRecord> ExecutionBinding { get; init; }
    public SunderRpcOptional<AgentRpcProviderHandle> ExecutionTarget { get; init; }
    public SunderRpcOptional<AgentMemoryConsistencyBarrier> MemoryConsistencyBarrier { get; init; }
    public SunderRpcOptional<AgentProfileRecord> Profile { get; init; }
    public required AgentTurnRecordCollection RecentLiveBufferTurns { get; init; }
    public required AgentRunContextRecord Run { get; init; }
    public required AgentSessionContextRecord Session { get; init; }
    public required Revision TranscriptEpoch { get; init; }
    public required PromptContextTurnWire Turn { get; init; }
    public required AgentTurnRecordCollection Turns { get; init; }
    public SunderRpcOptional<AgentWorkspaceRecord> Workspace { get; init; }
}

public sealed class AgentPromptContextReceipt
{
    public required AgentPromptContextReceiptBlockCollection Blocks { get; init; }
    public required EntityId RunId { get; init; }
    public required EntityId SessionId { get; init; }
    public required Revision TranscriptEpoch { get; init; }
}
