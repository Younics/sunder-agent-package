using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Runtime;

internal sealed class AgentRuntimeChangeHub : IDisposable
{
    private const int ReplayCapacity = 256;
    private const int SubscriberCapacity = 64;
    private readonly AgentProfileService _profiles;
    private readonly AgentWorkspaceService _workspaces;
    private readonly AgentSessionService _sessions;
    private readonly AgentRuntimeTransportBudgetState _transportBudgets;
    private readonly object _gate = new();
    private readonly Dictionary<long, Subscriber> _subscribers = [];
    private readonly Queue<QueuedChange> _replay = new(ReplayCapacity);
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private long _replayBytes;
    private long _revision;
    private long _subscriberId;

    public AgentRuntimeChangeHub(
        AgentProfileService profiles,
        AgentWorkspaceService workspaces,
        AgentSessionService sessions,
        AgentRuntimeTransportBudgetState? transportBudgets = null)
    {
        _profiles = profiles;
        _workspaces = workspaces;
        _sessions = sessions;
        _transportBudgets = transportBudgets ?? new AgentRuntimeTransportBudgetState();
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
            Channel.CreateBounded<QueuedChange>(new BoundedChannelOptions(SubscriberCapacity)
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
            var oldestRevision = _replay.Count == 0 ? revision + 1 : _replay.Peek().Change.Revision;
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
                replay = _replay
                    .Select(static item => item.Change)
                    .Where(change => change.Revision > request.AfterRevision)
                    .ToArray();
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
            await foreach (var queued in subscriber.Channel.Reader.ReadAllAsync(cancellationToken))
            {
                subscriber.Release(queued.Bytes);
                yield return queued.Change;
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
                : new AgentSessionSnapshot(session, _sessions.GetLatestCheckpoint(sessionId), _sessions.GetRunActivity(sessionId))));

    }

    internal AgentRunActivityUpdate? GetRunActivity(Guid sessionId) => _sessions.GetRunActivity(sessionId);

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
            var profile = _transportBudgets.Current;
            var maximumEventBytes = AgentRuntimePayloadLimits.GetEventPayloadBytes(profile);
            change = AgentRuntimePayloadLimits.ReplaceWithResnapshotIfOversized(
                change,
                maximumEventBytes,
                ProjectForSubscriber(change, supportsTurnMutations: true),
                ProjectForSubscriber(change, supportsTurnMutations: false));
            var changeBytes = AgentRuntimePayloadLimits.GetSerializedByteCount(change);
            _replay.Enqueue(new QueuedChange(change, changeBytes));
            _replayBytes += changeBytes;
            var maximumReplayBytes = AgentRuntimePayloadLimits.GetReplayQueueBytes(profile);
            while (_replay.Count > ReplayCapacity || _replayBytes > maximumReplayBytes)
            {
                _replayBytes -= _replay.Dequeue().Bytes;
            }
            foreach (var subscriber in _subscribers.Values)
            {
                var projected = ProjectForSubscriber(change, subscriber.SupportsTurnMutations);
                var bytes = AgentRuntimePayloadLimits.GetSerializedByteCount(projected);
                var reserved = subscriber.TryReserve(
                    bytes,
                    AgentRuntimePayloadLimits.GetStreamQueueBytes(profile));
                if (!reserved || !subscriber.Channel.Writer.TryWrite(new QueuedChange(projected, bytes)))
                {
                    if (reserved)
                    {
                        subscriber.Release(bytes);
                    }
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
        Channel<QueuedChange> channel,
        bool supportsTurnMutations)
    {
        private long _pendingBytes;

        public Channel<QueuedChange> Channel { get; } = channel;
        public bool SupportsTurnMutations { get; } = supportsTurnMutations;
        public bool Overflowed { get; set; }

        public bool TryReserve(int bytes, long maximumBytes)
        {
            while (true)
            {
                var pending = Volatile.Read(ref _pendingBytes);
                if (bytes > maximumBytes - pending)
                {
                    return false;
                }
                if (Interlocked.CompareExchange(ref _pendingBytes, pending + bytes, pending) == pending)
                {
                    return true;
                }
            }
        }

        public void Release(int bytes)
            => Interlocked.Add(ref _pendingBytes, -bytes);
    }

    private readonly record struct QueuedChange(AgentRuntimeChange Change, int Bytes);
}

internal sealed class AgentDashboardHandler(
    AgentProfileService profiles,
    AgentWorkspaceService workspaces,
    AgentRuntimeChangeHub changes)
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
    AgentRuntimeChangeHub changes,
    AgentRuntimeTransportBudgetState? transportBudgets = null)
{
    private readonly AgentRuntimeTransportBudgetState _transportBudgets =
        transportBudgets ?? new AgentRuntimeTransportBudgetState();
    private readonly object _cacheGate = new();
    private ChatSnapshotCacheEntry? _cache;

    public async ValueTask<AgentChatSnapshotProjection> HandleAsync(
        AgentChatSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        var revision = changes.Revision;
        var maximumBytes = AgentRuntimePayloadLimits.GetResponsePayloadBytes(_transportBudgets.Current);
        var key = new ChatSnapshotCacheKey(revision, request, maximumBytes);
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
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
        snapshot = snapshot with { RuntimeInstanceId = changes.InstanceId };
        var sessionItems = snapshot.WorkspaceSessions.Select(item => item with
        {
            Activity = changes.GetRunActivity(item.Session.SessionId),
        }).ToArray();
        snapshot = snapshot with
        {
            WorkspaceSessions = sessionItems,
            SelectedSession = snapshot.SelectedSession is { } selected
                ? sessionItems.FirstOrDefault(item => item.Session.SessionId == selected.Session.SessionId) ?? selected : null,
        };
        snapshot = AgentChatSnapshotPayload.Fit(snapshot, maximumBytes);
        if (changes.Revision == revision)
        {
            lock (_cacheGate)
            {
                _cache = new ChatSnapshotCacheEntry(key, snapshot);
            }
        }
        return snapshot;
    }

    private readonly record struct ChatSnapshotCacheKey(
        long Revision,
        AgentChatSnapshotRequest Request,
        int MaximumBytes);

    private sealed record ChatSnapshotCacheEntry(
        ChatSnapshotCacheKey Key,
        AgentChatSnapshotProjection Snapshot);
}

internal sealed partial class AgentTranscriptPageHandler(
    AgentSessionService sessions,
    AgentRuntimeChangeHub changes,
    AgentRuntimeTransportBudgetState? transportBudgets = null)
{
    private readonly AgentRuntimeTransportBudgetState _transportBudgets =
        transportBudgets ?? new AgentRuntimeTransportBudgetState();

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
            _ => throw new AgentOwnerOperationException("The transcript page anchor is invalid."),
        };
        var hasMore = turns.Count > limit;
        var pageTurns = !hasMore
            ? turns
            : request.Direction is AgentTranscriptPageDirection.Recent or AgentTranscriptPageDirection.Before
                ? turns.Skip(turns.Count - limit).ToArray()
                : turns.Take(limit).ToArray();
        return ValueTask.FromResult(AgentRuntimePayloadLimits.FitTranscriptPage(
            new AgentTranscriptPage(changes.Revision, pageTurns, hasMore),
            request.Direction,
            maximumBytes: AgentRuntimePayloadLimits.GetResponsePayloadBytes(_transportBudgets.Current)));
    }
}

internal sealed class AgentTranscriptToolDetailHandler(
    AgentSessionService sessions,
    AgentRuntimeTransportBudgetState? transportBudgets = null)
{
    private readonly AgentRuntimeTransportBudgetState _transportBudgets =
        transportBudgets ?? new AgentRuntimeTransportBudgetState();

    public ValueTask<AgentTranscriptToolDetailResult> HandleAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var detail = sessions.GetTranscriptToolDetail(request);
        return ValueTask.FromResult(new AgentTranscriptToolDetailResult(
            detail is not null,
            detail is null
                ? null
                : AgentRuntimePayloadLimits.FitToolDetail(
                    detail,
                    AgentRuntimePayloadLimits.GetResponsePayloadBytes(_transportBudgets.Current))));
    }
}

internal sealed class AgentCatalogHandler(
    AgentProfileService profiles,
    AgentExecutionTargetService executionTargets,
    AgentRuntimeChangeHub changes)
{
    public async ValueTask<AgentCatalogProjection> HandleAsync(
        AgentCatalogRequest request, CancellationToken cancellationToken = default)
    {
        var toolsTask = profiles.ListInstalledLocalToolsAsync(cancellationToken);
        var capabilitiesTask = profiles.ListSelectableProfileCapabilitiesAsync(request.Profile, cancellationToken);
        var chatTask = profiles.GetChatProviderCatalogAsync(request.ChatProviderId, cancellationToken);
        var embeddingTask = profiles.GetEmbeddingProviderCatalogAsync(request.EmbeddingProviderId, cancellationToken);
        var behaviorLoopsTask = profiles.ListBehaviorLoopDescriptorsAsync(cancellationToken);
        var embeddingConsumersTask = profiles.HasProfileCapabilityConsumersAsync(AgentModelCapabilityKinds.Embedding, cancellationToken);
        var executionTargetsTask = executionTargets.ListTargetsAsync(cancellationToken);
        await Task.WhenAll(toolsTask, capabilitiesTask, chatTask, embeddingTask,
            behaviorLoopsTask, embeddingConsumersTask, executionTargetsTask).ConfigureAwait(false);
        var chat = await chatTask.ConfigureAwait(false);
        var embedding = await embeddingTask.ConfigureAwait(false);
        return new AgentCatalogProjection(
            changes.Revision,
            chat.Providers,
            embedding.Providers,
            await behaviorLoopsTask.ConfigureAwait(false),
            await executionTargetsTask.ConfigureAwait(false),
            await toolsTask.ConfigureAwait(false),
            await capabilitiesTask.ConfigureAwait(false),
            await embeddingConsumersTask.ConfigureAwait(false),
            chat.Models,
            embedding.Models,
            chat.Readiness,
            embedding.Readiness);
    }
}

internal sealed class AgentProfileCommandHandler(
    AgentProfileService profiles,
    AgentRuntimeChangeHub changes)
{
    public async ValueTask<AgentProfileCommandResult> HandleAsync(
        AgentProfileCommand request, CancellationToken cancellationToken = default)
    {
        try
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
                    throw new AgentOwnerOperationException("Unknown profile command.");
            }
            return new AgentProfileCommandResult(changes.Revision, profile);
        }
        catch (AgentProfileOperationException exception)
        {
            throw new AgentOwnerOperationException(exception.Message, exception);
        }
    }

    private static string Require(string? value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw new AgentOwnerOperationException($"{name} is required.") : value;
}

internal sealed class AgentWorkspaceCommandHandler(
    AgentWorkspaceService workspaces,
    AgentExecutionTargetWarmupService warmup,
    AgentSessionDeletionService deletion,
    AgentSessionDeletionFence deletionFence,
    AgentRuntimeChangeHub changes)
{
    public async ValueTask<AgentWorkspaceCommandResult> HandleAsync(
        AgentWorkspaceCommand request, CancellationToken cancellationToken = default)
    {
        try
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
                    ThrowIfWorkspaceFenced(workspaceId);
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
                    var warmupWorkspaceId = Require(request.WorkspaceId);
                    ThrowIfWorkspaceFenced(warmupWorkspaceId);
                    workspace = workspaces.GetWorkspace(warmupWorkspaceId)
                        ?? throw new AgentOwnerOperationException("Workspace was not found.");
                    result = await warmup.WarmWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new AgentOwnerOperationException("Unknown workspace command.");
            }
            return new AgentWorkspaceCommandResult(changes.Revision, workspace, result);
        }
        catch (AgentWorkspaceOperationException exception)
        {
            throw new AgentOwnerOperationException(exception.Message, exception);
        }
    }

    private void ThrowIfWorkspaceFenced(string workspaceId)
    {
        if (deletionFence.IsWorkspaceFenced(workspaceId))
        {
            throw new AgentOwnerOperationException(
                "The workspace is being deleted and cannot be changed.");
        }
    }

    private static string Require(string? value)
        => string.IsNullOrWhiteSpace(value) ? throw new AgentOwnerOperationException("Workspace id is required.") : value;
}

internal sealed class AgentSessionCommandHandler(
    AgentSessionService sessions,
    AgentWorkspaceService workspaces,
    AgentSessionDeletionService deletion,
    AgentSessionDeletionFence deletionFence,
    AgentRuntimeChangeHub changes)
{
    public async ValueTask<AgentSessionCommandResult> HandleAsync(
        AgentSessionCommand request, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            AgentSessionRecord? session;
            switch (request.Kind)
            {
                case AgentSessionCommandKind.Create:
                    if (deletionFence.IsWorkspaceFenced(request.WorkspaceId))
                    {
                        throw new AgentOwnerOperationException("The workspace is being deleted and cannot create a session.");
                    }
                    EnsureWorkspaceExists(request.WorkspaceId);
                    session = sessions.CreateSession(request.Title ?? "New Session", profileId: request.ProfileId,
                        behaviorLoopId: request.BehaviorLoopId, workspaceId: request.WorkspaceId);
                    break;
                case AgentSessionCommandKind.Update:
                    session = request.Session ?? throw new AgentOwnerOperationException("Session is required.");
                    if (deletionFence.IsFenced(session))
                    {
                        throw new AgentOwnerOperationException("The session is being deleted and cannot be changed.");
                    }
                    EnsureWorkspaceExists(session.WorkspaceId);
                    sessions.UpdateSession(session);
                    break;
                case AgentSessionCommandKind.Delete:
                    session = request.Session ?? throw new AgentOwnerOperationException("Session is required.");
                    await deletion.DeleteSessionAsync(
                        session.SessionId,
                        cancellationToken).ConfigureAwait(false);
                    session = null;
                    break;
                default:
                    throw new AgentOwnerOperationException("Unknown session command.");
            }
            return new AgentSessionCommandResult(
                changes.Revision,
                session is null ? null : new AgentSessionSnapshot(session, sessions.GetLatestCheckpoint(session.SessionId)));
        }
        catch (AgentSessionOperationException exception)
        {
            throw new AgentOwnerOperationException(exception.Message, exception);
        }
    }

    private void EnsureWorkspaceExists(string? workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(workspaceId)
            && !string.Equals(
                workspaceId,
                AgentWorkspaceService.UnassignedSessionsWorkspaceId,
                StringComparison.OrdinalIgnoreCase)
            && workspaces.GetWorkspace(workspaceId.Trim()) is null)
        {
            throw new AgentOwnerOperationException("Workspace was not found.");
        }
    }
}

internal sealed class AgentRunCommandHandler(
    AgentRunCoordinator runs,
    AgentSessionService sessions,
    AgentAttachmentTransferService attachmentTransfers,
    AgentRuntimeChangeHub changes,
    AgentRuntimeTransportBudgetState? transportBudgets = null)
{
    private readonly ConcurrentDictionary<Guid, Guid> _pendingUserTurns = new();
    private readonly AgentRuntimeTransportBudgetState _transportBudgets = transportBudgets ?? new();

    public async ValueTask<AgentRunCommandResult> HandleAsync(
        AgentRunCommand request, CancellationToken cancellationToken = default)
    {
        if (request.Kind is AgentRunCommandKind.Start or AgentRunCommandKind.RollbackAndStart)
        {
            var userTurnId = request.UserTurnId ?? Guid.NewGuid();
            if (!_pendingUserTurns.TryAdd(userTurnId, request.SessionId))
            {
                throw new AgentOwnerOperationException("The correlated run command is already pending.");
            }
            try
            {
                Guid? rollbackAnchorTurnId = request.Kind == AgentRunCommandKind.RollbackAndStart
                    ? request.RollbackAnchorTurnId
                      ?? throw new AgentOwnerOperationException("Rollback anchor is required.")
                    : null;
                var admission = await AdmitTransferredUserTurnAsync(
                    request.SessionId,
                    Require(request.ProfileId, "Profile id"),
                    request.UserMessage ?? string.Empty,
                    Require(request.WorkspaceId, "Workspace id"),
                    request.AttachmentHandles ?? [],
                    userTurnId,
                    rollbackAnchorTurnId,
                    cancellationToken).ConfigureAwait(false);
                runs.SignalDispatcher();
                return new AgentRunCommandResult(
                    changes.Revision,
                    admission.Checkpoint,
                    admission.Run.Key.RunId,
                    userTurnId,
                    admission.UserTurn is { } userTurn
                        ? AgentRuntimePayloadLimits.FitTranscriptPage(
                            new AgentTranscriptPage(changes.Revision, [userTurn], false),
                            AgentTranscriptPageDirection.Turn,
                            maximumBytes: AgentRuntimePayloadLimits.GetResponsePayloadBytes(
                                _transportBudgets.Current) / 2).Turns.Single()
                        : null);
            }
            finally
            {
                _pendingUserTurns.TryRemove(userTurnId, out _);
            }
        }

        var checkpoint = request.Kind switch
        {
            AgentRunCommandKind.Stop => await ((IAgentRunGateway)runs).StopAsync(request.SessionId, cancellationToken),
            AgentRunCommandKind.ApprovePermission => await runs.AdmitPermissionDecisionAsync(
                request.SessionId,
                Require(request.PermissionRequestId, "Permission request id"),
                approve: true,
                request.ApproveForSession,
                cancellationToken),
            AgentRunCommandKind.DenyPermission => await runs.AdmitPermissionDecisionAsync(
                request.SessionId, Require(request.PermissionRequestId, "Permission request id"),
                approve: false, approveForSession: false, cancellationToken),
            _ => throw new AgentOwnerOperationException("Unknown run command."),
        };
        if (request.Kind is AgentRunCommandKind.ApprovePermission or AgentRunCommandKind.DenyPermission)
        {
            changes.NotifyPermissionChanged(request.SessionId);
        }
        return new AgentRunCommandResult(changes.Revision, checkpoint);
    }

    private async Task<AgentUserTurnAdmissionResult> AdmitTransferredUserTurnAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadHandle> attachmentHandles,
        Guid userTurnId,
        Guid? rollbackAnchorTurnId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await runs.AdmitTransferredUserTurnAsync(
                sessionId,
                profileId,
                userMessage,
                workspaceId,
                attachmentHandles,
                attachmentTransfers,
                userTurnId,
                rollbackAnchorTurnId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AgentUserTurnAdmissionException exception)
        {
            throw new AgentOwnerOperationException(exception.Message, exception);
        }
        catch (AgentUserTurnConflictException exception)
        {
            throw new AgentOwnerOperationException(exception.Message, exception);
        }
        catch (AgentAttachmentTransferOperationException exception)
        {
            throw new AgentOwnerOperationException(exception.Message, exception);
        }
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
        => string.IsNullOrWhiteSpace(value) ? throw new AgentOwnerOperationException($"{name} is required.") : value;
}
