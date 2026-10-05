using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using Sunder.Package.Agent.Rpc.Public.Catalog;

namespace Sunder.Package.Agent.Rpc.Public;

public enum AgentPromptContextAuthority
{
    [JsonStringEnumMemberName("reference")] Reference,
    [JsonStringEnumMemberName("standing-instruction")] StandingInstruction,
    [JsonStringEnumMemberName("scoped-instruction")] ScopedInstruction,
}

public enum AgentContextProvenance
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("user")] User,
    [JsonStringEnumMemberName("transcript-summary")] TranscriptSummary,
    [JsonStringEnumMemberName("assistant")] Assistant,
    [JsonStringEnumMemberName("tool")] Tool,
    [JsonStringEnumMemberName("extension")] Extension,
    [JsonStringEnumMemberName("durable-memory")] DurableMemory,
}

public enum AgentContextTrust
{
    [JsonStringEnumMemberName("untrusted")] Untrusted,
    [JsonStringEnumMemberName("user-provided")] UserProvided,
}

public enum AgentPromptContextUsage
{
    [JsonStringEnumMemberName("reference")] Reference,
    [JsonStringEnumMemberName("scoped-instruction")] ScopedInstruction,
    [JsonStringEnumMemberName("standing-instruction")] StandingInstruction,
}

public sealed class AgentPromptContextScope
{
    public required PathText AppliesToDirectory { get; init; }
    public required Sha256Text ContentHash { get; init; }
    public required TextValue ContextIdentity { get; init; }
    public required PathText DocumentPath { get; init; }
    public required PathText ScopeRoot { get; init; }
}

public sealed class AgentPromptContextBlock
{
    public required AgentPromptContextAuthority Authority { get; init; }
    public required ContentText Content { get; init; }
    public SunderRpcOptional<TextValue> HostIdentity { get; init; }
    public required SortOrder Priority { get; init; }
    public required AgentContextProvenance Provenance { get; init; }
    public SunderRpcOptional<AgentPromptContextScope> Scope { get; init; }
    public SunderRpcOptional<Identifier> SourceId { get; init; }
    public required DisplayText Title { get; init; }
    public required AgentContextTrust Trust { get; init; }
    public required AgentPromptContextUsage Usage { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentPromptContextBlockCollection { public required IReadOnlyList<AgentPromptContextBlock> Value { get; init; } }

public sealed class AgentBehaviorInstructionContext
{
    public required Flag HasSupplementaryContext { get; init; }
    public SunderRpcOptional<AgentPromptContextBlockCollection> SupplementaryContextBlocks { get; init; }
    public SunderRpcOptional<ContentText> SystemInstructions { get; init; }
}

public sealed class AgentSessionPromptProjection
{
    public SunderRpcOptional<AgentSessionContextCheckpointRecord> ContextCheckpoint { get; init; }
    public required Count OmittedHistoricalTurnCount { get; init; }
    public required AgentTurnRecordCollection PromptTurns { get; init; }
    public required Flag SummaryUpdated { get; init; }
}

public sealed class AgentPromptContextReceiptBlock
{
    public required Sha256Text ContentHash { get; init; }
    public required TextValue ContextIdentity { get; init; }
    public required PathText DocumentPath { get; init; }
    public required TextValue HostIdentity { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentPromptContextReceiptBlockCollection { public required IReadOnlyList<AgentPromptContextReceiptBlock> Value { get; init; } }
