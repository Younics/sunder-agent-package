using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

public sealed class ToolSourceDescription
{
    public required DisplayText DisplayName { get; init; }
    public required Identifier SourceId { get; init; }
    public required TextValue SourceKind { get; init; }
}

public sealed class AgentToolSourceContext
{
    public SunderRpcOptional<AgentWorkspaceBindingRecord> ExecutionBinding { get; init; }
    public SunderRpcOptional<AgentProfileRecord> Profile { get; init; }
    public SunderRpcOptional<EntityId> SessionId { get; init; }
    public SunderRpcOptional<AgentWorkspaceRecord> Workspace { get; init; }
}

public sealed class AgentToolSourceContextWire
{
    public required AgentToolSourceContext Context { get; init; }
    public SunderRpcOptional<AgentRpcProviderHandle> ExecutionTarget { get; init; }
    public SunderRpcOptional<TextValue> ExecutionTargetConfigurationGeneration { get; init; }
}

public sealed class AgentToolActivationRequirement
{
    public SunderRpcOptional<Identifier> CapabilityId { get; init; }
    public required TextValue CapabilityKind { get; init; }
    public SunderRpcOptional<Identifier> SourceId { get; init; }
}

public enum AgentToolConcurrencyMode
{
    [JsonStringEnumMemberName("sequential")] Sequential,
    [JsonStringEnumMemberName("parallel-safe")] ParallelSafe,
}

public enum AgentToolPriority
{
    [JsonStringEnumMemberName("low")] Low,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("high")] High,
}

public enum AgentToolSelectionScope
{
    [JsonStringEnumMemberName("tool")] Tool,
    [JsonStringEnumMemberName("group")] Group,
}

public sealed class AgentToolDescriptor
{
    public SunderRpcOptional<AgentToolActivationRequirement> ActivationRequirement { get; init; }
    public SunderRpcOptional<TextValues> Aliases { get; init; }
    public SunderRpcOptional<TextValue> ArgumentsJsonSchema { get; init; }
    public required AgentToolConcurrencyMode ConcurrencyMode { get; init; }
    public required DisplayText Description { get; init; }
    public required DisplayText DisplayName { get; init; }
    public required Flag IsReadOnly { get; init; }
    public required AgentToolPriority Priority { get; init; }
    public required Flag RequiresNetwork { get; init; }
    public SunderRpcOptional<ContentText> RuntimeInstructions { get; init; }
    public SunderRpcOptional<DisplayText> SelectionGroupDescription { get; init; }
    public SunderRpcOptional<DisplayText> SelectionGroupDisplayName { get; init; }
    public SunderRpcOptional<Identifier> SelectionGroupId { get; init; }
    public required AgentToolSelectionScope SelectionScope { get; init; }
    public SunderRpcOptional<DisplayText> SourceDisplayName { get; init; }
    public SunderRpcOptional<Identifier> SourceId { get; init; }
    public SunderRpcOptional<TextValue> SourceKind { get; init; }
    public required Identifier ToolId { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentToolDescriptorCollection
{
    public required IReadOnlyList<AgentToolDescriptor> Value { get; init; }
}

public sealed class ToolReadinessRequest
{
    public required AgentToolSourceContextWire Context { get; init; }
    public required Identifier ToolId { get; init; }
}

public enum AgentToolReadinessStatus
{
    [JsonStringEnumMemberName("ready")] Ready,
    [JsonStringEnumMemberName("failed")] Failed,
}

public sealed class AgentToolReadiness
{
    public required DisplayText Message { get; init; }
    public required AgentToolReadinessStatus Status { get; init; }
    public required Identifier ToolId { get; init; }
}

[SunderRpcTransparent]
public sealed class ToolReadinessResult
{
    public required AgentToolReadiness? Value { get; init; }
}

public sealed class AgentToolExecutionContext
{
    public required Flag AllowOutsideConfiguredScope { get; init; }
    public required AgentResourceClaimCollection ApprovedResourceClaims { get; init; }
    public required TextValues ApprovedResourceReferences { get; init; }
    public SunderRpcOptional<AgentWorkspaceBindingRecord> ExecutionBinding { get; init; }
    public SunderRpcOptional<Identifier> ProfileId { get; init; }
    public SunderRpcOptional<EntityId> RunId { get; init; }
    public SunderRpcOptional<Revision> RunRevision { get; init; }
    public SunderRpcOptional<EntityId> SessionId { get; init; }
    public SunderRpcOptional<Identifier> ToolCallId { get; init; }
    public required Revision TranscriptEpoch { get; init; }
    public SunderRpcOptional<EntityId> UserTurnId { get; init; }
    public SunderRpcOptional<AgentWorkspaceRecord> Workspace { get; init; }
}

public sealed class AgentToolExecutionContextWire
{
    public required TextValues ApprovedResourceCapabilities { get; init; }
    public required AgentToolExecutionContext Context { get; init; }
    public SunderRpcOptional<AgentRpcProviderHandle> ExecutionTarget { get; init; }
    public SunderRpcOptional<TextValue> ExecutionTargetConfigurationGeneration { get; init; }
    public SunderRpcOptional<AgentResourceOperationContextWire> ResourceOperation { get; init; }
}

public sealed class AgentToolRequest
{
    public required JsonText ArgumentsJson { get; init; }
    public required Identifier ToolId { get; init; }
}

public sealed class ToolInvocationRequest
{
    public required AgentToolExecutionContextWire Context { get; init; }
    public required AgentToolRequest Request { get; init; }
}

public sealed class AgentToolSourceItem
{
    public SunderRpcOptional<DisplayText> Snippet { get; init; }
    public required DisplayText Title { get; init; }
    public required TextValue Url { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentToolSourceItemCollection
{
    public required IReadOnlyList<AgentToolSourceItem> Value { get; init; }
}

public sealed class AgentToolResult
{
    public SunderRpcOptional<Identifier> BackendId { get; init; }
    public SunderRpcOptional<ContentText> Content { get; init; }
    public SunderRpcOptional<TextValue> ErrorCode { get; init; }
    public required Flag IsError { get; init; }
    public SunderRpcOptional<JsonText> PresentationPayloadJson { get; init; }
    public required Flag RequiresPromptContextRefresh { get; init; }
    public SunderRpcOptional<AgentToolSourceItemCollection> Sources { get; init; }
    public SunderRpcOptional<JsonText> StructuredPayloadJson { get; init; }
    public required DisplayText Summary { get; init; }
    public required Identifier ToolId { get; init; }
    public required Flag WasTruncated { get; init; }
}

[SunderRpcTransparent]
public sealed class ToolPreflightResult
{
    public required AgentToolResult? Value { get; init; }
}

public sealed class AgentPermissionRequest
{
    public required Identifier ActionId { get; init; }
    public SunderRpcOptional<Identifier> BindingId { get; init; }
    public required Identifier BoundaryId { get; init; }
    public SunderRpcOptional<CommandText> Command { get; init; }
    public required Flag IsMutation { get; init; }
    public SunderRpcOptional<PathText> Path { get; init; }
    public required AgentResourceClaimCollection ResourceClaims { get; init; }
    public SunderRpcOptional<DisplayText> ResourceDisplayName { get; init; }
    public SunderRpcOptional<TextValue> ResourceReference { get; init; }
    public required TextValues ResourceReferences { get; init; }
    public required AgentPermissionScopeClassificationBasis ScopeClassificationBasis { get; init; }
    public required DisplayText Summary { get; init; }
    public SunderRpcOptional<Identifier> ToolId { get; init; }
    public SunderRpcOptional<Identifier> WorkspaceId { get; init; }
}

public sealed class AgentPermissionRequestWire
{
    public required AgentPermissionRequest Request { get; init; }
    public required TextValues ResourceCapabilities { get; init; }
}

[SunderRpcTransparent]
public sealed class PermissionRequestResult
{
    public required AgentPermissionRequestWire? Value { get; init; }
}

public sealed class AgentToolPresentationRequest
{
    public required JsonText ArgumentsJson { get; init; }
    public SunderRpcOptional<Identifier> BackendId { get; init; }
    public SunderRpcOptional<TextValue> ErrorCode { get; init; }
    public required Flag IsError { get; init; }
    public SunderRpcOptional<DisplayText> ResultSummary { get; init; }
    public SunderRpcOptional<JsonText> SourcesJson { get; init; }
    public SunderRpcOptional<JsonText> StructuredPayloadJson { get; init; }
    public SunderRpcOptional<ContentText> TextContent { get; init; }
    public required Identifier ToolId { get; init; }
}

public sealed class AgentToolPresentation
{
    public SunderRpcOptional<ContentText> DetailMarkdown { get; init; }
    public SunderRpcOptional<DisplayText> HeaderText { get; init; }
    public SunderRpcOptional<ContentText> OutputText { get; init; }
}

[SunderRpcTransparent]
public sealed class ToolPresentationResult
{
    public required AgentToolPresentation? Value { get; init; }
}
