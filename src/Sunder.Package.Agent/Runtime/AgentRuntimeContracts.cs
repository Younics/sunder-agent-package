using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Runtime;

internal static class AgentRuntimeOperations
{
    public static readonly PackageRuntimeOperation<AgentDashboardRequest, AgentDashboardProjection> Dashboard =
        new("agent.dashboard.v1");
    public static readonly PackageRuntimeOperation<AgentSessionPageRequest, AgentSessionPage> Sessions =
        new("agent.sessions.page.v1");
    public static readonly PackageRuntimeOperation<AgentTranscriptPageRequest, AgentTranscriptPage> Transcript =
        new("agent.transcript.page.v1");
    public static readonly PackageRuntimeOperation<AgentCatalogRequest, AgentCatalogProjection> Catalog =
        new("agent.catalog.v1");
    public static readonly PackageRuntimeOperation<AgentProfileCommand, AgentProfileCommandResult> Profiles =
        new("agent.profiles.command.v1");
    public static readonly PackageRuntimeOperation<AgentWorkspaceCommand, AgentWorkspaceCommandResult> Workspaces =
        new("agent.workspaces.command.v1");
    public static readonly PackageRuntimeOperation<AgentSessionCommand, AgentSessionCommandResult> SessionCommands =
        new("agent.sessions.command.v1");
    public static readonly PackageRuntimeOperation<AgentRunCommand, AgentRunCommandResult> Runs =
        new("agent.runs.command.v1");
    public static readonly PackageRuntimeOperation<AgentPermissionCommand, AgentPermissionProjection> Permissions =
        new("agent.permissions.command.v1");
    public static readonly PackageRuntimeOperation<AgentAttachmentReadRequest, AgentAttachmentReadResult> Attachments =
        new("agent.attachments.read.v1");
    public static readonly PackageRuntimeStream<AgentChangeSubscription, AgentRuntimeChange> Changes =
        new("agent.changes.v1");
}

internal sealed record AgentDashboardRequest;
internal sealed record AgentDashboardProjection(
    long Revision,
    IReadOnlyList<AgentProfileRecord> Profiles,
    IReadOnlyList<AgentWorkspaceRecord> Workspaces,
    IReadOnlyList<AgentWorkspaceBindingRecord> WorkspaceBindings,
    IReadOnlyList<AgentSessionRecord> RecentSessions,
    IReadOnlyList<AgentRunCheckpointRecord> RecentCheckpoints,
    IReadOnlyList<AgentTranscriptMessageRecord> RecentMessages);

internal sealed record AgentSessionPageRequest(
    string? WorkspaceId = null,
    Guid? SessionId = null,
    int Offset = 0,
    int Limit = 100);
internal sealed record AgentSessionSnapshot(AgentSessionRecord Session, AgentRunCheckpointRecord? Checkpoint);
internal sealed record AgentSessionPage(long Revision, IReadOnlyList<AgentSessionSnapshot> Items, int TotalCount, bool HasMore);

internal enum AgentTranscriptPageDirection { Recent, Before, After, Turn }
internal sealed record AgentTranscriptPageRequest(
    Guid SessionId,
    AgentTranscriptPageDirection Direction,
    int Limit = 60,
    DateTimeOffset? AnchorCreatedAtUtc = null,
    Guid? AnchorTurnId = null);
internal sealed record AgentTranscriptPage(long Revision, IReadOnlyList<AgentTurnRecord> Turns, bool HasMore);

internal sealed record AgentCatalogRequest(
    AgentProfileRecord? Profile = null,
    string? ChatProviderId = null,
    string? EmbeddingProviderId = null);
internal sealed record AgentCatalogProjection(
    long Revision,
    IReadOnlyList<AgentProviderDescriptor> ChatProviders,
    IReadOnlyList<AgentEmbeddingProviderDescriptor> EmbeddingProviders,
    IReadOnlyList<AgentBehaviorLoopDescriptor> BehaviorLoops,
    IReadOnlyList<AgentExecutionTargetDescriptor> ExecutionTargets,
    IReadOnlyList<AgentToolCatalogEntry> LocalTools,
    IReadOnlyList<AgentProfileSelectableCapabilityDescriptor> SelectableCapabilities,
    bool HasEmbeddingConsumers,
    IReadOnlyList<AgentModelDescriptor> ChatModels,
    IReadOnlyList<AgentEmbeddingModelDescriptor> EmbeddingModels,
    AgentProviderReadiness? ChatReadiness,
    AgentEmbeddingProviderReadiness? EmbeddingReadiness);

internal enum AgentProfileCommandKind { Create, Save, Delete }
internal sealed record AgentProfileCommand(
    AgentProfileCommandKind Kind,
    string? ProfileId = null,
    string? DisplayName = null,
    string? Description = null,
    string? Instructions = null,
    string? ChatProviderId = null,
    string? ChatModelId = null,
    string? EmbeddingProviderId = null,
    string? EmbeddingModelId = null,
    IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? Assignments = null,
    string? BehaviorLoopId = null,
    string? BehaviorLoopSourceId = null,
    string? BehaviorLoopSettingsJson = null,
    string? ChatModelSettingsJson = null);
internal sealed record AgentProfileCommandResult(long Revision, AgentProfileRecord? Profile);

internal enum AgentWorkspaceCommandKind { Create, Save, Delete, Warmup }
internal sealed record AgentWorkspaceCommand(
    AgentWorkspaceCommandKind Kind,
    string? WorkspaceId = null,
    string? DisplayName = null,
    string? Description = null,
    IReadOnlyList<AgentWorkspacePathRecord>? Paths = null,
    IReadOnlyList<AgentWorkspaceDocumentRecord>? Documents = null,
    string? ExecutionTargetId = null);
internal sealed record AgentWorkspaceCommandResult(
    long Revision,
    AgentWorkspaceRecord? Workspace,
    AgentExecutionTargetWarmupResult? Warmup = null);

internal enum AgentSessionCommandKind { Create, Update, Delete }
internal sealed record AgentSessionCommand(
    AgentSessionCommandKind Kind,
    AgentSessionRecord? Session = null,
    string? Title = null,
    string? ProfileId = null,
    string? BehaviorLoopId = null,
    string? WorkspaceId = null);
internal sealed record AgentSessionCommandResult(long Revision, AgentSessionSnapshot? Session);

internal enum AgentRunCommandKind { Start, RollbackAndStart, Stop, ApprovePermission, DenyPermission }
internal sealed record AgentRunCommand(
    AgentRunCommandKind Kind,
    Guid SessionId,
    string? ProfileId = null,
    string? UserMessage = null,
    string? WorkspaceId = null,
    IReadOnlyList<AgentAttachmentUploadRequest>? Attachments = null,
    Guid? RollbackAnchorTurnId = null,
    string? PermissionRequestId = null);
internal sealed record AgentRunCommandResult(long Revision, AgentRunCheckpointRecord? Checkpoint);

internal enum AgentPermissionCommandKind { Read, SetUnrestricted, SaveOverride, DeleteOverride, SaveSessionApproval }
internal sealed record AgentPermissionCommand(
    AgentPermissionCommandKind Kind,
    Guid? SessionId = null,
    bool? IsEnabled = null,
    string? ActionId = null,
    string? BoundaryId = null,
    AgentPermissionDecision? Decision = null);
internal sealed record AgentPermissionProjection(
    long Revision,
    AgentSessionPermissionState? SessionState,
    IReadOnlyList<AgentPermissionActionDescriptor> Actions,
    IReadOnlyList<AgentPermissionOverride> Overrides,
    IReadOnlyList<AgentPendingPermissionRequestRecord> PendingRequests);

internal sealed record AgentAttachmentReadRequest(AgentAttachmentMetadata Metadata);
internal sealed record AgentAttachmentReadResult(byte[] Content);

internal sealed record AgentChangeSubscription(long AfterRevision = 0);
internal enum AgentRuntimeChangeKind { Connected, ResnapshotRequired, Profile, Catalog, Workspace, Session, Turn, TranscriptReset, RunActivity, Permission }
internal sealed record AgentRuntimeChange(
    long Revision,
    AgentRuntimeChangeKind Kind,
    string? ProfileId = null,
    string? WorkspaceId = null,
    Guid? SessionId = null,
    AgentProfileRecord? Profile = null,
    AgentWorkspaceRecord? Workspace = null,
    AgentSessionSnapshot? Session = null,
    AgentTurnRecord? Turn = null,
    AgentRunActivityUpdate? RunActivity = null);
