using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public.Catalog;

[SunderRpcTransparent, SunderRpcFacet(Minimum = "1", Maximum = "1000")]
public readonly record struct PageSize { public required long Value { get; init; } }

[SunderRpcTransparent]
public sealed class AgentSessionRecordCollection { public required IReadOnlyList<AgentSessionRecord> Value { get; init; } }
[SunderRpcTransparent]
public sealed class AgentProfileRecordCollection { public required IReadOnlyList<AgentProfileRecord> Value { get; init; } }
[SunderRpcTransparent]
public sealed class SessionLookupResult { public required AgentSessionRecord? Value { get; init; } }
[SunderRpcTransparent]
public sealed class WorkspaceLookupResult { public required AgentWorkspaceRecord? Value { get; init; } }
[SunderRpcTransparent]
public sealed class ProfileLookupResult { public required AgentProfileRecord? Value { get; init; } }
[SunderRpcTransparent]
public sealed class WorkingSummaryLookupResult { public required AgentWorkingSummaryRecord? Value { get; init; } }
[SunderRpcTransparent]
public sealed class SessionContextCheckpointLookupResult { public required AgentSessionContextCheckpointRecord? Value { get; init; } }
[SunderRpcTransparent]
public sealed class RunCheckpointLookupResult { public required AgentRunCheckpointRecord? Value { get; init; } }
[SunderRpcTransparent]
public sealed class ModelBindingLookupResult { public required AgentProfileModelBindingRecord? Value { get; init; } }

public sealed class RecentTurnsRequest
{
    public required PageSize Limit { get; init; }
    public required EntityId SessionId { get; init; }
}

public sealed class TurnBoundaryRequest
{
    public required Timestamp CreatedAtUtc { get; init; }
    public required PageSize Limit { get; init; }
    public required EntityId SessionId { get; init; }
    public required EntityId TurnId { get; init; }
}

public sealed class SessionCapabilityRequest
{
    public required TextValue CapabilityKind { get; init; }
    public required EntityId SessionId { get; init; }
}

public sealed class ProfileCapabilityRequest
{
    public required TextValue CapabilityKind { get; init; }
    public required Identifier ProfileId { get; init; }
}

public sealed class AgentWorkingSummaryRecord
{
    public required EntityId SessionId { get; init; }
    public required DisplayText SummaryText { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
}

public sealed class AgentSessionContextCheckpointRecord
{
    public required EntityId ContextCheckpointId { get; init; }
    public required Timestamp CreatedAtUtc { get; init; }
    public SunderRpcOptional<JsonText> DetailsJson { get; init; }
    public SunderRpcOptional<EntityId> FirstOmittedTurnId { get; init; }
    public SunderRpcOptional<EntityId> LastOmittedTurnId { get; init; }
    public required Count OmittedTurnCount { get; init; }
    public required EntityId SessionId { get; init; }
    public required DisplayText SummaryText { get; init; }
}

[SunderRpcUnion(typeof(AgentRuntimeCatalogResetEvent), typeof(AgentSessionCatalogEvent), typeof(AgentTurnCatalogEvent), typeof(AgentProfileCatalogEvent)), SunderRpcDiscriminator("kind")]
public abstract record AgentRuntimeCatalogEvent;

[SunderRpcVariant("reset")]
public sealed record AgentRuntimeCatalogResetEvent : AgentRuntimeCatalogEvent
{
    public required Revision Sequence { get; init; }
}

[SunderRpcVariant("session")]
public sealed record AgentSessionCatalogEvent : AgentRuntimeCatalogEvent
{
    public required Revision Sequence { get; init; }
    public required EntityId SessionId { get; init; }
}

[SunderRpcVariant("turn")]
public sealed record AgentTurnCatalogEvent : AgentRuntimeCatalogEvent
{
    public required Revision Sequence { get; init; }
    public required EntityId SessionId { get; init; }
    public required AgentTurnRecord Turn { get; init; }
}

[SunderRpcVariant("profile")]
public sealed record AgentProfileCatalogEvent : AgentRuntimeCatalogEvent
{
    public required Revision Sequence { get; init; }
    public required Identifier ProfileId { get; init; }
}
