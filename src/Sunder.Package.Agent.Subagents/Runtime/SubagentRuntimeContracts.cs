using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Subagents.Models;

namespace Sunder.Package.Agent.Subagents.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal interface ISubsessionSessionReader
{
    Task<SubsessionSessionCatalog> ListSessionsAsync(CancellationToken cancellationToken = default);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal interface ISubsessionCheckpointReader
{
    Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
        CancellationToken cancellationToken = default);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal interface ISubsessionTranscriptPageReader
{
    Task<SubsessionTranscriptPage> ListRecentTurnsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<SubsessionTranscriptPage> ListTurnsAfterAsync(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(
        Guid sessionId,
        Guid turnId,
        DateTimeOffset turnCreatedAtUtc,
        Guid itemId,
        int beforeLimit,
        int afterLimit,
        CancellationToken cancellationToken = default);

    Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult<AgentTranscriptToolDetailRecord?>(null);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal interface ISubsessionChangeNotifications
{
    event Action<Guid>? SessionChanged;
    event Action<Guid, AgentTurnRecord>? TurnChanged;
    event Action? ResnapshotRequired;
}

internal interface ISubagentPresentationInitialization
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal interface ISubagentOwnerClient
{
    ValueTask<SubagentProjection> QueryAsync(
        SubagentQuery request,
        CancellationToken cancellationToken = default);
    ValueTask<SubagentProjection> CommandAsync(
        SubagentCommand request,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<SubagentChanged> SubscribeChangesAsync(
        SubagentChangeSubscription request,
        CancellationToken cancellationToken = default);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SubsessionSessionCatalog(
    IReadOnlyList<AgentSessionRecord> Sessions,
    IReadOnlyList<AgentProfileRecord> Profiles);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SubsessionAroundTurnPage(
    IReadOnlyList<AgentTurnRecord> Turns,
    bool HasOlder,
    bool HasNewer,
    Guid AnchorTurnId);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SubsessionTranscriptPage(
    IReadOnlyList<AgentTurnRecord> Turns,
    bool HasMore,
    TranscriptPageCursor? Continuation = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SubagentSaveRequest(
    string SubagentId,
    string DisplayName,
    string? Description,
    string? Instructions,
    string? ChatProviderId,
    string? ChatModelId,
    IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? Assignments,
    string? ChatModelSettingsJson);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum SubagentQueryKind
{
    Management,
    ProviderModels,
    RuntimeCatalog,
    RecentTurns,
    TurnsBefore,
    TurnsAfter,
    AroundTurn,
    ToolDetail,
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SubagentQuery(
    SubagentQueryKind Kind,
    string? ProviderId = null,
    Guid? SessionId = null,
    int Limit = 100,
    DateTimeOffset? AnchorCreatedAtUtc = null,
    Guid? AnchorTurnId = null,
    Guid? ItemId = null,
    int AfterLimit = 0,
    AgentTranscriptToolDetailRequest? ToolDetailRequest = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum SubagentCommandKind
{
    Create,
    Save,
    Delete,
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SubagentCommand(
    SubagentCommandKind Kind,
    string? Value = null,
    SubagentSaveRequest? Save = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SubagentProviderProjection(
    string ProviderId,
    string DisplayName,
    string? PackageId,
    IReadOnlyList<AgentModelDescriptor>? Models = null,
    AgentProviderReadiness? Readiness = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SubagentProjection(
    IReadOnlyList<SubagentRecord>? Subagents = null,
    SubagentRecord? Subagent = null,
    IReadOnlyList<SubagentProviderProjection>? Providers = null,
    IReadOnlyList<AgentToolDescriptor>? Tools = null,
    IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>? Capabilities = null,
    IReadOnlyList<AgentSessionRecord>? Sessions = null,
    IReadOnlyList<AgentProfileRecord>? Profiles = null,
    IReadOnlyList<AgentRunCheckpointRecord>? Checkpoints = null,
    SubsessionTranscriptPage? TranscriptPage = null,
    SubsessionAroundTurnPage? AroundTurn = null,
    AgentTranscriptToolDetailRecord? ToolDetail = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SubagentChangeSubscription(long AfterRevision = 0);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum SubagentChangeKind
{
    Connected,
    ResnapshotRequired,
    Subagents,
    Catalog,
    Session,
    Turn,
    Profile,
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record SubagentChanged(
    long Revision,
    SubagentChangeKind Kind,
    Guid? SessionId = null,
    AgentTurnRecord? Turn = null,
    string? ProfileId = null,
    string? RuntimeInstanceId = null);
