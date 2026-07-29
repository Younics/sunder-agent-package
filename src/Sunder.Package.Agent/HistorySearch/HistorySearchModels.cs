using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.HistorySearch;

internal static class HistorySearchLimits
{
    internal const int MaximumQueryCharacters = 1_024;
    internal const int MaximumFilterCharacters = 512;
    internal const int MaximumContinuationCharacters = 1_024;
    internal const int MaximumResults = 100;
    internal const int DefaultResults = 30;
    internal const int MaximumSnippetCharacters = 1_200;
    internal const int StoredSnippetCharacters = 1_600;
    internal const int MaximumBodyCharacters = 1_800;
    internal const int MaximumFacetCharacters = 1_024;
    internal const int MaximumFacetsPerResult = 12;
    internal const int MaximumMatchReasons = 6;
    internal const int MaximumVectorDimensions = 8_192;
    internal const int MaximumSemanticScanDocuments = 20_000;
    internal const long MaximumSemanticScanBytes = 64L * 1024 * 1024;
    internal const int MaximumSemanticResults = 100;
    internal const int CandidateLimit = 400;
    internal const int AuthoritativePageSize = 100;
    internal const int AroundTurnSideLimit = 30;
    internal const int MaximumStateOptions = 200;
    internal const int MaximumAdvancedFilterPages = 20;
    internal const int MaximumAdvancedSessionOptions = 4_000;
    internal const int MaximumDisplayCharacters = 512;
    internal const int MaximumStatusCharacters = 2_048;

    internal static int GetMaximumSemanticScanDocuments(int dimensions)
    {
        if (dimensions is <= 0 or > MaximumVectorDimensions)
        {
            return 0;
        }
        var vectorBytes = checked(dimensions * sizeof(float));
        return (int)Math.Min(MaximumSemanticScanDocuments, MaximumSemanticScanBytes / vectorBytes);
    }
}

internal static class HistorySearchVersions
{
    internal const int Schema = 3;
    internal const int Extractor = 6;
    internal const int Redaction = 7;
    internal const int QueryPlanVersion = 2;
    internal const int RankingVersion = 3;
}

internal enum HistorySearchRoleFilter
{
    Any,
    User,
    Assistant,
}

internal enum HistoryActivityKind
{
    None,
    Read,
    Search,
    Use,
    Edit,
    Delete,
    Execute,
}

internal enum HistoryAnchorKind
{
    Text,
    Activity,
}

internal sealed record HistorySearchRequest(
    string Query,
    string? WorkspaceId = null,
    Guid? SessionId = null,
    string? ProfileId = null,
    HistorySearchRoleFilter Role = HistorySearchRoleFilter.Any,
    HistoryActivityKind Activity = HistoryActivityKind.None,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    bool IncludeChildSessions = true,
    int Limit = HistorySearchLimits.DefaultResults,
    string? Continuation = null);

internal sealed record HistorySearchHit(
    string DocumentId,
    string WorkspaceId,
    string WorkspaceName,
    Guid SessionId,
    string SessionTitle,
    bool IsChildSession,
    Guid? RootSessionId,
    string? ProfileId,
    DateTimeOffset TimestampUtc,
    AgentMessageRole? Role,
    HistoryActivityKind Activity,
    string Snippet,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> MatchReasons,
    Guid TurnId,
    Guid ItemId,
    string? CallId,
    HistoryAnchorKind AnchorKind);

internal sealed record HistorySearchResponse(
    IReadOnlyList<HistorySearchHit> Results,
    string? Continuation,
    bool IsPartial,
    HistorySearchStatus Status,
    bool Restarted = false);

internal enum HistorySearchAvailability
{
    Starting,
    Ready,
    Rebuilding,
    Clearing,
    Unavailable,
}

internal sealed record HistorySearchStatus(
    long Revision,
    HistorySearchAvailability Availability,
    bool LexicalEnabled,
    bool SemanticEnabled,
    bool SemanticReady,
    string? EmbeddingProviderPackageId,
    string? EmbeddingProviderId,
    string? EmbeddingModelId,
    long SemanticConfigurationRevision,
    long ProjectionRevision,
    long? TextGeneration,
    long? EmbeddingGeneration,
    int IndexedDocuments,
    int EmbeddedDocuments,
    int PendingChanges,
    int ProgressCompleted,
    int? ProgressTotal,
    DateTimeOffset? LastReconciledAtUtc,
    string? FailureCode,
    string? FailureMessage,
    string RuntimeInstanceId = "");

internal sealed record HistorySearchStatusSubscription(
    long AfterRevision = 0,
    string? RuntimeInstanceId = null);

internal sealed record HistorySearchStateRequest(
    string? EmbeddingProviderId = null,
    string? EmbeddingProviderPackageId = null,
    string? Continuation = null,
    int Limit = HistorySearchLimits.MaximumStateOptions,
    bool IncludeAdvancedFilters = true,
    string? WorkspaceId = null);

internal sealed record HistorySearchFilterOption(string Id, string DisplayName, string? ParentId = null);

internal sealed record HistoryEmbeddingProviderOption(
    string PackageId,
    string ProviderId,
    string DisplayName);

internal sealed record HistoryEmbeddingModelOption(
    string PackageId,
    string ProviderId,
    string ModelId,
    string DisplayName,
    int? Dimensions,
    bool IsRecommended);

internal sealed record HistorySearchState(
    HistorySearchStatus Status,
    IReadOnlyList<HistoryEmbeddingProviderOption> EmbeddingProviders,
    IReadOnlyList<HistoryEmbeddingModelOption> EmbeddingModels,
    AgentEmbeddingProviderReadiness? EmbeddingReadiness,
    IReadOnlyList<HistorySearchFilterOption> Workspaces,
    IReadOnlyList<HistorySearchFilterOption> Sessions,
    IReadOnlyList<HistorySearchFilterOption> Profiles,
    string? Continuation);

internal enum HistorySearchCommandKind
{
    ConfigureSemantic,
    Rebuild,
    ClearDerivedIndex,
}

internal sealed record HistorySearchCommand(
    HistorySearchCommandKind Kind,
    bool? SemanticEnabled = null,
    string? EmbeddingProviderId = null,
    string? EmbeddingModelId = null,
    string? EmbeddingProviderPackageId = null);

internal sealed record HistorySearchCommandResult(HistorySearchStatus Status);

internal interface IAgentHistorySearchGateway
{
    event Action<HistorySearchStatus>? HistoryStatusChanged;

    event Action? HistoryRuntimeChanged
    {
        add { }
        remove { }
    }

    Task<HistorySearchResponse> SearchHistoryAsync(
        HistorySearchRequest request,
        CancellationToken cancellationToken = default);

    Task<HistorySearchState> LoadHistoryStateAsync(
        HistorySearchStateRequest request,
        CancellationToken cancellationToken = default);

    Task<HistorySearchCommandResult> ExecuteHistoryCommandAsync(
        HistorySearchCommand command,
        CancellationToken cancellationToken = default);

    void StartObservingHistoryStatus();
}

internal sealed record HistorySourceSession(
    Guid SessionId,
    string Title,
    string WorkspaceId,
    string WorkspaceName,
    Guid? ParentSessionId,
    Guid? RootSessionId,
    string? ProfileId,
    string? ProfileName,
    DateTimeOffset UpdatedAtUtc);

internal sealed record HistorySourceSessionPage(
    IReadOnlyList<HistorySourceSession> Sessions,
    string? Continuation,
    long InsertionHighWaterMark);

internal sealed record HistorySourceSessionMutationPage(
    IReadOnlyList<HistorySourceSession> Sessions,
    DateTimeOffset? ContinuationUpdatedAtUtc,
    Guid? ContinuationSessionId);

internal sealed record HistorySourceTurnPage(
    IReadOnlyList<AgentTurnRecord> Turns,
    DateTimeOffset? ContinuationCreatedAtUtc,
    Guid? ContinuationTurnId);

internal sealed record HistorySearchValidation(
    Guid TurnId,
    Guid SessionId,
    string WorkspaceId,
    Guid? RootSessionId,
    Guid? ParentSessionId,
    string? ProfileId,
    AgentMessageRole Role,
    long ContentRevision,
    bool IsStreaming,
    IReadOnlySet<Guid> ItemIds,
    IReadOnlySet<string> CallIds);

internal sealed record HistorySessionScope(IReadOnlyList<Guid> SessionIds);

internal sealed record HistoryProjectionDocument(
    string DocumentId,
    string WorkspaceId,
    string WorkspaceName,
    Guid SessionId,
    string SessionTitle,
    Guid? RootSessionId,
    Guid? ParentSessionId,
    string? ProfileId,
    AgentMessageRole? Role,
    HistoryActivityKind Activity,
    Guid TurnId,
    Guid ItemId,
    string? CallId,
    HistoryAnchorKind AnchorKind,
    long SourceContentRevision,
    bool SourceIsStreaming,
    DateTimeOffset CreatedAtUtc,
    string BodyText,
    string DisplaySnippet,
    IReadOnlyList<HistoryProjectionFacet> Facets);

internal sealed record HistoryProjectionFacet(string Kind, string Value, string NormalizedValue);

internal sealed record HistoryProjectionConfiguration(
    bool SemanticEnabled,
    string? EmbeddingProviderPackageId,
    string? EmbeddingProviderId,
    string? EmbeddingModelId,
    long Revision,
    string? EmbeddingSpaceFingerprint);

internal sealed record HistoryProjectionGeneration(
    long GenerationId,
    string ProjectionKind,
    string State,
    long? ParentTextGenerationId,
    int ExtractorVersion,
    int RedactionVersion,
    string? ProviderPackageId,
    string? ProviderId,
    string? ModelId,
    int? Dimensions,
    long ConfigurationRevision,
    string? EmbeddingSpaceFingerprint,
    Guid? RuntimeEpoch);

internal sealed record HistoryProjectionSnapshot(
    long? ActiveTextGenerationId,
    long? ActiveEmbeddingGenerationId,
    int DocumentCount,
    int EmbeddingCount,
    DateTimeOffset? LastReconciledAtUtc,
    bool SemanticReady,
    bool IsManuallyCleared,
    long ConfigurationRevision,
    Guid? RuntimeEpoch);

internal sealed record HistoryProjectionPin(
    long TextGenerationId,
    long? EmbeddingGenerationId,
    long ConfigurationRevision,
    bool SemanticReady,
    string? EmbeddingSpaceFingerprint);

internal sealed record HistoryLexicalCandidate(
    string DocumentId,
    int Rank,
    int Tier,
    double Score,
    DateTimeOffset TimestampUtc,
    long SourceContentRevision,
    string ProjectionHash,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<HistoryLexicalLaneRank> LaneRanks);

internal enum HistoryLexicalLaneKind
{
    FacetExact,
    ExactPhrase,
    AllExact,
    FacetPrefix,
    AllPrefix,
    AnyExact,
    AnyPrefix,
    Recent,
}

internal sealed record HistoryLexicalLaneRank(HistoryLexicalLaneKind Lane, int Rank);

internal sealed record HistorySemanticCandidate(
    string DocumentId,
    int Rank,
    long SourceContentRevision,
    string ProjectionHash);

internal sealed record HistoryStoredDocument(
    string DocumentId,
    string WorkspaceId,
    string WorkspaceName,
    Guid SessionId,
    string SessionTitle,
    Guid? RootSessionId,
    Guid? ParentSessionId,
    string? ProfileId,
    AgentMessageRole? Role,
    HistoryActivityKind Activity,
    Guid TurnId,
    Guid ItemId,
    string? CallId,
    HistoryAnchorKind AnchorKind,
    long SourceContentRevision,
    bool SourceIsStreaming,
    DateTimeOffset CreatedAtUtc,
    string DisplaySnippet,
    string ProjectionHash,
    IReadOnlyList<HistoryProjectionFacet> Facets);

internal sealed record HistoryEmbeddingWorkItem(
    string DocumentId,
    string BodyText,
    string ProjectionHash);

internal sealed record HistoryStoredEmbedding(
    string DocumentId,
    int Dimensions,
    byte[] Vector);

internal sealed record HistoryScoredEmbedding(
    string DocumentId,
    double Score,
    long SourceContentRevision,
    string ProjectionHash);
