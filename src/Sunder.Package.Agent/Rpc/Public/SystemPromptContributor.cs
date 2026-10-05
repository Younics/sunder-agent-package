using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.system.prompt.contributor",
    ServiceId = "system-prompt-contributor", IncludeSchemaDialect = true)]
[SunderRpcRuntimeUse(Actions = ["discover", "invoke"])]
public interface SystemPromptContributorService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentRpcIdentity> DescribeAsync(Unit request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentSystemPromptBlockCollection> ContributeAsync(AgentSystemPromptRequest request, CancellationToken cancellationToken = default);
}

public sealed class AgentProviderRunCapabilities
{
    public SunderRpcOptional<TokenCount> ContextWindowTokens { get; init; }
    public SunderRpcOptional<TokenCount> MaxOutputTokens { get; init; }
    public required DisplayText Summary { get; init; }
    public required Flag SupportsAudioInput { get; init; }
    public required Flag SupportsImageInput { get; init; }
    public required Flag SupportsMultipleToolCalls { get; init; }
    public required Flag SupportsNativeToolCalling { get; init; }
    public required Flag SupportsPdfInput { get; init; }
    public required Flag SupportsStreamingToolCalls { get; init; }
    public required Flag SupportsVideoInput { get; init; }
}

public sealed class AgentSystemPromptRequest
{
    public required AgentToolDescriptorCollection AvailableTools { get; init; }
    public SunderRpcOptional<AgentWorkspaceBindingRecord> ExecutionBinding { get; init; }
    public required Identifier ModelId { get; init; }
    public required AgentProfileRecord Profile { get; init; }
    public required Identifier ProviderId { get; init; }
    public required AgentProviderRunCapabilities RunCapabilities { get; init; }
    public required EntityId RunId { get; init; }
    public required Revision RunRevision { get; init; }
    public required Timestamp RunStartedAtUtc { get; init; }
    public required AgentSessionRecord Session { get; init; }
    public required AgentTurnRecordCollection Turns { get; init; }
    public required ContentText UserMessage { get; init; }
    public SunderRpcOptional<AgentWorkspaceRecord> Workspace { get; init; }
}

public sealed class AgentSystemPromptBlock
{
    public required Identifier BlockId { get; init; }
    public required ContentText Content { get; init; }
    public SunderRpcOptional<CharacterCount> MaxChars { get; init; }
    public SunderRpcOptional<TextMetadata> Metadata { get; init; }
    public required SortOrder Priority { get; init; }
    public required Flag Required { get; init; }
    public SunderRpcOptional<Identifier> SourceId { get; init; }
    public required DisplayText Title { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentSystemPromptBlockCollection
{
    public required IReadOnlyList<AgentSystemPromptBlock> Value { get; init; }
}
