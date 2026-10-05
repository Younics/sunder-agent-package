using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using static System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Sunder.Package.Agent.Memory.Semantic.Rpc;

[SunderRpcTransparent] internal sealed class MemoryId { public required Guid Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)] internal sealed class ProfileId { public required string Value { get; init; } }
[SunderRpcTransparent] internal sealed class SearchText { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)] internal sealed class MemoryCategory { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)] internal sealed class MemoryContent { public required string Value { get; init; } }
[SunderRpcTransparent] internal sealed class EvidenceText { public required string Value { get; init; } }
[SunderRpcTransparent] internal sealed class CorrectionNote { public required string Value { get; init; } }
[SunderRpcTransparent] internal sealed class StatusText { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)] internal sealed class ProviderId { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)] internal sealed class ModelId { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)] internal sealed class ConfigurationFingerprint { public required string Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(Minimum = "0", Maximum = "1")] internal sealed class MemoryScore { public required float Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(Minimum = "0")] internal sealed class AccessCount { public required long Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(Minimum = "0")] internal sealed class MemoryRevision { public required long Value { get; init; } }
internal enum MemoryQueryKind
{
    [JsonStringEnumMemberName("sessions")] Sessions,
    [JsonStringEnumMemberName("session-checkpoint")] SessionCheckpoint,
    [JsonStringEnumMemberName("working-summary")] WorkingSummary,
    [JsonStringEnumMemberName("memories")] Memories,
    [JsonStringEnumMemberName("details")] Details,
    [JsonStringEnumMemberName("semantic-state")] SemanticState,
    [JsonStringEnumMemberName("semantic-index-status")] SemanticIndexStatus,
    [JsonStringEnumMemberName("worker-status")] WorkerStatus,
    [JsonStringEnumMemberName("metrics")] Metrics,
}
internal enum MemoryCommandKind
{
    [JsonStringEnumMemberName("update")] Update,
    [JsonStringEnumMemberName("pin")] Pin,
    [JsonStringEnumMemberName("contest")] Contest,
    [JsonStringEnumMemberName("forget")] Forget,
    [JsonStringEnumMemberName("supersede")] Supersede,
    [JsonStringEnumMemberName("correct")] Correct,
    [JsonStringEnumMemberName("reindex")] Reindex,
}
internal enum MemoryProvenance
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("user")] User,
    [JsonStringEnumMemberName("assistant")] Assistant,
    [JsonStringEnumMemberName("tool")] Tool,
}
internal enum MemoryState
{
    [JsonStringEnumMemberName("active")] Active,
    [JsonStringEnumMemberName("contested")] Contested,
    [JsonStringEnumMemberName("forgotten")] Forgotten,
    [JsonStringEnumMemberName("superseded")] Superseded,
}
internal sealed class StoredMemory
{
    public required MemoryId MemoryId { get; init; }
    public required MemoryId SessionId { get; init; }
    public required MemoryCategory Category { get; init; }
    public required MemoryContent Content { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<EvidenceText> EvidenceText { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<MemoryId> SourceTurnId { get; init; }
    public required MemoryScore Importance { get; init; }
    public required MemoryScore Confidence { get; init; }
    public required bool IsPinned { get; init; }
    public required MemoryState State { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<MemoryId> SupersededByMemoryId { get; init; }
    public required SunderRpcOffsetDateTime CreatedAtUtc { get; init; }
    public required SunderRpcOffsetDateTime UpdatedAtUtc { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<SunderRpcOffsetDateTime> LastAccessedAtUtc { get; init; }
    public required AccessCount AccessCount { get; init; }
    public required MemoryProvenance Provenance { get; init; }
    public required MemoryRevision MemoryRevision { get; init; }
    public required bool IsManual { get; init; }
}
internal sealed class SemanticEmbeddingContext
{
    public required StatusText StatusLabel { get; init; }
    public required StatusText StatusText { get; init; }
    public required bool IsReady { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<ProviderId> ProviderId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<ModelId> ModelId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<StatusText> ProviderDisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<ConfigurationFingerprint> ConfigurationFingerprint { get; init; }
}
internal sealed class MemoryQuery
{
    public required MemoryQueryKind Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<MemoryId> SessionId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<MemoryId> MemoryId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<SearchText> SearchText { get; init; }
    public required bool IncludeInactive { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<ProfileId> ProfileId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<StoredMemory> Memory { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<SemanticEmbeddingContext> SemanticContext { get; init; }
}
internal sealed class MemoryCommand
{
    public required MemoryCommandKind Kind { get; init; }
    public required MemoryId MemoryId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<MemoryCategory> Category { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<MemoryContent> Content { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<CorrectionNote> Note { get; init; }
    public required bool IsPinned { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<ProfileId> ProfileId { get; init; }
}
[SunderRpcTransparent] internal sealed class ContentReference { public required SunderRpcContentReference Value { get; init; } }
internal sealed class MemoryProjectionContent { public required ContentReference Content { get; init; } }
internal sealed class MemoryChangeSubscription;
internal enum MemoryChangeKind
{
    [JsonStringEnumMemberName("session")] Session,
    [JsonStringEnumMemberName("worker")] Worker,
    [JsonStringEnumMemberName("reset-required")] ResetRequired,
}
internal sealed class MemoryChanged
{
    public required MemoryChangeKind Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<MemoryId> SessionId { get; init; }
}
