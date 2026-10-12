using System.Collections.Concurrent;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
namespace Sunder.Package.Agent.Runtime;

internal enum AgentRuntimeConnectionState
{
    Connecting,
    Connected,
    Reconnecting,
    Unavailable,
    Disposed,
}

internal interface IAgentRuntimeAvailability
{
    AgentRuntimeConnectionState ConnectionState { get; }
    bool IsRuntimeAvailable { get; }
    event Action<AgentRuntimeConnectionState>? ConnectionStateChanged;
}

internal interface IAgentRuntimeFailureClassifier
{
    bool IsRetryableRuntimeFailure(
        Exception exception,
        CancellationToken callerCancellationToken = default);
}

internal sealed partial class AgentAppRuntimeGateway :
    IAgentProfileGateway,
    IAgentWorkspaceGateway,
    IAgentSessionGateway,
    IAgentTurnMutationGateway,
    IAgentPermissionGateway,
    IAgentRunGateway,
    IAgentAttachmentGateway,
    IAgentExecutionGateway,
    IAgentChatSnapshotGateway,
    IAgentTranscriptPageGateway,
    IAgentTranscriptToolDetailGateway,
    IAgentChatSessionCommandGateway,
    IAgentChatPermissionCommandGateway,
    IAgentChatRunGateway,
    IAgentCorrelatedRunGateway,
    IAgentRunCommandStatusGateway,
    IAgentPresentationInitialization,
    IAgentDashboardLoader,
    IAgentCatalogLoader,
    IAgentProfileCommandGateway,
    IAgentWorkspaceCommandGateway,
    IAgentGlobalPermissionGateway,
    IAgentRuntimeAvailability,
    IAgentRuntimeFailureClassifier,
    IDisposable,
    IAsyncDisposable
{
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(2);
    private readonly AgentServices.IAgentRuntimeService _owner;
    private readonly IAgentOwnerResources _resources;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _cacheLock = new();
    private readonly object _observationLock = new();
    private readonly SemaphoreSlim _resnapshotGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task<AgentCatalogProjection>>> _catalogs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AgentSessionSnapshot>> _workspaceSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, AgentSessionSnapshot> _knownSessions = [];
    private readonly Dictionary<Guid, AgentTurnRecord> _knownTurns = [];
    private AgentDashboardProjection? _dashboard;
    private Task<AgentDashboardProjection>? _dashboardLoad;
    private Task<AgentDashboardProjection>? _abandonedDashboardLoad;
    private AgentCatalogProjection? _catalogSnapshot;
    private int _catalogGeneration;
    private string? _runtimeInstanceId;
    private AgentChatSnapshotRequest? _pendingChatSnapshotRequest;
    private AgentChatSnapshotProjection? _pendingChatSnapshot;
    private AgentChatSnapshotRequest? _activeChatSnapshotRequest;
    private bool _chatSnapshotLoadPending;
    private long _revision;
    private volatile AgentRuntimeConnectionState _connectionState;
    private CancellationTokenSource? _observationCancellation;
    private int _observationGeneration;
    private int _chatSnapshotLoadGeneration;

    internal AgentAppRuntimeGateway(AgentServices.IAgentRuntimeService owner, IAgentOwnerResources? resources = null)
    {
        _owner = owner;
        _resources = resources ?? owner as IAgentOwnerResources
            ?? throw new ArgumentException("An owner resource session is required.", nameof(resources));
        _connectionState = AgentRuntimeConnectionState.Connecting;
    }

    public AgentRuntimeConnectionState ConnectionState => _connectionState;
    public bool IsRuntimeAvailable => _connectionState == AgentRuntimeConnectionState.Connected;

    public bool IsRetryableRuntimeFailure(
        Exception exception,
        CancellationToken callerCancellationToken = default)
        => IsRuntimeAvailabilityFailure(exception, callerCancellationToken);

    public event Action<AgentRuntimeConnectionState>? ConnectionStateChanged;
    public event Action? DashboardInvalidated;
    public event Action? GlobalPermissionsInvalidated;
    public event Action<string>? ProfileChanged;
    public event Action? SelectableCapabilitiesChanged;
    public event Action? WorkspacesChanged;
    public event Action<Guid>? SessionChanged;
    public event Action<Guid, AgentTurnRecord>? TurnChanged;
    public event Action<AgentTurnMutation>? TurnMutated;
    public event Action<Guid>? TranscriptReset;
    public event Action<Guid, AgentRunActivityUpdate>? RunActivityChanged;
    public event Action<AgentChatSnapshotProjection>? ChatSnapshotReloaded;

    public IReadOnlyList<AgentProfileRecord> ListProfiles() => ReadDashboardSnapshot().Profiles;
    public AgentProfileRecord? GetProfile(string profileId)
        => ReadDashboardSnapshot().Profiles.FirstOrDefault(profile => string.Equals(
            profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
    public AgentProfileModelBindingRecord? GetChatBinding(string profileId)
        => (GetProfile(profileId)?.ModelBindings ?? []).FirstOrDefault(binding => string.Equals(
            binding.CapabilityKind, AgentModelCapabilityKinds.Chat, StringComparison.OrdinalIgnoreCase));

    public void SaveProfile(string profileId, string displayName, string? description, string? instructions,
        string? chatProviderId, string? chatModelId, string? embeddingProviderId, string? embeddingModelId,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? selectableCapabilityAssignments = null,
        string? behaviorLoopId = null, string? behaviorLoopSourceId = null,
        string? behaviorLoopSettingsJson = null, string? chatModelSettingsJson = null)
        => throw AsyncOperationRequired(nameof(SaveProfileAsync));

    public void DeleteProfile(string profileId)
        => throw AsyncOperationRequired(nameof(DeleteProfileAsync));

    public async Task<IReadOnlyList<AgentBehaviorLoopDescriptor>> ListBehaviorLoopDescriptorsAsync(CancellationToken cancellationToken = default)
        => (await LoadCatalogAsync(new AgentCatalogRequest(), cancellationToken).ConfigureAwait(false)).BehaviorLoops;
    public async Task<IReadOnlyList<AgentProviderDescriptor>> ListChatProviderDescriptorsAsync(CancellationToken cancellationToken = default)
        => (await LoadCatalogAsync(new AgentCatalogRequest(), cancellationToken).ConfigureAwait(false)).ChatProviders;
    public async Task<IReadOnlyList<AgentEmbeddingProviderDescriptor>> ListEmbeddingProviderDescriptorsAsync(
        CancellationToken cancellationToken = default)
        => (await LoadCatalogAsync(new AgentCatalogRequest(), cancellationToken).ConfigureAwait(false)).EmbeddingProviders;
    public async Task<bool> HasProfileCapabilityConsumersAsync(string capabilityKind, CancellationToken cancellationToken = default)
        => string.Equals(capabilityKind, AgentModelCapabilityKinds.Embedding, StringComparison.OrdinalIgnoreCase)
            && (await LoadCatalogAsync(new AgentCatalogRequest(), cancellationToken).ConfigureAwait(false)).HasEmbeddingConsumers;

    public async Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListSelectableProfileCapabilitiesAsync(
        AgentProfileRecord? profile = null, CancellationToken cancellationToken = default)
        => (await LoadCatalogAsync(new AgentCatalogRequest(Profile: profile), cancellationToken).ConfigureAwait(false))
            .SelectableCapabilities;
    public async Task<IReadOnlyList<AgentToolCatalogEntry>> ListInstalledLocalToolsAsync(CancellationToken cancellationToken = default)
        => (await LoadCatalogAsync(new AgentCatalogRequest(), cancellationToken).ConfigureAwait(false)).LocalTools;
    public async Task<IReadOnlyList<AgentModelDescriptor>> ListChatModelsAsync(
        string? providerId, CancellationToken cancellationToken = default)
        => (await LoadCatalogAsync(new AgentCatalogRequest(ChatProviderId: providerId), cancellationToken).ConfigureAwait(false)).ChatModels;
    public async Task<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListEmbeddingModelsAsync(
        string? providerId, CancellationToken cancellationToken = default)
        => (await LoadCatalogAsync(new AgentCatalogRequest(EmbeddingProviderId: providerId), cancellationToken).ConfigureAwait(false)).EmbeddingModels;
    public async Task<AgentProviderReadiness?> GetChatProviderReadinessAsync(
        string? providerId, CancellationToken cancellationToken = default)
        => (await LoadCatalogAsync(new AgentCatalogRequest(ChatProviderId: providerId), cancellationToken).ConfigureAwait(false)).ChatReadiness;
    public async Task<AgentEmbeddingProviderReadiness?> GetEmbeddingProviderReadinessAsync(
        string? providerId, CancellationToken cancellationToken = default)
        => (await LoadCatalogAsync(new AgentCatalogRequest(EmbeddingProviderId: providerId), cancellationToken).ConfigureAwait(false)).EmbeddingReadiness;

    public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => ReadDashboardSnapshot().Workspaces;
    public AgentWorkspaceRecord? GetWorkspace(string workspaceId)
        => ReadDashboardSnapshot().Workspaces.FirstOrDefault(workspace => string.Equals(
            workspace.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase));
    public AgentWorkspaceRecord CreateWorkspace(string displayName)
        => throw AsyncOperationRequired(nameof(CreateWorkspaceAsync));

    public void SaveWorkspace(string workspaceId, string displayName, string? description)
    {
        var current = GetWorkspace(workspaceId) ?? throw new InvalidOperationException("Workspace was not found.");
        SaveWorkspaceAggregate(workspaceId, displayName, description, current.Paths, current.Documents,
            ListBindings(workspaceId).FirstOrDefault()?.ContributionId);
    }
    public void SaveWorkspaceAggregate(string workspaceId, string displayName, string? description,
        IReadOnlyList<AgentWorkspacePathRecord> paths, IReadOnlyList<AgentWorkspaceDocumentRecord> documents,
        string? executionTargetId)
        => throw AsyncOperationRequired(nameof(SaveWorkspaceAggregateAsync));

    public IReadOnlyList<AgentWorkspaceBindingRecord> ListBindings(string workspaceId)
        => ReadDashboardSnapshot().WorkspaceBindings.Where(binding => string.Equals(
            binding.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)).ToArray();
    public AgentWorkspaceBindingRecord SavePrimaryExecutionBinding(string workspaceId, string contributionId,
        string displayRole = AgentWorkspaceBindingRoles.PrimaryExecutionTarget)
    {
        var workspace = GetWorkspace(workspaceId) ?? throw new InvalidOperationException("Workspace was not found.");
        SaveWorkspaceAggregate(workspaceId, workspace.DisplayName, workspace.Description,
            workspace.Paths, workspace.Documents, contributionId);
        return new AgentWorkspaceBindingRecord(AgentWorkspaceDefaults.BuildPrimaryBindingId(workspaceId, displayRole),
            workspaceId, AgentWorkspaceBindingRoles.ExecutionTargetExtensionPoint, contributionId, displayRole,
            true, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        StartObservingChanges();
        _ = await LoadDashboardAsync(cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<AgentSessionRecord> ListSessions()
    {
        lock (_cacheLock)
        {
            return _knownSessions.Values.Select(static item => item.Session).ToArray();
        }
    }
    public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId)
    {
        lock (_cacheLock)
        {
            if (_workspaceSessions.TryGetValue(workspaceId, out var cached))
            {
                return cached.Select(static item => item.Session).ToArray();
            }
        }

        return [];
    }
    public AgentSessionRecord CreateSession(string title, Guid? parentSessionId = null, Guid? rootSessionId = null,
        Guid? parentRunId = null, long? parentRunRevision = null, string? parentToolCallId = null,
        string? taskId = null, string? profileId = null, string? behaviorLoopId = null,
        string? agentKind = null, string? workspaceId = null)
    {
        if (parentSessionId is not null || rootSessionId is not null || parentRunId is not null
            || parentToolCallId is not null || taskId is not null || agentKind is not null)
        {
            throw new NotSupportedException("App may only create root Agent sessions.");
        }
        throw AsyncOperationRequired(nameof(CreateRootSessionAsync));
    }
    public AgentSessionRecord? GetSession(Guid sessionId)
    {
        lock (_cacheLock)
        {
            if (_knownSessions.TryGetValue(sessionId, out var known))
            {
                return known.Session;
            }
        }

        return null;
    }
    public void UpdateSession(AgentSessionRecord session)
        => throw AsyncOperationRequired(nameof(UpdateSessionAsync));
    public async Task<AgentSessionSnapshot> CreateRootSessionAsync(
        string title,
        string profileId,
        string? behaviorLoopId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(
            _owner.ExecuteSessionCommandAsync,
            new AgentSessionCommand(
                AgentSessionCommandKind.Create,
                Title: title,
                ProfileId: profileId,
                BehaviorLoopId: behaviorLoopId,
                WorkspaceId: workspaceId),
            cancellationToken).ConfigureAwait(false);
        var created = result.Session
                      ?? throw new InvalidOperationException("Runtime did not return the created session.");
        CacheSession(created);
        return created;
    }
    public async Task<AgentSessionSnapshot> UpdateSessionAsync(
        AgentSessionRecord session,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(
            _owner.ExecuteSessionCommandAsync,
            new AgentSessionCommand(AgentSessionCommandKind.Update, Session: session),
            cancellationToken).ConfigureAwait(false);
        var updated = result.Session
                      ?? throw new InvalidOperationException("Runtime did not return the updated session.");
        CacheSession(updated);
        return updated;
    }
    public async Task DeleteSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = GetSession(sessionId) ?? new AgentSessionRecord(
            sessionId,
            string.Empty,
            AgentSessionState.Active,
            default,
            default);
        _ = await InvokeAsync(
            _owner.ExecuteSessionCommandAsync,
            new AgentSessionCommand(AgentSessionCommandKind.Delete, Session: session),
            cancellationToken).ConfigureAwait(false);
        RemoveCachedSession(sessionId);
    }
    public IReadOnlyList<AgentTurnRecord> ListTurns(Guid sessionId)
        => SnapshotTurns(sessionId);
    public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit)
        => SnapshotTurns(sessionId).TakeLast(Math.Max(0, limit)).ToArray();
    public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId, int limit)
        => SnapshotTurns(sessionId)
            .Where(turn => turn.CreatedAtUtc < beforeCreatedAtUtc
                           || turn.CreatedAtUtc == beforeCreatedAtUtc && turn.TurnId.CompareTo(beforeTurnId) < 0)
            .TakeLast(Math.Max(0, limit))
            .ToArray();
    public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId, int limit)
        => SnapshotTurns(sessionId)
            .Where(turn => turn.CreatedAtUtc > afterCreatedAtUtc
                           || turn.CreatedAtUtc == afterCreatedAtUtc && turn.TurnId.CompareTo(afterTurnId) > 0)
            .Take(Math.Max(0, limit))
            .ToArray();
    public AgentTurnRecord? GetTurn(Guid turnId)
    {
        lock (_cacheLock)
        {
            return _knownTurns.GetValueOrDefault(turnId);
        }
    }
    public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId)
    {
        lock (_cacheLock)
        {
            if (_knownSessions.TryGetValue(sessionId, out var known))
            {
                return known.Checkpoint;
            }
        }

        return null;
    }

    public AgentSessionPermissionState GetSessionState(Guid sessionId)
        => throw AsyncOperationRequired(nameof(LoadSessionPermissionsAsync));
    public void SetSessionUnrestrictedMode(Guid sessionId, bool isEnabled)
        => throw AsyncOperationRequired(nameof(SetSessionUnrestrictedModeAsync));
    public async Task<IReadOnlyList<AgentPermissionActionDescriptor>> ListActionsAsync(
        CancellationToken cancellationToken = default)
        => (await LoadGlobalPermissionsAsync(cancellationToken).ConfigureAwait(false)).Actions;
    public IReadOnlyList<AgentPermissionOverride> ListOverrides()
        => ReadGlobalPermissionsSnapshot().Overrides;
    public void SaveOverride(string actionId, string boundaryId, AgentPermissionDecision decision)
        => throw AsyncOperationRequired(nameof(SaveOverrideAsync));

    public void DeleteOverride(string actionId, string boundaryId)
        => throw AsyncOperationRequired(nameof(DeleteOverrideAsync));

    public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequestsForSessionTree(Guid sessionId)
        => throw AsyncOperationRequired(nameof(LoadSessionPermissionsAsync));

    public async Task<AgentChatPermissionProjection> LoadSessionPermissionsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var projection = await InvokeAsync(
            _owner.ExecutePermissionCommandAsync,
            new AgentPermissionCommand(AgentPermissionCommandKind.Read, sessionId),
            cancellationToken).ConfigureAwait(false);
        return new AgentChatPermissionProjection(
            projection.Revision,
            projection.SessionState,
            projection.PendingRequests);
    }
    public async Task<AgentChatPermissionProjection> SetSessionUnrestrictedModeAsync(
        Guid sessionId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        var projection = await InvokeAsync(
            _owner.ExecutePermissionCommandAsync,
            new AgentPermissionCommand(
                AgentPermissionCommandKind.SetUnrestricted,
                sessionId,
                isEnabled),
            cancellationToken).ConfigureAwait(false);
        return new AgentChatPermissionProjection(
            projection.Revision,
            projection.SessionState,
            projection.PendingRequests);
    }

    public async Task<AgentRunCheckpointRecord> QueueUserMessageAsync(Guid sessionId, string profileId,
        string userMessage, string workspaceId, IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        CancellationToken cancellationToken = default)
        => (await InvokeRunWithAttachmentsAsync(new AgentRunCommand(
            AgentRunCommandKind.Start,
            sessionId,
            profileId,
            userMessage,
            workspaceId, UserTurnId: Guid.NewGuid()), attachments, cancellationToken).ConfigureAwait(false))
            .Checkpoint ?? throw new InvalidOperationException("Runtime did not start the run.");
    async Task<AgentRunCheckpointRecord> IAgentCorrelatedRunGateway.QueueUserMessageAsync(Guid sessionId, string profileId,
        string userMessage, string workspaceId, IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        Guid userTurnId, CancellationToken cancellationToken)
        => (await InvokeRunWithAttachmentsAsync(new AgentRunCommand(
            AgentRunCommandKind.Start,
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            UserTurnId: userTurnId), attachments, cancellationToken).ConfigureAwait(false)).Checkpoint
           ?? throw new InvalidOperationException("Runtime did not start the run.");
    public async Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(Guid sessionId,
        Guid rollbackAnchorTurnId, string profileId, string userMessage, string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments, CancellationToken cancellationToken = default)
        => (await InvokeRunWithAttachmentsAsync(new AgentRunCommand(
            AgentRunCommandKind.RollbackAndStart,
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            RollbackAnchorTurnId: rollbackAnchorTurnId, UserTurnId: Guid.NewGuid()), attachments, cancellationToken)
            .ConfigureAwait(false)).Checkpoint ?? throw new InvalidOperationException("Runtime did not start the run.");
    async Task<AgentRunCheckpointRecord> IAgentCorrelatedRunGateway.RollbackAndQueueUserMessageAsync(Guid sessionId,
        Guid rollbackAnchorTurnId, string profileId, string userMessage, string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments, Guid userTurnId,
        CancellationToken cancellationToken)
        => (await InvokeRunWithAttachmentsAsync(new AgentRunCommand(
            AgentRunCommandKind.RollbackAndStart,
            sessionId,
            profileId,
            userMessage,
            workspaceId,
            RollbackAnchorTurnId: rollbackAnchorTurnId,
            UserTurnId: userTurnId), attachments, cancellationToken).ConfigureAwait(false)).Checkpoint
           ?? throw new InvalidOperationException("Runtime did not start the run.");
    public async Task<AgentRunCommandStatus> GetRunCommandStatusAsync(
        Guid sessionId,
        Guid userTurnId,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(
            _owner.GetRunStatusAsync,
            new AgentRunCommandStatusRequest(sessionId, userTurnId),
            cancellationToken).ConfigureAwait(false)).Status;
    public async Task<AgentRunCheckpointRecord?> StopAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => (await InvokeAsync(_owner.ExecuteRunCommandAsync,
            new AgentRunCommand(AgentRunCommandKind.Stop, sessionId), cancellationToken).ConfigureAwait(false)).Checkpoint;
    public async Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(Guid sessionId, string requestId,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(_owner.ExecuteRunCommandAsync, new AgentRunCommand(
            AgentRunCommandKind.ApprovePermission, sessionId, PermissionRequestId: requestId), cancellationToken)
            .ConfigureAwait(false)).Checkpoint;
    async Task<AgentRunCheckpointRecord?> IAgentChatRunGateway.ApprovePendingPermissionAsync(
        Guid sessionId,
        string requestId,
        bool approveForSession,
        CancellationToken cancellationToken)
        => (await InvokeAsync(_owner.ExecuteRunCommandAsync, new AgentRunCommand(
            AgentRunCommandKind.ApprovePermission,
            sessionId,
            PermissionRequestId: requestId,
            ApproveForSession: approveForSession), cancellationToken).ConfigureAwait(false)).Checkpoint;
    public async Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(Guid sessionId, string requestId,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(_owner.ExecuteRunCommandAsync, new AgentRunCommand(
            AgentRunCommandKind.DenyPermission, sessionId, PermissionRequestId: requestId), cancellationToken)
            .ConfigureAwait(false)).Checkpoint;

    public async Task<AgentTranscriptPage> LoadTranscriptPageAsync(
        AgentTranscriptPageRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = await InvokeAsync(
            _owner.GetTranscriptPageAsync,
            request,
            cancellationToken).ConfigureAwait(false);
        CacheTurns(page.Turns);
        return page;
    }

    public async Task<IReadOnlyList<AgentExecutionTargetDescriptor>> ListTargetsAsync(
        CancellationToken cancellationToken = default)
        => (await LoadCatalogAsync(new AgentCatalogRequest(), cancellationToken).ConfigureAwait(false)).ExecutionTargets;
    public async Task<AgentExecutionTargetWarmupResult> WarmWorkspaceAsync(
        AgentWorkspaceRecord workspace, CancellationToken cancellationToken = default)
        => (await InvokeAsync(_owner.ExecuteWorkspaceCommandAsync,
            new AgentWorkspaceCommand(AgentWorkspaceCommandKind.Warmup, WorkspaceId: workspace.WorkspaceId),
            cancellationToken).ConfigureAwait(false)).Warmup
           ?? AgentExecutionTargetWarmupResult.Skipped("Workspace has no execution target.");

    private AgentDashboardProjection ReadDashboardSnapshot()
    {
        lock (_cacheLock)
        {
            return _dashboard
                   ?? throw new InvalidOperationException(
                       "Agent dashboard data must be loaded asynchronously before it is read.");
        }
    }

    private AgentCatalogProjection ReadCatalogSnapshot()
    {
        lock (_cacheLock)
        {
            return _catalogSnapshot
                   ?? throw new InvalidOperationException(
                       "Agent catalog data must be loaded asynchronously before it is read.");
        }
    }

    private IReadOnlyList<AgentTurnRecord> SnapshotTurns(Guid sessionId)
    {
        lock (_cacheLock)
        {
            return _knownTurns.Values
                .Where(turn => turn.SessionId == sessionId)
                .OrderBy(turn => turn.CreatedAtUtc)
                .ThenBy(turn => turn.TurnId)
                .ToArray();
        }
    }

    private static NotSupportedException AsyncOperationRequired(string operation)
        => new($"App Runtime operation '{operation}' must be invoked asynchronously.");

    private async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        Func<TRequest, CancellationToken, ValueTask<TResponse>> operation, TRequest request, CancellationToken cancellationToken)
        where TRequest : class where TResponse : class
    {
        ThrowIfUnavailable();
        try
        {
            await _resources.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
            var response = await operation(request, cancellationToken).ConfigureAwait(false);
            if (!IsObservationEstablishingConnection())
            {
                SetConnectionState(AgentRuntimeConnectionState.Connected);
            }
            return response;
        }
        catch (Exception exception)
        {
            if (IsRuntimeAvailabilityFailure(exception, cancellationToken)
                && !IsObservationEstablishingConnection())
            {
                SetConnectionState(AgentRuntimeConnectionState.Unavailable);
            }
            throw;
        }
    }

    private static void Raise(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch { /* Projection listeners cannot break replay state. */ }
        }
    }

    private static bool IsRuntimeAvailabilityFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is AgentPackageUnavailableException || exception is OperationCanceledException && cancellationToken.IsCancellationRequested) return false;
        if (exception is SunderRpcException rpcException)
            return rpcException.Error.Kind is SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.NotFound;
        return exception is HttpRequestException or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException;
    }

    private void ThrowIfUnavailable() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void Raise<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try { handler(value); }
            catch { /* Projection listeners cannot break replay state. */ }
        }
    }

    private static void Raise<T1, T2>(Action<T1, T2>? handlers, T1 first, T2 second)
    {
        if (handlers is null) return;
        foreach (Action<T1, T2> handler in handlers.GetInvocationList())
        {
            try { handler(first, second); }
            catch { /* Projection listeners cannot break replay state. */ }
        }
    }

    private static async Task<bool> DelayForReconnectAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static TimeSpan NextReconnectDelay(TimeSpan current)
        => TimeSpan.FromMilliseconds(Math.Min(current.TotalMilliseconds * 2, MaximumReconnectDelay.TotalMilliseconds));

    private bool IsObservationEstablishingConnection()
    {
        lock (_observationLock)
        {
            return _observationCancellation is not null
                   && _connectionState != AgentRuntimeConnectionState.Connected;
        }
    }

    private void InvalidateDashboard() { lock (_cacheLock) { _dashboard = null; _dashboardLoad = null; } }
    private void InvalidateCatalog()
    {
        lock (_cacheLock)
        {
            _catalogSnapshot = null;
            _catalogGeneration++;
            _catalogs.Clear();
        }
    }
    private void InvalidateSessions()
    {
        lock (_cacheLock)
        {
            _workspaceSessions.Clear();
            _knownSessions.Clear();
        }
    }
    private void InvalidateAll()
    {
        lock (_cacheLock)
        {
            _dashboard = null;
            _dashboardLoad = null;
            _workspaceSessions.Clear();
            _knownSessions.Clear();
            _knownTurns.Clear();
            _globalPermissions = null;
            _globalPermissionRevision = -1;
            _globalPermissionGeneration++;
            _catalogSnapshot = null;
            _catalogGeneration++;
            _catalogs.Clear();
        }
    }
    private void CacheSession(AgentSessionSnapshot snapshot)
    {
        lock (_cacheLock)
        {
            RemoveCachedSessionCore(snapshot.Session.SessionId);
            _knownSessions[snapshot.Session.SessionId] = snapshot;
            if (!string.IsNullOrWhiteSpace(snapshot.Session.WorkspaceId)
                && _workspaceSessions.TryGetValue(snapshot.Session.WorkspaceId, out var workspaceItems))
            {
                workspaceItems.Add(snapshot);
                workspaceItems.Sort(static (left, right) =>
                    right.Session.UpdatedAtUtc.CompareTo(left.Session.UpdatedAtUtc));
            }
        }
    }
    private void RemoveCachedSession(Guid sessionId)
    {
        lock (_cacheLock)
        {
            RemoveCachedSessionCore(sessionId);
        }
    }
    private void RemoveCachedSessionCore(Guid sessionId)
    {
        _knownSessions.Remove(sessionId);
        RemoveCachedTurnsCore(sessionId);
        foreach (var workspaceItems in _workspaceSessions.Values)
        {
            workspaceItems.RemoveAll(item => item.Session.SessionId == sessionId);
        }
    }
    private void SetConnectionState(AgentRuntimeConnectionState state)
    {
        if (_disposed && state != AgentRuntimeConnectionState.Disposed) return;
        if (_connectionState == state) return;
        _connectionState = state;
        var handlers = ConnectionStateChanged;
        if (handlers is null) return;
        foreach (Action<AgentRuntimeConnectionState> handler in handlers.GetInvocationList())
        {
            try { handler(state); }
            catch { /* Presentation listeners cannot break Runtime transport state. */ }
        }
    }
    private void SetConnectionStateIfCurrent(int generation, AgentRuntimeConnectionState state)
    {
        if (IsCurrentObservation(generation))
        {
            SetConnectionState(state);
        }
    }
}
