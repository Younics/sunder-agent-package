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
        sessions.TurnChanged += OnTurnChanged;
        sessions.TranscriptReset += OnTranscriptReset;
        sessions.RunActivityChanged += OnRunActivityChanged;
    }

    public long Revision => Interlocked.Read(ref _revision);

    public void NotifyPermissionChanged(Guid? sessionId)
        => Publish(new AgentRuntimeChange(
            0, AgentRuntimeChangeKind.Permission, SessionId: sessionId));

    public async IAsyncEnumerable<AgentRuntimeChange> SubscribeAsync(
        AgentChangeSubscription request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriber = new Subscriber(Channel.CreateBounded<AgentRuntimeChange>(new BoundedChannelOptions(SubscriberCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        }));
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
                reset = new AgentRuntimeChange(revision, AgentRuntimeChangeKind.ResnapshotRequired);
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
                    yield return change;
                }
                yield return new AgentRuntimeChange(subscribedRevision, AgentRuntimeChangeKind.Connected);
            }
            await foreach (var change in subscriber.Channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
            if (subscriber.Overflowed)
            {
                yield return new AgentRuntimeChange(Revision, AgentRuntimeChangeKind.ResnapshotRequired);
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
            change = change with { Revision = Interlocked.Increment(ref _revision) };
            _replay.Enqueue(change);
            while (_replay.Count > ReplayCapacity)
            {
                _replay.Dequeue();
            }
            foreach (var subscriber in _subscribers.Values)
            {
                if (!subscriber.Channel.Writer.TryWrite(change))
                {
                    subscriber.Overflowed = true;
                    subscriber.Channel.Writer.TryComplete();
                }
            }
        }
    }

    public void Dispose()
    {
        _profiles.ProfileChanged -= OnProfileChanged;
        _profiles.SelectableCapabilitiesChanged -= OnCatalogChanged;
        _workspaces.WorkspacesChanged -= OnWorkspacesChanged;
        _sessions.SessionChanged -= OnSessionChanged;
        _sessions.TurnChanged -= OnTurnChanged;
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

    private sealed class Subscriber(Channel<AgentRuntimeChange> channel)
    {
        public Channel<AgentRuntimeChange> Channel { get; } = channel;
        public bool Overflowed { get; set; }
    }
}

internal sealed class AgentDashboardHandler(
    AgentLocalStoreAccessor store,
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentDashboardRequest, AgentDashboardProjection>
{
    public ValueTask<AgentDashboardProjection> HandleAsync(
        AgentDashboardRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = store.GetDashboardSnapshot();
        return ValueTask.FromResult(new AgentDashboardProjection(
            changes.Revision,
            snapshot.Profiles,
            store.ListWorkspaces(),
            store.ListWorkspaceBindings(),
            snapshot.Sessions,
            snapshot.RecentCheckpoints,
            snapshot.RecentMessages));
    }
}

// Keeps handlers on service APIs while allowing the dashboard to remain one aggregate read.
internal sealed class AgentLocalStoreAccessor(AgentLocalStore store)
{
    public AgentDashboardSnapshot GetDashboardSnapshot() => store.GetDashboardSnapshot();
    public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => store.ListWorkspaces();
    public IReadOnlyList<AgentWorkspaceBindingRecord> ListWorkspaceBindings()
        => store.ListWorkspaces().SelectMany(workspace => store.ListWorkspaceBindings(workspace.WorkspaceId)).ToArray();
}

internal sealed class AgentSessionPageHandler(
    AgentSessionService sessions,
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentSessionPageRequest, AgentSessionPage>
{
    public ValueTask<AgentSessionPage> HandleAsync(
        AgentSessionPageRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = request.SessionId is { } sessionId
            ? sessions.GetSession(sessionId) is { } session ? [session] : []
            : string.IsNullOrWhiteSpace(request.WorkspaceId)
                ? sessions.ListSessions()
                : sessions.ListSessionsForWorkspace(request.WorkspaceId);
        var offset = Math.Max(0, request.Offset);
        var limit = Math.Clamp(request.Limit, 1, 500);
        var page = source.Skip(offset).Take(limit)
            .Select(session => new AgentSessionSnapshot(session, sessions.GetLatestCheckpoint(session.SessionId)))
            .ToArray();
        return ValueTask.FromResult(new AgentSessionPage(
            changes.Revision, page, source.Count, offset + page.Length < source.Count));
    }
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
            AgentTranscriptPageDirection.Recent => sessions.ListRecentTurns(request.SessionId, limit + 1),
            AgentTranscriptPageDirection.Before when request.AnchorCreatedAtUtc is { } createdAt
                                                     && request.AnchorTurnId is { } turnId
                => sessions.ListTurnsBefore(request.SessionId, createdAt, turnId, limit + 1),
            AgentTranscriptPageDirection.After when request.AnchorCreatedAtUtc is { } createdAt
                                                    && request.AnchorTurnId is { } turnId
                => sessions.ListTurnsAfter(request.SessionId, createdAt, turnId, limit + 1),
            AgentTranscriptPageDirection.Turn when request.AnchorTurnId is { } turnId
                => sessions.GetTurn(turnId) is { } turn ? [turn] : [],
            _ => throw new InvalidOperationException("The transcript page anchor is invalid."),
        };
        var hasMore = turns.Count > limit;
        return ValueTask.FromResult(new AgentTranscriptPage(
            changes.Revision, hasMore ? turns.Take(limit).ToArray() : turns, hasMore));
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
                workspaces.DeleteWorkspace(Require(request.WorkspaceId));
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
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentSessionCommand, AgentSessionCommandResult>
{
    public ValueTask<AgentSessionCommandResult> HandleAsync(
        AgentSessionCommand request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AgentSessionRecord? session;
        switch (request.Kind)
        {
            case AgentSessionCommandKind.Create:
                session = sessions.CreateSession(request.Title ?? "New Session", profileId: request.ProfileId,
                    behaviorLoopId: request.BehaviorLoopId, workspaceId: request.WorkspaceId);
                break;
            case AgentSessionCommandKind.Update:
                session = request.Session ?? throw new InvalidOperationException("Session is required.");
                sessions.UpdateSession(session);
                break;
            case AgentSessionCommandKind.Delete:
                session = request.Session ?? throw new InvalidOperationException("Session is required.");
                sessions.DeleteSession(session.SessionId);
                session = null;
                break;
            default:
                throw new InvalidOperationException("Unknown session command.");
        }
        return ValueTask.FromResult(new AgentSessionCommandResult(
            changes.Revision,
            session is null ? null : new AgentSessionSnapshot(session, sessions.GetLatestCheckpoint(session.SessionId))));
    }
}

internal sealed class AgentRunCommandHandler(
    AgentRunCoordinator runs,
    AgentRuntimeChangeHub changes)
    : IPackageRuntimeOperationHandler<AgentRunCommand, AgentRunCommandResult>
{
    public async ValueTask<AgentRunCommandResult> HandleAsync(
        AgentRunCommand request, CancellationToken cancellationToken = default)
    {
        var checkpoint = request.Kind switch
        {
            AgentRunCommandKind.Start => await runs.QueueUserMessageAsync(request.SessionId,
                Require(request.ProfileId, "Profile id"), request.UserMessage ?? string.Empty,
                Require(request.WorkspaceId, "Workspace id"), request.Attachments ?? [], cancellationToken),
            AgentRunCommandKind.RollbackAndStart => await runs.RollbackAndQueueUserMessageAsync(request.SessionId,
                request.RollbackAnchorTurnId ?? throw new InvalidOperationException("Rollback anchor is required."),
                Require(request.ProfileId, "Profile id"), request.UserMessage ?? string.Empty,
                Require(request.WorkspaceId, "Workspace id"), request.Attachments ?? [], cancellationToken),
            AgentRunCommandKind.Stop => await ((IAgentRunGateway)runs).StopAsync(request.SessionId, cancellationToken),
            AgentRunCommandKind.ApprovePermission => await ((IAgentRunGateway)runs).ApprovePendingPermissionAsync(
                request.SessionId, Require(request.PermissionRequestId, "Permission request id"), cancellationToken),
            AgentRunCommandKind.DenyPermission => await ((IAgentRunGateway)runs).DenyPendingPermissionAsync(
                request.SessionId, Require(request.PermissionRequestId, "Permission request id"), cancellationToken),
            _ => throw new InvalidOperationException("Unknown run command."),
        };
        return new AgentRunCommandResult(changes.Revision, checkpoint);
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
        return ValueTask.FromResult(new AgentPermissionProjection(
            changes.Revision,
            sessionId is { } id ? permissions.GetSessionState(id) : null,
            permissions.ListActions(),
            permissions.ListOverrides(),
            sessionId is { } pendingId ? permissions.ListPendingRequestsForSessionTree(pendingId) : []));
    }

    private static Guid RequireSession(AgentPermissionCommand request)
        => request.SessionId ?? throw new InvalidOperationException("Session id is required.");
    private static string Require(string? value)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException("Permission identifier is required.") : value;
}

internal sealed class AgentAttachmentReadHandler(AgentAttachmentService attachments)
    : IPackageRuntimeOperationHandler<AgentAttachmentReadRequest, AgentAttachmentReadResult>
{
    public async ValueTask<AgentAttachmentReadResult> HandleAsync(
        AgentAttachmentReadRequest request, CancellationToken cancellationToken = default)
        => new(await attachments.ReadAttachmentBytesAsync(request.Metadata, cancellationToken).ConfigureAwait(false));
}
