using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.selectable.capability.provider",
    ServiceId = "selectable-capability-provider", IncludeSchemaDialect = true)]
[SunderRpcRuntimeUse(Actions = ["discover", "invoke", "subscribe"])]
public interface SelectableCapabilityProviderService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentRpcIdentity> DescribeAsync(Unit request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentProfileSelectableCapabilityDescriptorCollection> ListAsync(AgentProfileSelectableCapabilityRequest request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    IAsyncEnumerable<AgentRpcChange> WatchAsync(Unit request, CancellationToken cancellationToken = default);
}

[SunderRpcTransparent, SunderRpcFacet(Description = "Opaque JSON text that may be malformed, truncated, or interpreted by an extension-owned schema.")]
public readonly record struct JsonText
{
    public required string Value { get; init; }
}

public sealed class AgentProfileModelBindingRecord
{
    public required TextValue CapabilityKind { get; init; }
    public SunderRpcOptional<Identifier> ModelId { get; init; }
    public required Identifier ProfileId { get; init; }
    public SunderRpcOptional<Identifier> ProviderId { get; init; }
    public SunderRpcOptional<JsonText> SettingsJson { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentProfileModelBindingRecordCollection
{
    public required IReadOnlyList<AgentProfileModelBindingRecord> Value { get; init; }
}

public sealed class AgentProfileSelectableCapabilityAssignmentRecord
{
    public required Identifier CapabilityId { get; init; }
    public required Identifier Kind { get; init; }
    public SunderRpcOptional<Identifier> SourceId { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentProfileSelectableCapabilityAssignmentRecordCollection
{
    public required IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> Value { get; init; }
}

public sealed class AgentProfileRecord
{
    public SunderRpcOptional<Identifier> BehaviorLoopId { get; init; }
    public SunderRpcOptional<JsonText> BehaviorLoopSettingsJson { get; init; }
    public SunderRpcOptional<Identifier> BehaviorLoopSourceId { get; init; }
    public SunderRpcOptional<Identifier> ChatModelId { get; init; }
    public SunderRpcOptional<Identifier> ChatProviderId { get; init; }
    public required Timestamp CreatedAtUtc { get; init; }
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public required DisplayText DisplayName { get; init; }
    public SunderRpcOptional<Identifier> EmbeddingModelId { get; init; }
    public SunderRpcOptional<Identifier> EmbeddingProviderId { get; init; }
    public SunderRpcOptional<ContentText> Instructions { get; init; }
    public required Flag IsInternal { get; init; }
    public SunderRpcOptional<AgentProfileModelBindingRecordCollection> ModelBindings { get; init; }
    public required Identifier ProfileId { get; init; }
    public SunderRpcOptional<AgentProfileSelectableCapabilityAssignmentRecordCollection> SelectableCapabilityAssignments { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
}

public sealed class AgentProfileSelectableCapabilityRequest
{
    public SunderRpcOptional<AgentProfileRecord> Profile { get; init; }
}

public sealed class AgentProfileSelectableCapabilityDescriptor
{
    public required Identifier CapabilityId { get; init; }
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public required DisplayText DisplayName { get; init; }
    public SunderRpcOptional<DisplayText> GroupDescription { get; init; }
    public SunderRpcOptional<DisplayText> GroupDisplayName { get; init; }
    public SunderRpcOptional<Identifier> GroupId { get; init; }
    public required SortOrder GroupSortOrder { get; init; }
    public required Flag IsSelectable { get; init; }
    public required Identifier Kind { get; init; }
    public SunderRpcOptional<DisplayText> SourceDisplayName { get; init; }
    public SunderRpcOptional<Identifier> SourceId { get; init; }
    public SunderRpcOptional<DisplayText> StatusText { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentProfileSelectableCapabilityDescriptorCollection
{
    public required IReadOnlyList<AgentProfileSelectableCapabilityDescriptor> Value { get; init; }
}

public sealed class AgentRpcChange
{
    public required Revision Revision { get; init; }
}
