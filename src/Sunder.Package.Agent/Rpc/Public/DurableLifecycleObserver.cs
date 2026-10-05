using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.durable.lifecycle.observer",
    ServiceId = "durable-lifecycle-observer", IncludeSchemaDialect = true)]
[SunderRpcRuntimeUse(Actions = ["discover", "invoke"])]
public interface DurableLifecycleObserverService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentRpcIdentity> DescribeAsync(Unit request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<Unit> HandleAsync(AgentDurableLifecycleEventEnvelope request, CancellationToken cancellationToken = default);
}

public enum AgentLifecycleEventKind
{
    [JsonStringEnumMemberName("user-turn-added")] UserTurnAdded,
    [JsonStringEnumMemberName("assistant-turn-completed")] AssistantTurnCompleted,
    [JsonStringEnumMemberName("tool-result-recorded")] ToolResultRecorded,
    [JsonStringEnumMemberName("run-interrupted")] RunInterrupted,
    [JsonStringEnumMemberName("run-stopped")] RunStopped,
    [JsonStringEnumMemberName("run-failed")] RunFailed,
    [JsonStringEnumMemberName("transcript-rolled-back")] TranscriptRolledBack,
    [JsonStringEnumMemberName("session-deleted")] SessionDeleted,
    [JsonStringEnumMemberName("workspace-deleted")] WorkspaceDeleted,
}

public sealed class AgentDurableLifecycleEventPayload
{
    public SunderRpcOptional<AgentRunCheckpointRecord> Checkpoint { get; init; }
    public required Flag ContentErased { get; init; }
    public required Count DeletedChildSessionCount { get; init; }
    public required EntityIds DeletedChildSessionIds { get; init; }
    public required Count DeletedSessionCount { get; init; }
    public required EntityIds DeletedSessionIds { get; init; }
    public required Count DeletedTurnCount { get; init; }
    public required EntityIds DeletedTurnIds { get; init; }
    public SunderRpcOptional<Identifier> DeletionBatchId { get; init; }
    public SunderRpcOptional<Count> DeletionChunkCount { get; init; }
    public SunderRpcOptional<CollectionIndex> DeletionChunkIndex { get; init; }
    public required AgentMemoryConsistencyBarrierCollection DeletionChunkReceipts { get; init; }
    public required AgentTurnRecordCollection RecentLiveBufferTurns { get; init; }
    public SunderRpcOptional<EntityId> RootSessionId { get; init; }
    public SunderRpcOptional<AgentRunContextRecord> Run { get; init; }
    public SunderRpcOptional<AgentSessionContextRecord> Session { get; init; }
    public SunderRpcOptional<EntityId> SessionId { get; init; }
    public SunderRpcOptional<AgentTurnRecord> TriggerTurn { get; init; }
    public required AgentTurnRecordCollection Turns { get; init; }
    public SunderRpcOptional<ContentText> UserMessage { get; init; }
    public SunderRpcOptional<DisplayText> WorkingSummary { get; init; }
    public SunderRpcOptional<Identifier> WorkspaceId { get; init; }
    public SunderRpcOptional<Identifier> WorkspaceIncarnationId { get; init; }
}

public sealed class AgentDurableLifecycleEventEnvelope
{
    public required Identifier EventId { get; init; }
    public required AgentLifecycleEventKind Kind { get; init; }
    public required Timestamp OccurredAtUtc { get; init; }
    public required TextValue OrderingKey { get; init; }
    public SunderRpcOptional<Sha256Text> OriginalPayloadHash { get; init; }
    public required AgentDurableLifecycleEventPayload Payload { get; init; }
    public required Sha256Text PayloadHash { get; init; }
    public required JsonText PayloadJson { get; init; }
    public required Revision Sequence { get; init; }
    public required TextValue SourceKey { get; init; }
}
