using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Models;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Runtime;

internal sealed class AgentRuntimeChangeHub :
    IPackageRuntimeStreamHandler<AgentChangeSubscription, AgentRuntimeChange>, IDisposable
{
    private const int ReplayCapacity = 256;
    private const int SubscriberCapacity = 64;
    private readonly AgentProfileService _profiles;
    private readonly AgentWorkspaceService _workspaces;
    private readonly AgentSessionService _sessions;
    private readonly object _gate = new();
    private readonly Dictionary<long, Subscriber> _subscribers = [];
    private readonly Queue<AgentRuntimeChange> _replay = new(ReplayCapacity);
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private long _revision;
    private long _subscriberId;

    public AgentRuntimeChangeHub(
        AgentProfileService profiles,
        AgentWorkspaceService workspaces,
        AgentSessionService sessions)
    {
        _profiles = profiles;
        _workspaces = workspaces;
        _sessions = sessions;
        profiles.ProfileChanged += OnProfileChanged;
        profiles.SelectableCapabilitiesChanged += OnCatalogChanged;
        workspaces.WorkspacesChanged += OnWorkspacesChanged;
        sessions.SessionChanged += OnSessionChanged;
        sessions.TurnMutated += OnTurnMutated;
        sessions.TranscriptReset += OnTranscriptReset;
        sessions.RunActivityChanged += OnRunActivityChanged;
    }

    public long Revision => Interlocked.Read(ref _revision);

    public string InstanceId => _instanceId;

    public void NotifyPermissionChanged(Guid? sessionId)
        => Publish(new AgentRuntimeChange(
            0, AgentRuntimeChangeKind.Permission, SessionId: sessionId));

    public async IAsyncEnumerable<AgentRuntimeChange> SubscribeAsync(
        AgentChangeSubscription request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriber = new Subscriber(
            Channel.CreateBounded<AgentRuntimeChange>(new BoundedChannelOptions(SubscriberCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            }),
            request.SupportsTurnMutations);
        AgentRuntimeChange[] replay;
        AgentRuntimeChange? reset = null;
        long subscribedRevision;
        var id = Interlocked.Increment(ref _subscriberId);
        lock (_gate)
        {
            var revision = Revision;
            subscribedRevision = revision;
            var oldestRevision = _replay.Count == 0 ? revision + 1 : _replay.Peek().Revision;
            if (request.AfterRevision > revision || request.AfterRevision < oldestRevision - 1)
            {
                replay = [];
                reset = new AgentRuntimeChange(
                    revision,
                    AgentRuntimeChangeKind.ResnapshotRequired,
                    RuntimeInstanceId: _instanceId);
            }
            else
            {
                replay = _replay.Where(change => change.Revision > request.AfterRevision).ToArray();
            }
            _subscribers[id] = subscriber;
        }
        try
        {
            if (reset is not null)
            {
                yield return reset;
            }
            else
            {
                foreach (var change in replay)
                {
                    yield return ProjectForSubscriber(change, subscriber.SupportsTurnMutations);
                }
                yield return new AgentRuntimeChange(
                    subscribedRevision,
                    AgentRuntimeChangeKind.Connected,
                    RuntimeInstanceId: _instanceId);
            }
            await foreach (var change in subscriber.Channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
            if (subscriber.Overflowed)
            {
                yield return new AgentRuntimeChange(
                    Revision,
                    AgentRuntimeChangeKind.ResnapshotRequired,
                    RuntimeInstanceId: _instanceId);
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(id);
            }
            subscriber.Channel.Writer.TryComplete();
        }
    }

    private void OnProfileChanged(string profileId)
        => Publish(new AgentRuntimeChange(
            0, AgentRuntimeChangeKind.Profile, ProfileId: profileId,
            Profile: _profiles.GetProfile(profileId)));

    private void OnCatalogChanged()
        => Publish(new AgentRuntimeChange(0, AgentRuntimeChangeKind.Catalog));

    private void OnWorkspacesChanged()
        => Publish(new AgentRuntimeChange(0, AgentRuntimeChangeKind.Workspace));

    private void OnSessionChanged(Guid sessionId)
    {
        var session = _sessions.GetSession(sessionId);
        Publish(new AgentRuntimeChange(
            0, AgentRuntimeChangeKind.Session, SessionId: sessionId,
            WorkspaceId: session?.WorkspaceId,
            Session: session is null
                ? null
                : new AgentSessionSnapshot(session, _sessions.GetLatestCheckpoint(sessionId))));
    }

    private void OnTurnChanged(Guid sessionId, AgentTurnRecord turn)
        => Publish(new AgentRuntimeChange(
            0, AgentRuntimeChangeKind.Turn, SessionId: sessionId, Turn: turn));

    private void OnTurnMutated(AgentTurnMutation mutation)
        => Publish(new AgentRuntimeChange(
            0,
            AgentRuntimeChangeKind.TurnMutation,
            SessionId: mutation.SessionId,
            Turn: _sessions.GetTranscriptHeader(mutation.TurnId) ?? mutation.Turn,
            TurnMutation: mutation));

    private void OnTranscriptReset(Guid sessionId)
        => Publish(new AgentRuntimeChange(
            0, AgentRuntimeChangeKind.TranscriptReset, SessionId: sessionId));

    private void OnRunActivityChanged(Guid sessionId, AgentRunActivityUpdate activity)
        => Publish(new AgentRuntimeChange(
            0, AgentRuntimeChangeKind.RunActivity, SessionId: sessionId, RunActivity: activity));

    private void Publish(AgentRuntimeChange change)
    {
        lock (_gate)
        {
            change = change with
            {
                Revision = Interlocked.Increment(ref _revision),
                RuntimeInstanceId = _instanceId,
            };
            change = AgentRuntimePayloadLimits.ProjectChange(change);
            change = AgentRuntimePayloadLimits.ReplaceWithResnapshotIfOversized(
                change,
                ProjectForSubscriber(change, supportsTurnMutations: true),
                ProjectForSubscriber(change, supportsTurnMutations: false));
            _replay.Enqueue(change);
            while (_replay.Count > ReplayCapacity)
            {
                _replay.Dequeue();
            }
            foreach (var subscriber in _subscribers.Values)
            {
                var projected = ProjectForSubscriber(change, subscriber.SupportsTurnMutations);
                if (!subscriber.Channel.Writer.TryWrite(projected))
                {
                    subscriber.Overflowed = true;
                    subscriber.Channel.Writer.TryComplete();
                }
            }
        }
    }

    private static AgentRuntimeChange ProjectForSubscriber(
        AgentRuntimeChange change,
        bool supportsTurnMutations)
    {
        if (change.Kind != AgentRuntimeChangeKind.TurnMutation)
        {
            return change;
        }

        return supportsTurnMutations
            ? change with { Turn = null }
            : change with
            {
                Kind = AgentRuntimeChangeKind.Turn,
                TurnMutation = null,
            };
    }

    public void Dispose()
    {
        _profiles.ProfileChanged -= OnProfileChanged;
        _profiles.SelectableCapabilitiesChanged -= OnCatalogChanged;
        _workspaces.WorkspacesChanged -= OnWorkspacesChanged;
        _sessions.SessionChanged -= OnSessionChanged;
        _sessions.TurnMutated -= OnTurnMutated;
        _sessions.TranscriptReset -= OnTranscriptReset;
        _sessions.RunActivityChanged -= OnRunActivityChanged;
        lock (_gate)
        {
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Channel.Writer.TryComplete();
            }
            _subscribers.Clear();
        }
    }

    private sealed class Subscriber(
        Channel<AgentRuntimeChange> channel,
        bool supportsTurnMutations)
    {
        public Channel<AgentRuntimeChange> Channel { get; } = channel;
        public bool SupportsTurnMutations { get; } = supportsTurnMutations;
        public bool Overflowed { get; set; }
    }
}

internal sealed class AgentDashboardHandler(
    AgentProfileService profiles,
    AgentWorkspaceService workspaces,
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentDashboardRequest, AgentDashboardProjection>
{
    public ValueTask<AgentDashboardProjection> HandleAsync(
        AgentDashboardRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workspaceItems = workspaces.ListWorkspaces();
        return ValueTask.FromResult(new AgentDashboardProjection(
            changes.Revision,
            profiles.ListProfiles(),
            workspaceItems,
            workspaceItems
                .SelectMany(workspace => workspaces.ListBindings(workspace.WorkspaceId))
                .ToArray()));
    }
}

internal sealed class AgentChatSnapshotHandler(
    AgentLocalStore store,
    AgentChatSelectionStateService selectionState,
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentChatSnapshotRequest, AgentChatSnapshotProjection>
{
    private readonly object _cacheGate = new();
    private ChatSnapshotCacheEntry? _cache;

    public async ValueTask<AgentChatSnapshotProjection> HandleAsync(
        AgentChatSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        var storedProfileIdTask = selectionState.GetSelectedProfileIdAsync(cancellationToken);
        var storedWorkspaceIdTask = selectionState.GetSelectedWorkspaceIdAsync(cancellationToken);
        await Task.WhenAll(storedProfileIdTask, storedWorkspaceIdTask).ConfigureAwait(false);
        var revision = changes.Revision;
        var storedProfileId = Normalize(request.PreferredProfileId)
                              ?? await storedProfileIdTask.ConfigureAwait(false);
        var storedWorkspaceId = Normalize(request.PreferredWorkspaceId)
                                ?? await storedWorkspaceIdTask.ConfigureAwait(false);
        var storedSessionId = storedWorkspaceId is null
            ? null
            : await selectionState.GetSelectedSessionIdAsync(storedWorkspaceId, cancellationToken).ConfigureAwait(false);
        var key = new ChatSnapshotCacheKey(
            revision,
            request,
            storedProfileId,
            storedWorkspaceId,
            storedSessionId);
        lock (_cacheGate)
        {
            if (_cache is { } cached && cached.Key == key)
            {
                return cached.Snapshot;
            }
        }

        var snapshot = await store.ReadChatSnapshotAsync(
            revision,
            request,
            storedProfileId,
            storedWorkspaceId,
            (workspaceId, _) => Task.FromResult(
                string.Equals(workspaceId, storedWorkspaceId, StringComparison.OrdinalIgnoreCase)
                    ? storedSessionId
                    : null),
            cancellationToken).ConfigureAwait(false);
        snapshot = snapshot with { RuntimeInstanceId = changes.InstanceId };
        if (changes.Revision == revision)
        {
            lock (_cacheGate)
            {
                _cache = new ChatSnapshotCacheEntry(key, snapshot);
            }
        }
        return snapshot;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct ChatSnapshotCacheKey(
        long Revision,
        AgentChatSnapshotRequest Request,
        string? StoredProfileId,
        string? StoredWorkspaceId,
        Guid? StoredSessionId);

    private sealed record ChatSnapshotCacheEntry(
        ChatSnapshotCacheKey Key,
        AgentChatSnapshotProjection Snapshot);
}

internal sealed class AgentTranscriptPageHandler(
    AgentSessionService sessions,
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentTranscriptPageRequest, AgentTranscriptPage>
{
    public ValueTask<AgentTranscriptPage> HandleAsync(
        AgentTranscriptPageRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = Math.Clamp(request.Limit, 1, 500);
        IReadOnlyList<AgentTurnRecord> turns = request.Direction switch
        {
            AgentTranscriptPageDirection.Recent => sessions.ListRecentTranscriptHeaders(request.SessionId, limit + 1),
            AgentTranscriptPageDirection.Before when request.AnchorCreatedAtUtc is { } createdAt
                                                     && request.AnchorTurnId is { } turnId
                => sessions.ListTranscriptHeadersBefore(request.SessionId, createdAt, turnId, limit + 1),
            AgentTranscriptPageDirection.After when request.AnchorCreatedAtUtc is { } createdAt
                                                    && request.AnchorTurnId is { } turnId
                => sessions.ListTranscriptHeadersAfter(request.SessionId, createdAt, turnId, limit + 1),
            AgentTranscriptPageDirection.Turn when request.AnchorTurnId is { } turnId
                => sessions.GetTranscriptHeader(turnId) is { } turn ? [turn] : [],
            _ => throw new InvalidOperationException("The transcript page anchor is invalid."),
        };
        var hasMore = turns.Count > limit;
        var pageTurns = !hasMore
            ? turns
            : request.Direction is AgentTranscriptPageDirection.Recent or AgentTranscriptPageDirection.Before
                ? turns.Skip(turns.Count - limit).ToArray()
                : turns.Take(limit).ToArray();
        return ValueTask.FromResult(AgentRuntimePayloadLimits.FitTranscriptPage(
            new AgentTranscriptPage(changes.Revision, pageTurns, hasMore),
            request.Direction));
    }
}

internal sealed class AgentTranscriptToolDetailHandler(AgentSessionService sessions)
    : IPackageRuntimeOperationHandler<AgentTranscriptToolDetailRequest, AgentTranscriptToolDetailResponse>
{
    public ValueTask<AgentTranscriptToolDetailResponse> HandleAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var detail = sessions.GetTranscriptToolDetail(request);
        return ValueTask.FromResult(new AgentTranscriptToolDetailResponse(
            detail is null ? null : AgentRuntimePayloadLimits.FitToolDetail(detail)));
    }
}

internal sealed class AgentCatalogHandler(
    AgentProfileService profiles,
    AgentExecutionTargetService executionTargets,
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentCatalogRequest, AgentCatalogProjection>
{
    public async ValueTask<AgentCatalogProjection> HandleAsync(
        AgentCatalogRequest request, CancellationToken cancellationToken = default)
    {
        var toolsTask = profiles.ListInstalledLocalToolsAsync(cancellationToken);
        var capabilitiesTask = profiles.ListSelectableProfileCapabilitiesAsync(request.Profile, cancellationToken);
        var chatModelsTask = profiles.ListChatModelsAsync(request.ChatProviderId, cancellationToken);
        var embeddingModelsTask = profiles.ListEmbeddingModelsAsync(request.EmbeddingProviderId, cancellationToken);
        var chatReadinessTask = profiles.GetChatProviderReadinessAsync(request.ChatProviderId, cancellationToken);
        var embeddingReadinessTask = profiles.GetEmbeddingProviderReadinessAsync(request.EmbeddingProviderId, cancellationToken);
        await Task.WhenAll(toolsTask, capabilitiesTask, chatModelsTask, embeddingModelsTask,
            chatReadinessTask, embeddingReadinessTask).ConfigureAwait(false);
        return new AgentCatalogProjection(
            changes.Revision,
            profiles.ListChatProviderDescriptors(),
            profiles.ListEmbeddingProviderDescriptors(),
            profiles.ListBehaviorLoopDescriptors(),
            executionTargets.ListTargets(),
            await toolsTask.ConfigureAwait(false),
            await capabilitiesTask.ConfigureAwait(false),
            profiles.HasProfileCapabilityConsumers(AgentModelCapabilityKinds.Embedding),
            await chatModelsTask.ConfigureAwait(false),
            await embeddingModelsTask.ConfigureAwait(false),
            await chatReadinessTask.ConfigureAwait(false),
            await embeddingReadinessTask.ConfigureAwait(false));
    }
}

internal sealed class AgentProfileCommandHandler(
    AgentProfileService profiles,
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentProfileCommand, AgentProfileCommandResult>
{
    public async ValueTask<AgentProfileCommandResult> HandleAsync(
        AgentProfileCommand request, CancellationToken cancellationToken = default)
    {
        AgentProfileRecord? profile;
        switch (request.Kind)
        {
            case AgentProfileCommandKind.Create:
                profile = await profiles.CreateProfileAsync(request.DisplayName ?? "New Agent", cancellationToken)
                    .ConfigureAwait(false);
                break;
            case AgentProfileCommandKind.Save:
                var profileId = Require(request.ProfileId, "Profile id");
                profiles.SaveProfile(profileId, request.DisplayName ?? "Unnamed Profile", request.Description,
                    request.Instructions, request.ChatProviderId, request.ChatModelId,
                    request.EmbeddingProviderId, request.EmbeddingModelId, request.Assignments,
                    request.BehaviorLoopId, request.BehaviorLoopSourceId,
                    request.BehaviorLoopSettingsJson, request.ChatModelSettingsJson);
                profile = profiles.GetProfile(profileId);
                break;
            case AgentProfileCommandKind.Delete:
                profiles.DeleteProfile(Require(request.ProfileId, "Profile id"));
                profile = null;
                break;
            default:
                throw new InvalidOperationException("Unknown profile command.");
        }
        return new AgentProfileCommandResult(changes.Revision, profile);
    }

    private static string Require(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name} is required.") : value;
}

internal sealed class AgentWorkspaceCommandHandler(
    AgentWorkspaceService workspaces,
    AgentExecutionTargetWarmupService warmup,
    AgentSessionDeletionService deletion,
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentWorkspaceCommand, AgentWorkspaceCommandResult>
{
    public async ValueTask<AgentWorkspaceCommandResult> HandleAsync(
        AgentWorkspaceCommand request, CancellationToken cancellationToken = default)
    {
        AgentWorkspaceRecord? workspace;
        AgentExecutionTargetWarmupResult? result = null;
        switch (request.Kind)
        {
            case AgentWorkspaceCommandKind.Create:
                workspace = workspaces.CreateWorkspace(request.DisplayName ?? "New Workspace");
                break;
            case AgentWorkspaceCommandKind.Save:
                var workspaceId = Require(request.WorkspaceId);
                workspaces.SaveWorkspaceAggregate(workspaceId, request.DisplayName ?? "Unnamed Workspace",
                    request.Description, request.Paths ?? [], request.Documents ?? [], request.ExecutionTargetId);
                workspace = workspaces.GetWorkspace(workspaceId);
                break;
            case AgentWorkspaceCommandKind.Delete:
                await deletion.DeleteWorkspaceAsync(
                    Require(request.WorkspaceId),
                    cancellationToken).ConfigureAwait(false);
                workspace = null;
                break;
            case AgentWorkspaceCommandKind.Warmup:
                workspace = workspaces.GetWorkspace(Require(request.WorkspaceId))
                    ?? throw new InvalidOperationException("Workspace was not found.");
                result = await warmup.WarmWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException("Unknown workspace command.");
        }
        return new AgentWorkspaceCommandResult(changes.Revision, workspace, result);
    }

    private static string Require(string? value)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException("Workspace id is required.") : value;
}

internal sealed class AgentSessionCommandHandler(
    AgentSessionService sessions,
    AgentSessionDeletionService deletion,
    AgentSessionDeletionFence deletionFence,
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentSessionCommand, AgentSessionCommandResult>
{
    public async ValueTask<AgentSessionCommandResult> HandleAsync(
        AgentSessionCommand request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AgentSessionRecord? session;
        switch (request.Kind)
        {
            case AgentSessionCommandKind.Create:
                if (deletionFence.IsWorkspaceFenced(request.WorkspaceId))
                {
                    throw new InvalidOperationException("The workspace is being deleted and cannot create a session.");
                }
                session = sessions.CreateSession(request.Title ?? "New Session", profileId: request.ProfileId,
                    behaviorLoopId: request.BehaviorLoopId, workspaceId: request.WorkspaceId);
                break;
            case AgentSessionCommandKind.Update:
                session = request.Session ?? throw new InvalidOperationException("Session is required.");
                sessions.UpdateSession(session);
                break;
            case AgentSessionCommandKind.Delete:
                session = request.Session ?? throw new InvalidOperationException("Session is required.");
                await deletion.DeleteSessionAsync(
                    session.SessionId,
                    cancellationToken).ConfigureAwait(false);
                session = null;
                break;
            default:
                throw new InvalidOperationException("Unknown session command.");
        }
        return new AgentSessionCommandResult(
            changes.Revision,
            session is null ? null : new AgentSessionSnapshot(session, sessions.GetLatestCheckpoint(session.SessionId)));
    }
}

internal sealed class AgentRunCommandHandler(
    AgentRunCoordinator runs,
    AgentSessionService sessions,
    AgentAttachmentTransferService attachmentTransfers,
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentRunCommand, AgentRunCommandResult>,
      IPackageRuntimeOperationHandler<AgentRunCommandStatusRequest, AgentRunCommandStatusResult>
{
    private readonly ConcurrentDictionary<Guid, Guid> _pendingUserTurns = new();

    public async ValueTask<AgentRunCommandResult> HandleAsync(
        AgentRunCommand request, CancellationToken cancellationToken = default)
    {
        if (request.Kind is AgentRunCommandKind.Start or AgentRunCommandKind.RollbackAndStart)
        {
            var userTurnId = request.UserTurnId ?? Guid.NewGuid();
            if (!_pendingUserTurns.TryAdd(userTurnId, request.SessionId))
            {
                throw new InvalidOperationException("The correlated run command is already pending.");
            }
            try
            {
                if ((request.UserMessage?.Length ?? 0) > AgentRuntimePayloadLimits.MaximumRunMessageCharacters)
                {
                    throw new InvalidOperationException(
                        $"Agent message exceeds the {AgentRuntimePayloadLimits.MaximumRunMessageCharacters} character Runtime transport limit.");
                }
                Guid? rollbackAnchorTurnId = request.Kind == AgentRunCommandKind.RollbackAndStart
                    ? request.RollbackAnchorTurnId
                      ?? throw new InvalidOperationException("Rollback anchor is required.")
                    : null;
                var admission = await runs.AdmitTransferredUserTurnAsync(
                    request.SessionId,
                    Require(request.ProfileId, "Profile id"),
                    request.UserMessage ?? string.Empty,
                    Require(request.WorkspaceId, "Workspace id"),
                    request.AttachmentHandles ?? [],
                    attachmentTransfers,
                    userTurnId,
                    rollbackAnchorTurnId,
                    cancellationToken).ConfigureAwait(false);
                runs.SignalDispatcher();
                return new AgentRunCommandResult(
                    changes.Revision,
                    admission.Checkpoint,
                    admission.Run.Key.RunId,
                    userTurnId);
            }
            finally
            {
                _pendingUserTurns.TryRemove(userTurnId, out _);
            }
        }

        var checkpoint = request.Kind switch
        {
            AgentRunCommandKind.Stop => await ((IAgentRunGateway)runs).StopAsync(request.SessionId, cancellationToken),
            AgentRunCommandKind.ApprovePermission => await runs.ApprovePendingPermissionAsync(
                request.SessionId,
                Require(request.PermissionRequestId, "Permission request id"),
                request.ApproveForSession,
                cancellationToken),
            AgentRunCommandKind.DenyPermission => await ((IAgentRunGateway)runs).DenyPendingPermissionAsync(
                request.SessionId, Require(request.PermissionRequestId, "Permission request id"), cancellationToken),
            _ => throw new InvalidOperationException("Unknown run command."),
        };
        if (request.Kind is AgentRunCommandKind.ApprovePermission or AgentRunCommandKind.DenyPermission)
        {
            changes.NotifyPermissionChanged(request.SessionId);
        }
        return new AgentRunCommandResult(changes.Revision, checkpoint);
    }

    public ValueTask<AgentRunCommandStatusResult> HandleAsync(
        AgentRunCommandStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = GetCommittedStatus(request);
        if (status != AgentRunCommandStatus.Committed
            && _pendingUserTurns.TryGetValue(request.UserTurnId, out var pendingSessionId)
            && pendingSessionId == request.SessionId)
        {
            status = AgentRunCommandStatus.Pending;
        }
        else if (status != AgentRunCommandStatus.Committed)
        {
            // Admission can commit between the first durable read and pending-map removal.
            status = GetCommittedStatus(request);
        }
        return ValueTask.FromResult(new AgentRunCommandStatusResult(changes.Revision, status));
    }

    private AgentRunCommandStatus GetCommittedStatus(AgentRunCommandStatusRequest request)
    {
        var run = sessions.Store.GetRunByUserTurnId(request.UserTurnId);
        return run?.Key.SessionId == request.SessionId
            ? AgentRunCommandStatus.Committed
            : AgentRunCommandStatus.Absent;
    }

    private static string Require(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name} is required.") : value;
}

internal sealed class AgentPermissionCommandHandler(
    AgentPermissionService permissions,
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentPermissionCommand, AgentPermissionProjection>
{
    public ValueTask<AgentPermissionProjection> HandleAsync(
        AgentPermissionCommand request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (request.Kind)
        {
            case AgentPermissionCommandKind.SetUnrestricted:
                permissions.SetSessionUnrestrictedMode(RequireSession(request), request.IsEnabled == true);
                break;
            case AgentPermissionCommandKind.SaveOverride:
                permissions.SaveOverride(Require(request.ActionId), Require(request.BoundaryId),
                    request.Decision ?? throw new InvalidOperationException("Permission decision is required."));
                break;
            case AgentPermissionCommandKind.DeleteOverride:
                permissions.DeleteOverride(Require(request.ActionId), Require(request.BoundaryId));
                break;
            case AgentPermissionCommandKind.SaveSessionApproval:
                permissions.SaveSessionApproval(RequireSession(request), Require(request.ActionId), Require(request.BoundaryId));
                break;
        }
        if (request.Kind != AgentPermissionCommandKind.Read)
        {
            changes.NotifyPermissionChanged(request.SessionId);
        }
        var sessionId = request.SessionId;
        var includeGlobalRules = sessionId is null;
        return ValueTask.FromResult(new AgentPermissionProjection(
            changes.Revision,
            sessionId is { } id ? permissions.GetSessionState(id) : null,
            includeGlobalRules ? permissions.ListActions() : [],
            includeGlobalRules ? permissions.ListOverrides() : [],
            sessionId is { } pendingId ? permissions.ListPendingRequestsForSessionTree(pendingId) : []));
    }

    private static Guid RequireSession(AgentPermissionCommand request)
        => request.SessionId ?? throw new InvalidOperationException("Session id is required.");
    private static string Require(string? value)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException("Permission identifier is required.") : value;
}

internal sealed class AgentAttachmentTransferHandler(
    AgentAttachmentTransferService transfers,
    AgentAttachmentService attachments)
    : IPackageRuntimeOperationHandler<AgentAttachmentTransferRequest, AgentAttachmentTransferResult>
{
    public async ValueTask<AgentAttachmentTransferResult> HandleAsync(
        AgentAttachmentTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (request.Kind)
        {
            case AgentAttachmentTransferKind.BeginUpload:
                return transfers.BeginUpload(request.Upload
                    ?? throw new InvalidOperationException("Attachment upload descriptor is required."));
            case AgentAttachmentTransferKind.WriteUploadChunk:
                return transfers.WriteUploadChunk(
                    RequireTransferId(request),
                    request.Offset,
                    request.Content ?? throw new InvalidOperationException("Attachment upload content is required."));
            case AgentAttachmentTransferKind.CompleteUpload:
                return transfers.CompleteUpload(RequireTransferId(request));
            case AgentAttachmentTransferKind.AbortUpload:
                transfers.AbortUpload(RequireTransferId(request));
                return new AgentAttachmentTransferResult(IsComplete: true);
            case AgentAttachmentTransferKind.ReadDownloadChunk:
                var metadata = request.Metadata
                    ?? throw new InvalidOperationException("Attachment metadata is required.");
                var chunk = await attachments.ReadAttachmentChunkAsync(
                    metadata,
                    request.Offset,
                    cancellationToken).ConfigureAwait(false);
                return new AgentAttachmentTransferResult(
                    NextOffset: request.Offset + chunk.Content.Length,
                    TotalBytes: chunk.TotalBytes,
                    Content: chunk.Content,
                    IsComplete: chunk.IsComplete);
            default:
                throw new InvalidOperationException("Unknown attachment transfer operation.");
        }
    }

    private static string RequireTransferId(AgentAttachmentTransferRequest request)
        => string.IsNullOrWhiteSpace(request.TransferId)
            ? throw new InvalidOperationException("Attachment transfer id is required.")
            : request.TransferId;
}
