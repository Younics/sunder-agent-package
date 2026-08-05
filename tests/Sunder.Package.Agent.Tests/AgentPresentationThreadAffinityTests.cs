extern alias AgentCore;

using System.Collections.Concurrent;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Xunit;
using AgentPresentationDispatcher = AgentCore::Sunder.Package.Agent.Shared.Presentation.IPresentationDispatcher;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentPresentationThreadAffinityTests
{
    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("save", false)]
    [InlineData("save", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    public async Task ProfileMutationsPublishBoundStateAndSiblingCommandsOnDispatcher(
        string mutation,
        bool fail)
    {
        using var dispatcher = new DedicatedTestDispatcher();
        var gateway = new DeferredProfileGateway(mutation, fail);
        using var viewModel = new AgentProfilesViewModel(
            gateway,
            settingsNavigationService: null,
            dispatcher);

        Task initialization = Task.CompletedTask;
        await dispatcher.InvokeAsync(() => initialization = viewModel.InitializeAsync());
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));

        var propertyThreads = new ConcurrentQueue<int>();
        var siblingCommandThreads = new ConcurrentQueue<int>();
        viewModel.PropertyChanged += (_, _) =>
            propertyThreads.Enqueue(Environment.CurrentManagedThreadId);
        var siblingCommand = mutation == "save"
            ? viewModel.DeleteProfileCommand
            : viewModel.SaveProfileCommand;
        siblingCommand.CanExecuteChanged += (_, _) =>
            siblingCommandThreads.Enqueue(Environment.CurrentManagedThreadId);

        Task operation = Task.CompletedTask;
        await dispatcher.InvokeAsync(() =>
        {
            if (mutation == "save")
            {
                viewModel.DisplayName = "Updated Profile";
            }

            operation = mutation switch
            {
                "create" => viewModel.CreateProfileCommand.ExecuteAsync(null),
                "save" => viewModel.SaveProfileCommand.ExecuteAsync(null),
                "delete" => viewModel.DeleteProfileCommand.ExecuteAsync(null),
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            };
        });
        await gateway.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        gateway.ReleaseMutation.TrySetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.WaitForIdleAsync();

        Assert.NotEmpty(propertyThreads);
        Assert.NotEmpty(siblingCommandThreads);
        Assert.All(propertyThreads, threadId => Assert.Equal(dispatcher.ThreadId, threadId));
        Assert.All(siblingCommandThreads, threadId => Assert.Equal(dispatcher.ThreadId, threadId));
        if (fail)
        {
            Assert.Equal(AgentProfileStatusKind.Error, viewModel.StatusKind);
            Assert.Equal($"Injected {mutation} failure.", viewModel.StatusText);
        }
        else
        {
            Assert.False(viewModel.IsBusy);
            Assert.NotEqual(AgentProfileStatusKind.Error, viewModel.StatusKind);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeferredRollbackPublishesSuccessAndFallbackOnDispatcher(bool turnIsAvailable)
    {
        using var dispatcher = new DedicatedTestDispatcher();
        var profileGateway = new StaticProfileGateway();
        var workspaceGateway = new StaticWorkspaceGateway();
        var sessionGateway = new DeferredTranscriptGateway(turnIsAvailable);
        var permissionGateway = new FallbackPermissionGateway();
        using var viewModel = new AgentChatViewModel(
            profileGateway,
            workspaceGateway,
            sessionGateway,
            permissionGateway,
            NoOpRunGateway.Instance,
            dispatcher);

        Task initialization = Task.CompletedTask;
        await dispatcher.InvokeAsync(() => initialization = viewModel.InitializeAsync());
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));

        AgentTextTranscriptRowViewModel? row = null;
        AgentSessionListItemViewModel? selectedSession = null;
        await dispatcher.InvokeAsync(() =>
        {
            row = Assert.Single(viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>());
            selectedSession = Assert.IsType<AgentSessionListItemViewModel>(viewModel.SelectedSession);
        });
        var propertyThreads = new ConcurrentQueue<int>();
        var sessionPropertyThreads = new ConcurrentQueue<int>();
        var siblingCommandThreads = new ConcurrentQueue<int>();
        viewModel.PropertyChanged += (_, _) =>
            propertyThreads.Enqueue(Environment.CurrentManagedThreadId);
        selectedSession!.PropertyChanged += (_, _) =>
            sessionPropertyThreads.Enqueue(Environment.CurrentManagedThreadId);
        viewModel.ClearComposerCommand.CanExecuteChanged += (_, _) =>
            siblingCommandThreads.Enqueue(Environment.CurrentManagedThreadId);

        Task rollback = Task.CompletedTask;
        await dispatcher.InvokeAsync(() =>
            rollback = viewModel.StartRollbackFromMessageCommand.ExecuteAsync(row));
        await sessionGateway.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        sessionGateway.ReleaseLoad.TrySetResult();
        await rollback.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.WaitForIdleAsync();

        Assert.NotEmpty(propertyThreads);
        Assert.NotEmpty(sessionPropertyThreads);
        Assert.All(propertyThreads, threadId => Assert.Equal(dispatcher.ThreadId, threadId));
        Assert.All(sessionPropertyThreads, threadId => Assert.Equal(dispatcher.ThreadId, threadId));
        if (turnIsAvailable)
        {
            Assert.NotEmpty(siblingCommandThreads);
            Assert.All(siblingCommandThreads, threadId => Assert.Equal(dispatcher.ThreadId, threadId));
            Assert.Equal(sessionGateway.Turn.TurnId, viewModel.PendingRollbackTurnId);
            Assert.Equal("Original message", viewModel.DraftMessage);
        }
        else
        {
            Assert.Contains("no longer available", viewModel.StatusText, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PermissionFallbackReloadsCollectionOnDispatcher(bool approve)
    {
        using var dispatcher = new DedicatedTestDispatcher();
        var request = CreatePermissionRequest();
        var permissionGateway = new FallbackPermissionGateway(request);
        var runGateway = new DeferredPermissionRunGateway(permissionGateway);
        var state = new AgentPermissionPanelState(permissionGateway, runGateway, dispatcher);
        await dispatcher.InvokeAsync(() =>
            state.ApplySnapshot(request.SessionId, new AgentSessionPermissionState(request.SessionId, false), [request]));
        var collectionThreads = new ConcurrentQueue<int>();
        state.Requests.CollectionChanged += (_, _) =>
            collectionThreads.Enqueue(Environment.CurrentManagedThreadId);

        Task operation = Task.CompletedTask;
        await dispatcher.InvokeAsync(() => operation = approve
            ? state.ApproveAsync(request, approveForSession: false)
            : state.DenyAsync(request));
        await runGateway.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        runGateway.ReleaseMutation.TrySetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.WaitForIdleAsync();

        Assert.Empty(state.Requests);
        Assert.NotEmpty(collectionThreads);
        Assert.All(collectionThreads, threadId => Assert.Equal(dispatcher.ThreadId, threadId));
    }

    [Fact]
    public async Task ProfileChangeCapturesChatSelectionsOnDispatcher()
    {
        using var dispatcher = new DedicatedTestDispatcher();
        var profileGateway = new StaticProfileGateway();
        using var viewModel = new AgentChatViewModel(
            profileGateway,
            new StaticWorkspaceGateway(),
            new DeferredTranscriptGateway(turnIsAvailable: true),
            new FallbackPermissionGateway(),
            NoOpRunGateway.Instance,
            dispatcher);

        Task initialization = Task.CompletedTask;
        await dispatcher.InvokeAsync(() => initialization = viewModel.InitializeAsync());
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        profileGateway.TrackListThreads = true;

        await Task.Run(profileGateway.RaiseProfileChanged);
        await dispatcher.WaitForIdleAsync();

        Assert.NotEmpty(profileGateway.ListThreads);
        Assert.All(profileGateway.ListThreads, threadId => Assert.Equal(dispatcher.ThreadId, threadId));
    }

    private static AgentPendingPermissionRequestRecord CreatePermissionRequest()
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentPendingPermissionRequestRecord(
            "request-1",
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "profile-1",
            Guid.NewGuid(),
            "Approve this action.",
            "call-1",
            "action-1",
            "boundary-1",
            "Permission summary",
            "tool-1",
            "{}",
            null,
            null,
            "workspace-1",
            null,
            null,
            null,
            IsMutation: true,
            now);
    }

    private static AgentProfileRecord CreateProfile(string id = "profile-1", string name = "Profile")
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentProfileRecord(id, name, null, null, null, null, null, null, now, now);
    }

    private sealed class DeferredProfileGateway(
        string deferredMutation,
        bool failMutation) :
        IAgentProfileGateway,
        IAgentDashboardLoader,
        IAgentProfileCommandGateway
    {
        private readonly object _syncRoot = new();
        private AgentProfileRecord? _profile = CreateProfile();

        public TaskCompletionSource MutationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseMutation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<string>? ProfileChanged
        {
            add { }
            remove { }
        }

        public event Action? SelectableCapabilitiesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AgentProfileRecord> ListProfiles()
        {
            lock (_syncRoot)
            {
                return _profile is null ? [] : [_profile];
            }
        }

        public Task<AgentDashboardProjection> LoadDashboardAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentDashboardProjection(0, ListProfiles(), [], []));
        }

        public AgentProfileRecord? GetProfile(string profileId)
        {
            lock (_syncRoot)
            {
                return string.Equals(_profile?.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)
                    ? _profile
                    : null;
            }
        }

        public AgentProfileModelBindingRecord? GetChatBinding(string profileId) => null;

        public async Task<AgentProfileRecord> CreateProfileAsync(
            string displayName,
            CancellationToken cancellationToken = default)
        {
            await AwaitMutationAsync("create", cancellationToken).ConfigureAwait(false);
            var profile = CreateProfile("profile-created", displayName);
            lock (_syncRoot)
            {
                _profile = profile;
            }
            return profile;
        }

        public void SaveProfile(
            string profileId,
            string displayName,
            string? description,
            string? instructions,
            string? chatProviderId,
            string? chatModelId,
            string? embeddingProviderId,
            string? embeddingModelId,
            IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? selectableCapabilityAssignments = null,
            string? behaviorLoopId = null,
            string? behaviorLoopSourceId = null,
            string? behaviorLoopSettingsJson = null,
            string? chatModelSettingsJson = null)
        {
            lock (_syncRoot)
            {
                _profile = (_profile ?? throw new InvalidOperationException("Profile missing.")) with
                {
                    DisplayName = displayName,
                    Description = description,
                    Instructions = instructions,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
            }
        }

        public async Task SaveProfileAsync(
            string profileId,
            string displayName,
            string? description,
            string? instructions,
            string? chatProviderId,
            string? chatModelId,
            string? embeddingProviderId,
            string? embeddingModelId,
            IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? selectableCapabilityAssignments = null,
            string? behaviorLoopId = null,
            string? behaviorLoopSourceId = null,
            string? behaviorLoopSettingsJson = null,
            string? chatModelSettingsJson = null,
            CancellationToken cancellationToken = default)
        {
            await AwaitMutationAsync("save", cancellationToken).ConfigureAwait(false);
            SaveProfile(
                profileId,
                displayName,
                description,
                instructions,
                chatProviderId,
                chatModelId,
                embeddingProviderId,
                embeddingModelId,
                selectableCapabilityAssignments,
                behaviorLoopId,
                behaviorLoopSourceId,
                behaviorLoopSettingsJson,
                chatModelSettingsJson);
        }

        public void DeleteProfile(string profileId)
        {
            lock (_syncRoot)
            {
                _profile = null;
            }
        }

        public async Task DeleteProfileAsync(
            string profileId,
            CancellationToken cancellationToken = default)
        {
            await AwaitMutationAsync("delete", cancellationToken).ConfigureAwait(false);
            DeleteProfile(profileId);
        }

        public IReadOnlyList<AgentBehaviorLoopDescriptor> ListBehaviorLoopDescriptors() => [];
        public IReadOnlyList<AgentProviderDescriptor> ListChatProviderDescriptors() => [];
        public IReadOnlyList<AgentEmbeddingProviderDescriptor> ListEmbeddingProviderDescriptors() => [];
        public bool HasProfileCapabilityConsumers(string capabilityKind) => false;

        public Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>
            ListSelectableProfileCapabilitiesAsync(
                AgentProfileRecord? profile = null,
                CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);

        public Task<IReadOnlyList<AgentToolCatalogEntry>> ListInstalledLocalToolsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentToolCatalogEntry>>([]);

        public Task<IReadOnlyList<AgentModelDescriptor>> ListChatModelsAsync(
            string? providerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelDescriptor>>([]);

        public Task<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListEmbeddingModelsAsync(
            string? providerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([]);

        public Task<AgentProviderReadiness?> GetChatProviderReadinessAsync(
            string? providerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentProviderReadiness?>(null);

        public Task<AgentEmbeddingProviderReadiness?> GetEmbeddingProviderReadinessAsync(
            string? providerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentEmbeddingProviderReadiness?>(null);

        private async Task AwaitMutationAsync(string mutation, CancellationToken cancellationToken)
        {
            Assert.Equal(deferredMutation, mutation);
            MutationStarted.TrySetResult();
            await ReleaseMutation.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (failMutation)
            {
                throw new InvalidOperationException($"Injected {mutation} failure.");
            }
        }
    }

    private sealed class StaticProfileGateway : IAgentProfileGateway
    {
        private readonly AgentProfileRecord _profile = CreateProfile();
        private int _trackListThreads;

        public event Action<string>? ProfileChanged;

        public event Action? SelectableCapabilitiesChanged
        {
            add { }
            remove { }
        }

        public bool TrackListThreads
        {
            get => Volatile.Read(ref _trackListThreads) != 0;
            set => Volatile.Write(ref _trackListThreads, value ? 1 : 0);
        }

        public ConcurrentQueue<int> ListThreads { get; } = new();

        public IReadOnlyList<AgentProfileRecord> ListProfiles()
        {
            if (TrackListThreads)
            {
                ListThreads.Enqueue(Environment.CurrentManagedThreadId);
            }
            return [_profile];
        }

        public void RaiseProfileChanged() => ProfileChanged?.Invoke(_profile.ProfileId);

        public AgentProfileRecord? GetProfile(string profileId) => _profile;
        public AgentProfileModelBindingRecord? GetChatBinding(string profileId) => null;
        public Task<AgentProfileRecord> CreateProfileAsync(string displayName, CancellationToken cancellationToken = default)
            => Task.FromResult(_profile);
        public void SaveProfile(string profileId, string displayName, string? description, string? instructions,
            string? chatProviderId, string? chatModelId, string? embeddingProviderId, string? embeddingModelId,
            IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? selectableCapabilityAssignments = null,
            string? behaviorLoopId = null, string? behaviorLoopSourceId = null,
            string? behaviorLoopSettingsJson = null, string? chatModelSettingsJson = null) { }
        public void DeleteProfile(string profileId) { }
        public IReadOnlyList<AgentBehaviorLoopDescriptor> ListBehaviorLoopDescriptors() => [];
        public IReadOnlyList<AgentProviderDescriptor> ListChatProviderDescriptors() => [];
        public IReadOnlyList<AgentEmbeddingProviderDescriptor> ListEmbeddingProviderDescriptors() => [];
        public bool HasProfileCapabilityConsumers(string capabilityKind) => false;
        public Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListSelectableProfileCapabilitiesAsync(
            AgentProfileRecord? profile = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);
        public Task<IReadOnlyList<AgentToolCatalogEntry>> ListInstalledLocalToolsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentToolCatalogEntry>>([]);
        public Task<IReadOnlyList<AgentModelDescriptor>> ListChatModelsAsync(
            string? providerId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentModelDescriptor>>([]);
        public Task<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListEmbeddingModelsAsync(
            string? providerId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([]);
        public Task<AgentProviderReadiness?> GetChatProviderReadinessAsync(
            string? providerId, CancellationToken cancellationToken = default)
            => Task.FromResult<AgentProviderReadiness?>(null);
        public Task<AgentEmbeddingProviderReadiness?> GetEmbeddingProviderReadinessAsync(
            string? providerId, CancellationToken cancellationToken = default)
            => Task.FromResult<AgentEmbeddingProviderReadiness?>(null);
    }

    private sealed class StaticWorkspaceGateway : IAgentWorkspaceGateway
    {
        private readonly AgentWorkspaceRecord _workspace = new(
            "workspace-1",
            "Workspace",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        public event Action? WorkspacesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [_workspace];
        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => _workspace;
        public AgentWorkspaceRecord CreateWorkspace(string displayName) => throw new NotSupportedException();
        public void SaveWorkspace(string workspaceId, string displayName, string? description) =>
            throw new NotSupportedException();
        public void SaveWorkspaceAggregate(string workspaceId, string displayName, string? description,
            IReadOnlyList<AgentWorkspacePathRecord> paths, IReadOnlyList<AgentWorkspaceDocumentRecord> documents,
            string? executionTargetId) => throw new NotSupportedException();
        public void DeleteWorkspace(string workspaceId) => throw new NotSupportedException();
        public IReadOnlyList<AgentWorkspaceBindingRecord> ListBindings(string workspaceId) => [];
        public AgentWorkspaceBindingRecord SavePrimaryExecutionBinding(string workspaceId, string contributionId,
            string displayRole = AgentWorkspaceBindingRoles.PrimaryExecutionTarget) => throw new NotSupportedException();
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DeferredTranscriptGateway : IAgentSessionGateway, IAgentTranscriptPageGateway
    {
        private readonly bool _turnIsAvailable;
        private readonly AgentSessionRecord _session;

        public DeferredTranscriptGateway(bool turnIsAvailable)
        {
            _turnIsAvailable = turnIsAvailable;
            var now = DateTimeOffset.UtcNow;
            var sessionId = Guid.NewGuid();
            var turnId = Guid.NewGuid();
            _session = new AgentSessionRecord(
                sessionId,
                "Session",
                AgentSessionState.Active,
                now,
                now,
                RootSessionId: sessionId,
                ProfileId: "profile-1",
                WorkspaceId: "workspace-1");
            Turn = new AgentTurnRecord(
                turnId,
                sessionId,
                AgentMessageRole.User,
                AgentTurnKind.Message,
                [new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    turnId,
                    0,
                    AgentTurnItemKind.Text,
                    "Original message",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    false,
                    null,
                    null)],
                now,
                now);
        }

        public AgentTurnRecord Turn { get; }
        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLoad { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<Guid>? SessionChanged { add { } remove { } }
        public event Action<Guid, AgentTurnRecord>? TurnChanged { add { } remove { } }
        public event Action<Guid>? TranscriptReset { add { } remove { } }
        public event Action<Guid, AgentRunActivityUpdate>? RunActivityChanged { add { } remove { } }

        public IReadOnlyList<AgentSessionRecord> ListSessions() => [_session];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId) => [_session];
        public AgentSessionRecord CreateSession(string title, Guid? parentSessionId = null, Guid? rootSessionId = null,
            Guid? parentRunId = null, long? parentRunRevision = null, string? parentToolCallId = null,
            string? taskId = null, string? profileId = null, string? behaviorLoopId = null,
            string? agentKind = null, string? workspaceId = null) => throw new NotSupportedException();
        public AgentSessionRecord? GetSession(Guid sessionId) => sessionId == _session.SessionId ? _session : null;
        public void UpdateSession(AgentSessionRecord session) => throw new NotSupportedException();
        public void DeleteSession(Guid sessionId) => throw new NotSupportedException();
        public IReadOnlyList<AgentTurnRecord> ListTurns(Guid sessionId) => [Turn];
        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [Turn];
        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(
            Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(
            Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit) => [];
        public AgentTurnRecord? GetTurn(Guid turnId) => turnId == Turn.TurnId ? Turn : null;
        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;

        public async Task<AgentTranscriptPage> LoadTranscriptPageAsync(
            AgentTranscriptPageRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Direction != AgentTranscriptPageDirection.Turn)
            {
                return new AgentTranscriptPage(0, [Turn], false);
            }

            LoadStarted.TrySetResult();
            await ReleaseLoad.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new AgentTranscriptPage(0, _turnIsAvailable ? [Turn] : [], false);
        }
    }

    private sealed class FallbackPermissionGateway(params AgentPendingPermissionRequestRecord[] requests) :
        IAgentPermissionGateway
    {
        private readonly object _syncRoot = new();
        private readonly List<AgentPendingPermissionRequestRecord> _requests = [.. requests];

        public AgentSessionPermissionState GetSessionState(Guid sessionId) => new(sessionId, false);
        public void SetSessionUnrestrictedMode(Guid sessionId, bool isEnabled) { }
        public IReadOnlyList<AgentPermissionActionDescriptor> ListActions() => [];
        public IReadOnlyList<AgentPermissionOverride> ListOverrides() => [];
        public void SaveOverride(string actionId, string boundaryId, AgentPermissionDecision decision) { }
        public void DeleteOverride(string actionId, string boundaryId) { }

        public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequestsForSessionTree(Guid sessionId)
        {
            lock (_syncRoot)
            {
                return _requests.Where(request => request.SessionId == sessionId).ToArray();
            }
        }

        public void Remove(string requestId)
        {
            lock (_syncRoot)
            {
                _requests.RemoveAll(request => string.Equals(request.RequestId, requestId, StringComparison.Ordinal));
            }
        }
    }

    private sealed class DeferredPermissionRunGateway(FallbackPermissionGateway permissions) :
        IAgentRunGateway,
        IAgentChatRunGateway
    {
        public TaskCompletionSource MutationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMutation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(Guid sessionId, string profileId,
            string userMessage, string workspaceId, IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(Guid sessionId,
            Guid rollbackAnchorTurnId, string profileId, string userMessage, string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentRunCheckpointRecord?> StopAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(Guid sessionId, string requestId,
            CancellationToken cancellationToken = default)
            => CompleteAsync(requestId, cancellationToken);
        public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(Guid sessionId, string requestId,
            bool approveForSession, CancellationToken cancellationToken = default)
            => CompleteAsync(requestId, cancellationToken);
        public Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(Guid sessionId, string requestId,
            CancellationToken cancellationToken = default)
            => CompleteAsync(requestId, cancellationToken);

        private async Task<AgentRunCheckpointRecord?> CompleteAsync(
            string requestId,
            CancellationToken cancellationToken)
        {
            MutationStarted.TrySetResult();
            await ReleaseMutation.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            permissions.Remove(requestId);
            return null;
        }
    }

    private sealed class NoOpRunGateway : IAgentRunGateway, IAgentChatRunGateway
    {
        public static NoOpRunGateway Instance { get; } = new();

        public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(Guid sessionId, string profileId,
            string userMessage, string workspaceId, IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(Guid sessionId,
            Guid rollbackAnchorTurnId, string profileId, string userMessage, string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentRunCheckpointRecord?> StopAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => Task.FromResult<AgentRunCheckpointRecord?>(null);
        public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(Guid sessionId, string requestId,
            CancellationToken cancellationToken = default) => Task.FromResult<AgentRunCheckpointRecord?>(null);
        public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(Guid sessionId, string requestId,
            bool approveForSession, CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);
        public Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(Guid sessionId, string requestId,
            CancellationToken cancellationToken = default) => Task.FromResult<AgentRunCheckpointRecord?>(null);
    }

    private sealed class DedicatedTestDispatcher : AgentPresentationDispatcher, IDisposable
    {
        private readonly BlockingCollection<WorkItem> _queue = new();
        private readonly Thread _thread;
        private readonly TaskCompletionSource<int> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DedicatedTestDispatcher()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Agent presentation affinity test dispatcher",
            };
            _thread.Start();
            ThreadId = _started.Task.GetAwaiter().GetResult();
        }

        public int ThreadId { get; }
        public bool CheckAccess() => Environment.CurrentManagedThreadId == ThreadId;

        public Task InvokeAsync(Action action)
        {
            if (CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(new WorkItem(action, completion));
            return completion.Task;
        }

        public Task WaitForIdleAsync() => InvokeAsync(static () => { });

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }

        private void Run()
        {
            _started.SetResult(Environment.CurrentManagedThreadId);
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    item.Action();
                    item.Completion.SetResult();
                }
                catch (Exception ex)
                {
                    item.Completion.SetException(ex);
                }
            }
        }

        private sealed record WorkItem(Action Action, TaskCompletionSource Completion);
    }
}
