using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using static System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Sunder.Package.Agent.Rpc.Owner;

internal sealed class AgentProfileRecord
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> BehaviorLoopId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> BehaviorLoopSettingsJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> BehaviorLoopSourceId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ChatModelId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ChatProviderId { get; init; }
    public required Timestamp CreatedAtUtc { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Description { get; init; }
    public required string DisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> EmbeddingModelId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> EmbeddingProviderId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Instructions { get; init; }
    public required bool IsInternal { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<IReadOnlyList<AgentProfileModelBindingRecord>> ModelBindings { get; init; }
    public required string ProfileId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>> SelectableCapabilityAssignments { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
}

internal sealed class AgentProfileModelBindingRecord
{
    public required string CapabilityKind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ModelId { get; init; }
    public required string ProfileId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ProviderId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> SettingsJson { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
}

internal sealed class AgentProfileSelectableCapabilityAssignmentRecord
{
    public required string CapabilityId { get; init; }
    public required string Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> SourceId { get; init; }
}

internal sealed class AgentWorkspaceRecord
{
    public required Timestamp CreatedAtUtc { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Description { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<AgentWorkspaceDocumentRecord> Documents { get; init; }
    public required IReadOnlyList<AgentWorkspacePathRecord> Paths { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
    public required string WorkspaceId { get; init; }
}

internal sealed class AgentWorkspaceBindingRecord
{
    public required string BindingId { get; init; }
    public required string ContributionId { get; init; }
    public required Timestamp CreatedAtUtc { get; init; }
    public required string ExtensionPointId { get; init; }
    public required bool IsEnabled { get; init; }
    public required string Role { get; init; }
    public required long SortOrder { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
    public required string WorkspaceId { get; init; }
}

internal sealed class AgentWorkspaceDocumentRecord
{
    public required Timestamp CreatedAtUtc { get; init; }
    public required string DocumentId { get; init; }
    public required string FilePath { get; init; }
    public required long SortOrder { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
    public required string WorkspaceId { get; init; }
}

internal sealed class AgentWorkspacePathRecord
{
    public required Timestamp CreatedAtUtc { get; init; }
    public required string HostPath { get; init; }
    public required bool IsDefault { get; init; }
    public required string PathId { get; init; }
    public required long SortOrder { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
    public required string WorkspaceId { get; init; }
}

internal sealed class AgentExecutionTargetWarmupResult
{
    public required string Message { get; init; }
    public required AgentExecutionTargetWarmupStatus Status { get; init; }
}
