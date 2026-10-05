using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using static System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Sunder.Package.Agent.Rpc.Owner;

internal sealed class HistorySearchRequest
{
    public required HistoryActivityKind Activity { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Continuation { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Timestamp> FromUtc { get; init; }
    public required bool IncludeChildSessions { get; init; }
    [SunderRpcFacet(Minimum = "1", Maximum = "100")] public required long Limit { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ProfileId { get; init; }
    public required string Query { get; init; }
    public required HistorySearchRoleFilter Role { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> SessionId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Timestamp> ToUtc { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> WorkspaceId { get; init; }
}

internal sealed class HistorySearchResponse
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Continuation { get; init; }
    public required bool IsPartial { get; init; }
    public required bool Restarted { get; init; }
    public required IReadOnlyList<HistorySearchHit> Results { get; init; }
    public required HistorySearchStatus Status { get; init; }
}

internal sealed class HistorySearchHit
{
    public required HistoryActivityKind Activity { get; init; }
    public required HistoryAnchorKind AnchorKind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> CallId { get; init; }
    public required string DocumentId { get; init; }
    public required bool IsChildSession { get; init; }
    public required Uuid ItemId { get; init; }
    public required IReadOnlyList<string> MatchReasons { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ProfileId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentMessageRole> Role { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Uuid> RootSessionId { get; init; }
    public required Uuid SessionId { get; init; }
    public required string SessionTitle { get; init; }
    public required string Snippet { get; init; }
    public required IReadOnlyList<string> Symbols { get; init; }
    public required Timestamp TimestampUtc { get; init; }
    public required Uuid TurnId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string WorkspaceName { get; init; }
}

internal sealed class HistorySearchStatus
{
    public required HistorySearchAvailability Availability { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long EmbeddedDocuments { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault), SunderRpcFacet(Minimum = "0")] public SunderRpcOptional<long> EmbeddingGeneration { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> EmbeddingModelId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> EmbeddingProviderId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> EmbeddingProviderPackageId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> FailureCode { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> FailureMessage { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long IndexedDocuments { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<Timestamp> LastReconciledAtUtc { get; init; }
    public required bool LexicalEnabled { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long PendingChanges { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long ProgressCompleted { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault), SunderRpcFacet(Minimum = "0")] public SunderRpcOptional<long> ProgressTotal { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long ProjectionRevision { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    public required string RuntimeInstanceId { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long SemanticConfigurationRevision { get; init; }
    public required bool SemanticEnabled { get; init; }
    public required bool SemanticReady { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault), SunderRpcFacet(Minimum = "0")] public SunderRpcOptional<long> TextGeneration { get; init; }
}

internal sealed class HistorySearchStateRequest
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Continuation { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> EmbeddingProviderId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> EmbeddingProviderPackageId { get; init; }
    public required bool IncludeAdvancedFilters { get; init; }
    [SunderRpcFacet(Minimum = "1", Maximum = "200")] public required long Limit { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> WorkspaceId { get; init; }
}

internal sealed class HistorySearchState
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Continuation { get; init; }
    public required IReadOnlyList<HistoryEmbeddingModelOption> EmbeddingModels { get; init; }
    public required IReadOnlyList<HistoryEmbeddingProviderOption> EmbeddingProviders { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentEmbeddingProviderReadiness> EmbeddingReadiness { get; init; }
    public required IReadOnlyList<HistorySearchFilterOption> Profiles { get; init; }
    public required IReadOnlyList<HistorySearchFilterOption> Sessions { get; init; }
    public required HistorySearchStatus Status { get; init; }
    public required IReadOnlyList<HistorySearchFilterOption> Workspaces { get; init; }
}

internal sealed class HistoryEmbeddingModelOption
{
    [JsonIgnore(Condition = WhenWritingDefault), SunderRpcFacet(Minimum = "1")] public SunderRpcOptional<long> Dimensions { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsRecommended { get; init; }
    public required string ModelId { get; init; }
    public required string PackageId { get; init; }
    public required string ProviderId { get; init; }
}

internal sealed class HistoryEmbeddingProviderOption
{
    public required string DisplayName { get; init; }
    public required string PackageId { get; init; }
    public required string ProviderId { get; init; }
}

internal sealed class HistorySearchFilterOption
{
    public required string DisplayName { get; init; }
    public required string Id { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ParentId { get; init; }
}

internal sealed class HistorySearchCommand
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> EmbeddingModelId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> EmbeddingProviderId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> EmbeddingProviderPackageId { get; init; }
    public required HistorySearchCommandKind Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<bool> SemanticEnabled { get; init; }
}

internal sealed class HistorySearchCommandResult { public required HistorySearchStatus Status { get; init; } }

internal sealed class HistorySearchStatusSubscription
{
    [SunderRpcFacet(Minimum = "0")] public required long AfterRevision { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> RuntimeInstanceId { get; init; }
}
