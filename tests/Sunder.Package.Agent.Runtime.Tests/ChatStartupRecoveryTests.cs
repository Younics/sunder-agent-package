extern alias AgentCore;

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using AgentCore::Sunder.Package.Agent.PackageViews;
using AgentCore::Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;
using Xunit;
using AgentChatSelectionStateService = AgentCore::Sunder.Package.Agent.Services.AgentChatSelectionStateService;
using CorePresentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;
using static Sunder.Package.Agent.Tests.RuntimeSnapshotFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Runtime correctness")]
public sealed class ChatStartupRecoveryTests
{
    [Fact]
    public async Task ChatViewModel_InitialSnapshotRequestIncludesPersistedAppSelections()
    {
        using var scope = RegressionTestPackageScope.Create();
        var selectionState = new AgentChatSelectionStateService(scope.Context);
        var snapshot = CreateChatSnapshot(revision: 16);
        var profileId = snapshot.SelectedProfile!.ProfileId;
        var workspaceId = snapshot.SelectedWorkspace!.WorkspaceId;
        var sessionId = snapshot.SelectedSession!.Session.SessionId;
        await selectionState.SaveSelectedProfileIdAsync(profileId);
        await selectionState.SaveSelectedWorkspaceIdAsync(workspaceId);
        await selectionState.SaveSelectedSessionIdAsync(workspaceId, sessionId);
        var client = new StaticChatSnapshotRuntimeClient(snapshot);
        using var gateway = new AgentAppRuntimeGateway(client);
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway,
            selectionState);

        await viewModel.InitializeAsync();

        var request = Assert.Single(client.ChatSnapshotRequests);
        Assert.Equal(profileId, request.PreferredProfileId);
        Assert.Equal(workspaceId, request.PreferredWorkspaceId);
        Assert.Equal(sessionId, request.PreferredSessionId);
    }

    [Fact]
    public async Task ChatViewModel_CanceledWaiterDoesNotApplyStaleStartupOrLoseSnapshotRevision()
    {
        var snapshot = CreateChatSnapshot(revision: 17);
        var client = new BlockingChatSnapshotRuntimeClient(snapshot);
        using var gateway = new AgentAppRuntimeGateway(client);
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);
        using var cancellation = new CancellationTokenSource();

        var canceledWaiter = viewModel.InitializeAsync(cancellation.Token);
        await client.SnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
        Assert.Null(viewModel.SelectedSession);

        client.ReleaseSnapshot();
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => client.AfterRevisions.Contains(snapshot.Revision));
        await WaitUntilAsync(() => viewModel.Messages.Count == 1);

        Assert.Equal(1, client.SnapshotInvocationCount);
        Assert.Equal(snapshot.SelectedProfile, viewModel.SelectedProfile);
        Assert.Equal(snapshot.SelectedWorkspace?.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal(snapshot.SelectedSession?.Session.SessionId, viewModel.SelectedSession?.SessionId);
        Assert.Equal("Live after snapshot.", Assert.IsType<AgentTextTranscriptRowViewModel>(
            viewModel.Messages[0]).Content);
    }

    [Fact]
    public async Task ChatViewModel_CanceledSelectionPersistenceReleasesSerializationGate()
    {
        using var scope = RegressionTestPackageScope.Create();
        var selectionState = new AgentChatSelectionStateService(scope.Context);
        Assert.Null(await selectionState.GetSelectedProfileIdAsync());
        var state = Assert.IsType<RegressionTestKeyValueStore>(scope.Context.Storage.State);
        var blockedWrite = state.BlockNextWrite();
        var client = new BlockingChatSnapshotRuntimeClient(CreateChatSnapshot(revision: 17));
        using var gateway = new AgentAppRuntimeGateway(client);
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway,
            selectionState);
        var persistSelection = typeof(AgentChatViewModel)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "PersistAppliedSelectionAsync"
                              && method.GetParameters().Length == 5);
        var sessionId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        var canceledPersistence = Assert.IsAssignableFrom<Task>(persistSelection.Invoke(
            viewModel,
            ["profile-a", "workspace-a", sessionId, 0, cancellation.Token]));
        await blockedWrite.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledPersistence.WaitAsync(TimeSpan.FromSeconds(2)));
        var completedPersistence = Assert.IsAssignableFrom<Task>(persistSelection.Invoke(
            viewModel,
            ["profile-b", "workspace-b", sessionId, 0, CancellationToken.None]));
        await completedPersistence.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("profile-b", await selectionState.GetSelectedProfileIdAsync());
        Assert.Equal("workspace-b", await selectionState.GetSelectedWorkspaceIdAsync());
        Assert.Equal(sessionId, await selectionState.GetSelectedSessionIdAsync("workspace-b"));
        blockedWrite.Release();
    }

    [Fact]
    public async Task ChatViewModel_ConcurrentInitializeAsyncCallsApplyOneDeterministicSnapshot()
    {
        var snapshot = CreateChatSnapshot(revision: 23);
        var client = new BlockingChatSnapshotRuntimeClient(snapshot);
        using var gateway = new AgentAppRuntimeGateway(client);
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);

        var first = viewModel.InitializeAsync();
        var second = viewModel.InitializeAsync();
        await client.SnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        client.ReleaseSnapshot();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, client.SnapshotInvocationCount);
        Assert.Equal(snapshot.SelectedWorkspace?.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal(snapshot.SelectedSession?.Session.SessionId, viewModel.SelectedSession?.SessionId);
    }

    [Fact]
    public async Task ChatViewModel_UnavailableStartupAutomaticallyInitializesWhenRuntimeBecomesReady()
    {
        var snapshot = CreateChatSnapshot(revision: 29);
        var client = new UnavailableThenReadyChatRuntimeClient(snapshot);
        using var gateway = new AgentAppRuntimeGateway(client);
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);

        var failure = await Assert.ThrowsAsync<SunderRpcException>(
            () => viewModel.InitializeAsync());
        viewModel.ReportStartupFailure(failure);
        Assert.False(viewModel.IsInitialized);

        client.MakeReady();
        await WaitUntilAsync(() => viewModel.IsInitialized);

        Assert.Equal(1, client.SuccessfulSnapshotInvocationCount);
        Assert.Equal(snapshot.SelectedWorkspace?.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal(snapshot.SelectedSession?.Session.SessionId, viewModel.SelectedSession?.SessionId);
        Assert.Equal(AgentRuntimeConnectionState.Connected, gateway.ConnectionState);
    }

    [Fact]
    public async Task ChatViewModel_ConnectedDuringSecondTransientFailureDrainsPendingRecovery()
    {
        var snapshot = CreateChatSnapshot(revision: 30);
        var startup = new InterleavedStartupProfileGateway(snapshot);
        using var dataGateway = new AgentAppRuntimeGateway(
            new StaticChatSnapshotRuntimeClient(snapshot));
        using var viewModel = new AgentChatViewModel(
            startup,
            dataGateway,
            dataGateway,
            dataGateway,
            dataGateway);

        var failure = await Assert.ThrowsAsync<TimeoutException>(() => viewModel.InitializeAsync());
        viewModel.ReportStartupFailure(failure);
        Assert.Equal(1, startup.SnapshotInvocationCount);

        startup.Connect();
        await WaitUntilAsync(() => viewModel.IsInitialized);

        Assert.True(startup.ConnectedRaisedDuringSecondFailure);
        Assert.Equal(2, startup.ClassificationCount);
        Assert.Equal(3, startup.SnapshotInvocationCount);
        Assert.Equal(snapshot.SelectedWorkspace?.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal(snapshot.SelectedSession?.Session.SessionId, viewModel.SelectedSession?.SessionId);
    }

    [Fact]
    public async Task ChatViewModel_PermanentStartupFailureDoesNotRetryAfterChangeStreamConnects()
    {
        var client = new PermanentChatStartupFailureRuntimeClient();
        using var gateway = new AgentAppRuntimeGateway(client);
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);

        var failure = await Assert.ThrowsAsync<SunderRpcException>(
            () => viewModel.InitializeAsync());
        viewModel.ReportStartupFailure(failure);
        await gateway.InitializeAsync();
        await client.ChangeStreamConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(viewModel.IsInitialized);
        Assert.Equal(1, client.ChatSnapshotInvocationCount);
    }

    [Fact]
    public async Task ChatViewModel_ResnapshotTreatsIdleAdmissionAsQueuedActiveWork()
    {
        var snapshot = CreateChatSnapshot(80);
        var sessionId = snapshot.SelectedSession!.Session.SessionId;
        var now = DateTimeOffset.UtcNow;
        var userTurn = new AgentTurnRecord(
            Guid.NewGuid(),
            sessionId,
            AgentMessageRole.User,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                Guid.Empty,
                0,
                AgentTurnItemKind.Text,
                "admitted while disconnected",
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
        userTurn = userTurn with
        {
            Items = userTurn.Items.Select(item => item with { TurnId = userTurn.TurnId }).ToArray(),
        };
        snapshot = snapshot with
        {
            InitialTranscript = new AgentTranscriptPage(snapshot.Revision, [userTurn], false),
        };
        using var gateway = new AgentAppRuntimeGateway(
            new StaticChatSnapshotRuntimeClient(snapshot));
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);

        viewModel.IsDetailedTranscript = true; // This fixture supplies Detailed's flat initial page.
        await viewModel.InitializeAsync();
        var restarted = snapshot with
        {
            Revision = 1,
            RuntimeInstanceId = "runtime-after-reconnect",
            InitialTranscript = snapshot.InitialTranscript with { Revision = 1 },
        };
        typeof(AgentChatViewModel).GetMethod(
                "ApplyChatSnapshot",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [restarted, true]);

        Assert.True(viewModel.SelectedSession?.IsRunActive);
        Assert.Equal("Queued", viewModel.SelectedSession?.StatusBadgeText);
        Assert.Single(viewModel.Messages, row => row.RowId == userTurn.TurnId);
    }

    [Fact]
    public async Task ChatViewModel_CanceledStaleSnapshotCannotOverwriteOrPersistNewerSelection()
    {
        using var scope = RegressionTestPackageScope.Create();
        var snapshots = CreateWorkspaceSelectionSnapshots();
        var client = new RacingChatSnapshotRuntimeClient(snapshots.First, snapshots.Second);
        using var gateway = new AgentAppRuntimeGateway(client);
        var selections = new AgentChatSelectionStateService(scope.Context);
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway,
            selections);
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.SelectedWorkspace = viewModel.Workspaces.Single(workspace =>
            workspace.WorkspaceId == snapshots.Second.SelectedWorkspace!.WorkspaceId);
        await client.DelayedRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        viewModel.SelectedWorkspace = viewModel.Workspaces.Single(workspace =>
            workspace.WorkspaceId == snapshots.First.SelectedWorkspace!.WorkspaceId);
        await WaitUntilAsync(() => client.Requests.Count >= 3
                                   && viewModel.SelectedSession?.SessionId
                                   == snapshots.First.SelectedSession!.Session.SessionId);

        client.ReleaseDelayedRequest();
        await client.DelayedRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);

        Assert.Equal(snapshots.First.SelectedWorkspace!.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal(snapshots.First.SelectedSession!.Session.SessionId, viewModel.SelectedSession?.SessionId);
        await WaitUntilAsync(() => string.Equals(
            selections.GetSelectedWorkspaceIdAsync().GetAwaiter().GetResult(),
            snapshots.First.SelectedWorkspace.WorkspaceId,
            StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            snapshots.First.SelectedSession.Session.SessionId,
            await selections.GetSelectedSessionIdAsync(
                snapshots.First.SelectedWorkspace.WorkspaceId));
    }

    [Fact]
    public async Task ChatViewModel_StaleSessionSnapshotCannotOverwriteNewerSelection()
    {
        var snapshots = CreateSessionSelectionSnapshots();
        var client = new RacingChatSnapshotRuntimeClient(snapshots.First, snapshots.Second);
        using var gateway = new AgentAppRuntimeGateway(client);
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.SelectedSession = viewModel.Sessions.Single(session =>
            session.SessionId == snapshots.Second.SelectedSession!.Session.SessionId);
        await client.DelayedRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        viewModel.SelectedSession = viewModel.Sessions.Single(session =>
            session.SessionId == snapshots.First.SelectedSession!.Session.SessionId);
        await WaitUntilAsync(() => client.Requests.Count >= 3
                                   && viewModel.SelectedSession?.SessionId
                                   == snapshots.First.SelectedSession!.Session.SessionId);

        client.ReleaseDelayedRequest();
        await client.DelayedRequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);

        Assert.Equal(snapshots.First.SelectedSession!.Session.SessionId, viewModel.SelectedSession?.SessionId);
    }

    [Fact]
    public async Task ChatViewModel_InitialProjectionUsesSnapshotSessionsForSenderAndChildLinks()
    {
        var snapshot = CreateChatSnapshotWithChildLink(revision: 61);
        using var gateway = new AgentAppRuntimeGateway(
            new StaticChatSnapshotRuntimeClient(snapshot));
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);

        viewModel.IsDetailedTranscript = true;
        await viewModel.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var message = Assert.Single(viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>());
        Assert.Equal("Snapshot subagent", message.SenderDisplayName);
        var tool = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        var child = Assert.Single(tool.ChildSessionLinks);
        Assert.Equal("Child session", child.Title);
        Assert.Equal("Snapshot subagent", child.Subtitle);
        Assert.Equal("Done", child.StatusText);
    }

    [Fact]
    public async Task ChatViewModel_SessionReplacementPreservesDraftAndClearsSessionBoundLocalState()
    {
        var snapshots = CreateSessionSelectionSnapshots();
        var client = new SelectionChatSnapshotRuntimeClient(snapshots.First, snapshots.Second);
        using var gateway = new AgentAppRuntimeGateway(client);
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);
        await viewModel.InitializeAsync();
        var firstSessionId = snapshots.First.SelectedSession!.Session.SessionId;
        var secondSessionId = snapshots.Second.SelectedSession!.Session.SessionId;
        viewModel.DraftMessage = "first draft";
        viewModel.PendingAttachments.Add(CreatePendingAttachment("first.txt"));
        viewModel.SetTranscriptViewportAnchor(new CorePresentation.TranscriptViewportAnchorData(
            "first-anchor",
            10,
            20));

        viewModel.SelectedSession = viewModel.Sessions.Single(session => session.SessionId == secondSessionId);
        await WaitUntilAsync(() => client.Requests.Count >= 2
                                   && viewModel.SelectedSession?.SessionId == secondSessionId
                                   && viewModel.TranscriptViewportAnchor is null);

        Assert.Empty(viewModel.DraftMessage);
        Assert.Empty(viewModel.PendingAttachments);
        Assert.Null(viewModel.PendingRollbackTurnId);
        viewModel.DraftMessage = "rollback draft";
        viewModel.PendingAttachments.Add(CreatePendingAttachment("second.txt"));
        viewModel.PendingRollbackTurnId = Guid.NewGuid();
        viewModel.SetTranscriptViewportAnchor(new CorePresentation.TranscriptViewportAnchorData(
            "second-anchor",
            5,
            10));

        viewModel.SelectedSession = viewModel.Sessions.Single(session => session.SessionId == firstSessionId);
        await WaitUntilAsync(() => client.Requests.Count >= 3
                                   && viewModel.SelectedSession?.SessionId == firstSessionId
                                   && viewModel.TranscriptViewportAnchor is null);

        Assert.Equal("first draft", viewModel.DraftMessage);
        Assert.Empty(viewModel.PendingAttachments);
        Assert.Null(viewModel.PendingRollbackTurnId);
    }

    private static (AgentChatSnapshotProjection First, AgentChatSnapshotProjection Second)
        CreateSessionSelectionSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AgentProfileRecord(
            "session-race-profile",
            "Session race profile",
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now);
        var workspace = new AgentWorkspaceRecord(
            "session-race-workspace",
            "Session race workspace",
            null,
            now,
            now);
        var firstSession = CreateRootSession("First session", workspace.WorkspaceId, profile.ProfileId, now);
        var secondSession = CreateRootSession("Second session", workspace.WorkspaceId, profile.ProfileId, now);
        var firstSessionSnapshot = new AgentSessionSnapshot(firstSession, null);
        var secondSessionSnapshot = new AgentSessionSnapshot(secondSession, null);
        var workspaceSessions = new[] { firstSessionSnapshot, secondSessionSnapshot };
        AgentChatSnapshotProjection Create(long revision, AgentSessionSnapshot selected)
            => new(
                revision,
                [profile],
                [workspace],
                [],
                profile,
                workspace,
                selected,
                workspaceSessions,
                new AgentTranscriptPage(revision, [], false),
                new AgentChatPermissionProjection(
                    revision,
                    new AgentSessionPermissionState(selected.Session.SessionId, false),
                    []));
        return (Create(51, firstSessionSnapshot), Create(52, secondSessionSnapshot));
    }

    private sealed class UnavailableThenReadyChatRuntimeClient(
        AgentChatSnapshotProjection snapshot) : OwnerServiceStub
    {
        private int _isAvailable;
        private int _successfulSnapshotInvocationCount;

        public bool IsAvailable => Volatile.Read(ref _isAvailable) != 0;

        public int SuccessfulSnapshotInvocationCount
            => Volatile.Read(ref _successfulSnapshotInvocationCount);

        public void MakeReady() => Volatile.Write(ref _isAvailable, 1);

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAvailable)
            {
                throw RuntimeUnavailable();
            }
            if (request is not AgentChatSnapshotRequest)
            {
                throw new NotSupportedException(typeof(TRequest).Name);
            }

            Interlocked.Increment(ref _successfulSnapshotInvocationCount);
            return ValueTask.FromResult((TResponse)(object)snapshot);
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            if (!IsAvailable)
            {
                throw RuntimeUnavailable();
            }

            yield return (TEvent)(object)new AgentRuntimeChange(
                snapshot.Revision,
                AgentRuntimeChangeKind.Connected,
                RuntimeInstanceId: snapshot.RuntimeInstanceId);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class InterleavedStartupProfileGateway(
        AgentChatSnapshotProjection snapshot) :
        IAgentProfileGateway,
        IAgentChatSnapshotGateway,
        IAgentRuntimeAvailability,
        IAgentRuntimeFailureClassifier
    {
        private int _connectionState = (int)AgentRuntimeConnectionState.Unavailable;
        private int _snapshotInvocationCount;
        private int _classificationCount;
        private int _connectedRaisedDuringSecondFailure;

        public int SnapshotInvocationCount => Volatile.Read(ref _snapshotInvocationCount);

        public int ClassificationCount => Volatile.Read(ref _classificationCount);

        public bool ConnectedRaisedDuringSecondFailure
            => Volatile.Read(ref _connectedRaisedDuringSecondFailure) != 0;

        public AgentRuntimeConnectionState ConnectionState
            => (AgentRuntimeConnectionState)Volatile.Read(ref _connectionState);

        public bool IsRuntimeAvailable => ConnectionState == AgentRuntimeConnectionState.Connected;

        public event Action<AgentRuntimeConnectionState>? ConnectionStateChanged;

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

        public event Action<AgentChatSnapshotProjection>? ChatSnapshotReloaded
        {
            add { }
            remove { }
        }

        public void Connect()
        {
            Volatile.Write(ref _connectionState, (int)AgentRuntimeConnectionState.Connected);
            ConnectionStateChanged?.Invoke(AgentRuntimeConnectionState.Connected);
        }

        public bool IsRetryableRuntimeFailure(
            Exception exception,
            CancellationToken callerCancellationToken = default)
        {
            if (exception is not TimeoutException || callerCancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (Interlocked.Increment(ref _classificationCount) == 2)
            {
                Interlocked.Exchange(ref _connectedRaisedDuringSecondFailure, 1);
                ConnectionStateChanged?.Invoke(AgentRuntimeConnectionState.Connected);
            }
            return true;
        }

        public Task<AgentChatSnapshotProjection> LoadChatSnapshotAsync(
            AgentChatSnapshotRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Interlocked.Increment(ref _snapshotInvocationCount) <= 2
                ? Task.FromException<AgentChatSnapshotProjection>(
                    new TimeoutException("Injected transient Agent Chat startup failure."))
                : Task.FromResult(snapshot);
        }

        public void CompleteChatSnapshot(AgentChatSnapshotProjection completedSnapshot, bool applied) { }

        public IReadOnlyList<AgentProfileRecord> ListProfiles() => snapshot.Profiles;

        public AgentProfileRecord? GetProfile(string profileId)
            => snapshot.Profiles.FirstOrDefault(profile => string.Equals(
                profile.ProfileId,
                profileId,
                StringComparison.OrdinalIgnoreCase));

        public AgentProfileModelBindingRecord? GetChatBinding(string profileId)
            => (GetProfile(profileId)?.ModelBindings ?? []).FirstOrDefault(binding => string.Equals(
                binding.CapabilityKind,
                AgentModelCapabilityKinds.Chat,
                StringComparison.OrdinalIgnoreCase));

        public Task<AgentProfileRecord> CreateProfileAsync(
            string displayName,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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
            => throw new NotSupportedException();

        public void DeleteProfile(string profileId) => throw new NotSupportedException();

        public Task<IReadOnlyList<AgentBehaviorLoopDescriptor>> ListBehaviorLoopDescriptorsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentBehaviorLoopDescriptor>>([]);

        public Task<IReadOnlyList<AgentProviderDescriptor>> ListChatProviderDescriptorsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentProviderDescriptor>>([]);

        public Task<IReadOnlyList<AgentEmbeddingProviderDescriptor>> ListEmbeddingProviderDescriptorsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentEmbeddingProviderDescriptor>>([]);

        public Task<bool> HasProfileCapabilityConsumersAsync(string capabilityKind, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListSelectableProfileCapabilitiesAsync(
            AgentProfileRecord? profile = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);

        public Task<IReadOnlyList<AgentCore::Sunder.Package.Agent.Services.AgentToolCatalogEntry>> ListInstalledLocalToolsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentCore::Sunder.Package.Agent.Services.AgentToolCatalogEntry>>([]);

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
    }

    private sealed class PermanentChatStartupFailureRuntimeClient : OwnerServiceStub
    {
        private int _chatSnapshotInvocationCount;

        public bool IsAvailable => true;

        public int ChatSnapshotInvocationCount => Volatile.Read(ref _chatSnapshotInvocationCount);

        public TaskCompletionSource ChangeStreamConnected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is AgentChatSnapshotRequest)
            {
                Interlocked.Increment(ref _chatSnapshotInvocationCount);
                return ValueTask.FromException<TResponse>(new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Validation,
                    "agent.owner.validation",
                    "The owner RPC request is invalid.")));
            }
            if (request is AgentDashboardRequest)
            {
                return ValueTask.FromResult((TResponse)(object)new AgentDashboardProjection(0, [], [], []));
            }

            throw new NotSupportedException(typeof(TRequest).Name);
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            yield return (TEvent)(object)new AgentRuntimeChange(
                0,
                AgentRuntimeChangeKind.Connected,
                RuntimeInstanceId: "permanent-startup-failure-runtime");
            ChangeStreamConnected.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class RacingChatSnapshotRuntimeClient(
        AgentChatSnapshotProjection first,
        AgentChatSnapshotProjection second)
        : OwnerServiceStub
    {
        private readonly TaskCompletionSource _delayedRequestRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _delayByWorkspace = !string.Equals(
            first.SelectedWorkspace?.WorkspaceId,
            second.SelectedWorkspace?.WorkspaceId,
            StringComparison.OrdinalIgnoreCase);

        public bool IsAvailable => true;
        public ConcurrentQueue<AgentChatSnapshotRequest> Requests { get; } = new();
        public TaskCompletionSource DelayedRequestStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DelayedRequestCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseDelayedRequest() => _delayedRequestRelease.TrySetResult();

        public override async ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (request is not AgentChatSnapshotRequest)
            {
                throw new NotSupportedException(typeof(TRequest).Name);
            }

            var chatRequest = (AgentChatSnapshotRequest)(object)request;
            Requests.Enqueue(chatRequest);
            var shouldDelay = _delayByWorkspace
                ? string.Equals(
                    chatRequest.PreferredWorkspaceId,
                    second.SelectedWorkspace?.WorkspaceId,
                    StringComparison.OrdinalIgnoreCase)
                : chatRequest.PreferredSessionId == second.SelectedSession?.Session.SessionId;
            if (shouldDelay)
            {
                DelayedRequestStarted.TrySetResult();
                await _delayedRequestRelease.Task.ConfigureAwait(false);
                DelayedRequestCompleted.TrySetResult();
                return (TResponse)(object)second;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return (TResponse)(object)first;
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class SelectionChatSnapshotRuntimeClient(
        AgentChatSnapshotProjection first,
        AgentChatSnapshotProjection second)
        : OwnerServiceStub
    {
        public bool IsAvailable => true;
        public ConcurrentQueue<AgentChatSnapshotRequest> Requests { get; } = new();

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is not AgentChatSnapshotRequest)
            {
                return ValueTask.FromException<TResponse>(new NotSupportedException(typeof(TRequest).Name));
            }

            var chatRequest = (AgentChatSnapshotRequest)(object)request;
            Requests.Enqueue(chatRequest);
            var snapshot = chatRequest.PreferredSessionId == second.SelectedSession?.Session.SessionId
                ? second
                : first;
            return ValueTask.FromResult((TResponse)(object)snapshot);
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }
}
