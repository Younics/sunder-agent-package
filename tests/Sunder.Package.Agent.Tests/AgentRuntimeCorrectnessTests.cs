extern alias AgentCore;

using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Reflection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
using Sunder.Sdk.Storage;
using Xunit;
using CorePresentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRuntimeCorrectnessTests
{
    [Fact]
    public async Task ChatSelection_UsesPortablePhysicalKeyForImportedWorkspaceId()
    {
        using var scope = RegressionTestPackageScope.Create();
        var workspaceId = " imported:/\u65E5\u672C\u8A9E workspace/" + new string('w', 300);
        var sessionId = Guid.NewGuid();
        var selections = new AgentChatSelectionStateService(scope.Context);

        await selections.SaveSelectedSessionIdAsync(workspaceId, sessionId);

        Assert.Equal(sessionId, await selections.GetSelectedSessionIdAsync(workspaceId));
        var key = Assert.Single(await scope.Context.Storage.State.ListKeysAsync());
        Assert.True(PackageStorageValidation.IsValidKey(key));
        Assert.Equal(
            PackageStorageKeyFactory.Create("agent.chat.selected-session", 1, workspaceId.Trim()),
            key);
    }

    [Fact]
    public async Task ChatSnapshotHandler_ResolvesExplicitSelectionsWithoutReadingAppState()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store, catalog);
        var workspaces = new AgentWorkspaceService(store, catalog, sessions);
        var executionTargets = new AgentExecutionTargetService(catalog);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            executionTargets,
            catalog);
        using var profiles = new AgentProfileService(store, tools, catalog, catalog.BehaviorLoops);
        var permissions = new AgentPermissionService(store, catalog);
        using var changes = new AgentRuntimeChangeHub(profiles, workspaces, sessions);
        var profile = await profiles.CreateProfileAsync("Snapshot profile");
        var workspace = workspaces.CreateWorkspace("Snapshot workspace");
        var rootSession = sessions.CreateSession(
            "Root session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var childSession = sessions.CreateSession(
            "Child session",
            parentSessionId: rootSession.SessionId,
            rootSessionId: rootSession.SessionId,
            workspaceId: workspace.WorkspaceId);
        var checkpoint = sessions.SaveCheckpoint(
            rootSession.SessionId,
            1,
            AgentRunStatus.Completed,
            "Snapshot complete.");
        var transcriptContent = new string('x', 10_000);
        sessions.AppendTextTurn(
            rootSession.SessionId,
            AgentMessageRole.Assistant,
            transcriptContent);
        permissions.SetSessionUnrestrictedMode(rootSession.SessionId, true);
        var handler = new AgentChatSnapshotHandler(store, changes);

        var snapshot = await handler.HandleAsync(new AgentChatSnapshotRequest(
            PreferredProfileId: "missing-preferred-profile",
            PreferredWorkspaceId: "missing-preferred-workspace",
            PreferredSessionId: Guid.NewGuid()));

        Assert.Equal(changes.Revision, snapshot.Revision);
        Assert.Equal(changes.InstanceId, snapshot.RuntimeInstanceId);
        Assert.Equal(snapshot.Revision, snapshot.InitialTranscript.Revision);
        Assert.Equal(profile.ProfileId, snapshot.SelectedProfile?.ProfileId);
        Assert.Equal(workspace.WorkspaceId, snapshot.SelectedWorkspace?.WorkspaceId);
        Assert.Equal(rootSession.SessionId, snapshot.SelectedSession?.Session.SessionId);
        Assert.Equal(checkpoint.CheckpointId, snapshot.SelectedSession?.Checkpoint?.CheckpointId);
        Assert.Contains(snapshot.WorkspaceSessions, item => item.Session.SessionId == childSession.SessionId);
        Assert.True(snapshot.Permissions.SessionState?.IsUnrestrictedModeEnabled);
        Assert.DoesNotContain(
            typeof(AgentChatPermissionProjection).GetProperties(),
            property => property.Name is "Actions" or "Overrides");
        var transcriptItem = Assert.Single(snapshot.InitialTranscript.Turns).Items[0];
        Assert.Equal(transcriptContent, transcriptItem.TextContent);
        Assert.False(transcriptItem.WasTruncated);
    }

    [Fact]
    public async Task ChatSnapshotHandler_PrefersValidRequestedSelectionsWithoutPersistingThem()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store, catalog);
        var workspaces = new AgentWorkspaceService(store, catalog, sessions);
        var executionTargets = new AgentExecutionTargetService(catalog);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            executionTargets,
            catalog);
        using var profiles = new AgentProfileService(store, tools, catalog, catalog.BehaviorLoops);
        var permissions = new AgentPermissionService(store, catalog);
        using var changes = new AgentRuntimeChangeHub(profiles, workspaces, sessions);
        var storedProfile = await profiles.CreateProfileAsync("Stored profile");
        var preferredProfile = await profiles.CreateProfileAsync("Preferred profile");
        var storedWorkspace = workspaces.CreateWorkspace("Stored workspace");
        var preferredWorkspace = workspaces.CreateWorkspace("Preferred workspace");
        var storedSession = sessions.CreateSession(
            "Stored session",
            workspaceId: storedWorkspace.WorkspaceId);
        var preferredSession = sessions.CreateSession(
            "Preferred session",
            workspaceId: preferredWorkspace.WorkspaceId);
        var handler = new AgentChatSnapshotHandler(store, changes);

        var snapshot = await handler.HandleAsync(new AgentChatSnapshotRequest(
            PreferredProfileId: preferredProfile.ProfileId,
            PreferredWorkspaceId: preferredWorkspace.WorkspaceId,
            PreferredSessionId: preferredSession.SessionId));

        Assert.Equal(preferredProfile.ProfileId, snapshot.SelectedProfile?.ProfileId);
        Assert.Equal(preferredWorkspace.WorkspaceId, snapshot.SelectedWorkspace?.WorkspaceId);
        Assert.Equal(preferredSession.SessionId, snapshot.SelectedSession?.Session.SessionId);
        Assert.NotEqual(storedProfile.ProfileId, snapshot.SelectedProfile?.ProfileId);
        Assert.NotEqual(storedWorkspace.WorkspaceId, snapshot.SelectedWorkspace?.WorkspaceId);
        Assert.NotEqual(storedSession.SessionId, snapshot.SelectedSession?.Session.SessionId);
    }

    [Fact]
    public async Task ChatSnapshotHandler_ReusesUnchangedSnapshotAndInvalidatesForRuntimeChanges()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store, catalog);
        var workspaces = new AgentWorkspaceService(store, catalog, sessions);
        var executionTargets = new AgentExecutionTargetService(catalog);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            executionTargets,
            catalog);
        using var profiles = new AgentProfileService(store, tools, catalog, catalog.BehaviorLoops);
        using var changes = new AgentRuntimeChangeHub(profiles, workspaces, sessions);
        var profile = await profiles.CreateProfileAsync("Cached profile");
        var workspace = workspaces.CreateWorkspace("Cached workspace");
        var session = sessions.CreateSession(
            "Cached session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var handler = new AgentChatSnapshotHandler(store, changes);
        var request = new AgentChatSnapshotRequest(
            PreferredProfileId: profile.ProfileId,
            PreferredWorkspaceId: workspace.WorkspaceId,
            PreferredSessionId: session.SessionId);

        var first = await handler.HandleAsync(request);
        var cached = await handler.HandleAsync(request);
        sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "Invalidate the cache.");
        var refreshed = await handler.HandleAsync(request);

        Assert.Same(first, cached);
        Assert.NotSame(first, refreshed);
        Assert.True(refreshed.Revision > first.Revision);
        Assert.Equal("Invalidate the cache.", Assert.Single(refreshed.InitialTranscript.Turns).Items[0].TextContent);
    }

    [Fact]
    public async Task ChatSnapshotHandler_InvalidatesWhenRequestedSessionSelectionChanges()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store, catalog);
        var workspaces = new AgentWorkspaceService(store, catalog, sessions);
        var executionTargets = new AgentExecutionTargetService(catalog);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            executionTargets,
            catalog);
        using var profiles = new AgentProfileService(store, tools, catalog, catalog.BehaviorLoops);
        using var changes = new AgentRuntimeChangeHub(profiles, workspaces, sessions);
        var workspace = workspaces.CreateWorkspace("Selection workspace");
        var firstSession = sessions.CreateSession("First session", workspaceId: workspace.WorkspaceId);
        var secondSession = sessions.CreateSession("Second session", workspaceId: workspace.WorkspaceId);
        var handler = new AgentChatSnapshotHandler(store, changes);
        var firstRequest = new AgentChatSnapshotRequest(
            PreferredWorkspaceId: workspace.WorkspaceId,
            PreferredSessionId: firstSession.SessionId);
        var secondRequest = firstRequest with { PreferredSessionId = secondSession.SessionId };

        var first = await handler.HandleAsync(firstRequest);
        var second = await handler.HandleAsync(secondRequest);

        Assert.NotSame(first, second);
        Assert.Equal(firstSession.SessionId, first.SelectedSession?.Session.SessionId);
        Assert.Equal(secondSession.SessionId, second.SelectedSession?.Session.SessionId);
    }

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
        Assert.Equal(snapshot.SelectedWorkspace, viewModel.SelectedWorkspace);
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
        await blockedWrite.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
        blockedWrite.Release.TrySetResult();
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

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
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
        using var dataGateway = new AgentAppRuntimeGateway(new StaticChatSnapshotRuntimeClient(snapshot));
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

        var failure = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(
            () => viewModel.InitializeAsync());
        viewModel.ReportStartupFailure(failure);
        await gateway.InitializeAsync();
        await client.ChangeStreamConnected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(viewModel.IsInitialized);
        Assert.Equal(1, client.ChatSnapshotInvocationCount);
    }

    [Fact]
    public async Task AppRuntimeGateway_HealthySnapshotHandoffDoesNotReportReconnect()
    {
        var snapshot = CreateChatSnapshot(revision: 31);
        var client = new BlockingChatSnapshotRuntimeClient(snapshot);
        using var gateway = new AgentAppRuntimeGateway(client);
        var states = new ConcurrentQueue<AgentRuntimeConnectionState>();
        gateway.ConnectionStateChanged += states.Enqueue;

        var load = gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
        await client.SnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        client.ReleaseSnapshot();
        var loaded = await load;
        gateway.CompleteChatSnapshot(loaded, applied: true);
        await WaitUntilAsync(() => client.AfterRevisions.Contains(snapshot.Revision));

        Assert.Equal(AgentRuntimeConnectionState.Connected, gateway.ConnectionState);
        Assert.Contains(AgentRuntimeConnectionState.Connected, states);
        Assert.DoesNotContain(AgentRuntimeConnectionState.Reconnecting, states);
        Assert.DoesNotContain(AgentRuntimeConnectionState.Unavailable, states);
    }

    [Fact]
    public async Task ChatViewModel_RuntimeInstanceResetAcceptsLowerPermissionRevision()
    {
        var initial = CreateChatSnapshot(100);
        var sessionId = initial.SelectedSession!.Session.SessionId;
        initial = initial with
        {
            Permissions = initial.Permissions with
            {
                SessionState = new AgentSessionPermissionState(
                    sessionId,
                    true),
            },
        };
        using var gateway = new AgentAppRuntimeGateway(new StaticChatSnapshotRuntimeClient(initial));
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);
        await viewModel.InitializeAsync();
        Assert.True(viewModel.IsUnrestrictedModeEnabled);
        var restarted = initial with
        {
            Revision = 1,
            RuntimeInstanceId = "runtime-2",
            InitialTranscript = initial.InitialTranscript with { Revision = 1 },
            Permissions = new AgentChatPermissionProjection(
                1,
                new AgentSessionPermissionState(
                    sessionId,
                    false),
                []),
        };
        var applySnapshot = typeof(AgentChatViewModel).GetMethod(
            "ApplyChatSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        applySnapshot.Invoke(viewModel, [restarted, true]);

        Assert.False(viewModel.IsUnrestrictedModeEnabled);
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
        using var gateway = new AgentAppRuntimeGateway(new StaticChatSnapshotRuntimeClient(snapshot));
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);

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
    public async Task AppRuntimeGateway_CorrelatedRunsCarryRequestedUserTurnIds()
    {
        var client = new CapturingRunRuntimeClient();
        using var gateway = new AgentAppRuntimeGateway(client);
        var correlatedGateway = (IAgentCorrelatedRunGateway)gateway;
        var sessionId = Guid.NewGuid();
        var startUserTurnId = Guid.NewGuid();
        var rollbackUserTurnId = Guid.NewGuid();
        var rollbackAnchorTurnId = Guid.NewGuid();

        await correlatedGateway.QueueUserMessageAsync(
            sessionId,
            "profile",
            "start",
            "workspace",
            [],
            startUserTurnId);
        await correlatedGateway.RollbackAndQueueUserMessageAsync(
            sessionId,
            rollbackAnchorTurnId,
            "profile",
            "rollback",
            "workspace",
            [],
            rollbackUserTurnId);
        var status = await gateway.GetRunCommandStatusAsync(sessionId, startUserTurnId);

        var commands = client.Commands.ToArray();
        Assert.Equal(2, commands.Length);
        Assert.Equal(AgentRunCommandKind.Start, commands[0].Kind);
        Assert.Equal(startUserTurnId, commands[0].UserTurnId);
        Assert.Equal(AgentRunCommandKind.RollbackAndStart, commands[1].Kind);
        Assert.Equal(rollbackAnchorTurnId, commands[1].RollbackAnchorTurnId);
        Assert.Equal(rollbackUserTurnId, commands[1].UserTurnId);
        Assert.Equal(AgentRunCommandStatus.Pending, status);
        Assert.Equal(
            new AgentRunCommandStatusRequest(sessionId, startUserTurnId),
            client.LastStatusRequest);
    }

    [Fact]
    public async Task AppRuntimeGateway_UncorrelatedOverloadsGenerateStablePerCallUserTurnIds()
    {
        var client = new CapturingRunRuntimeClient();
        using var gateway = new AgentAppRuntimeGateway(client);
        var sessionId = Guid.NewGuid();

        await gateway.QueueUserMessageAsync(
            sessionId,
            "profile",
            "start",
            "workspace",
            []);
        await gateway.RollbackAndQueueUserMessageAsync(
            sessionId,
            Guid.NewGuid(),
            "profile",
            "replacement",
            "workspace",
            []);

        var commands = client.Commands.ToArray();
        Assert.Equal(2, commands.Length);
        Assert.NotNull(commands[0].UserTurnId);
        Assert.NotNull(commands[1].UserTurnId);
        Assert.NotEqual(commands[0].UserTurnId, commands[1].UserTurnId);
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
    public async Task ChatViewModel_StalePermissionReadCannotOverwriteNewerCommandResult()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = CreateSnapshotRuntime(scope.Context);
        using var profiles = runtime.Profiles;
        var profile = await profiles.CreateProfileAsync("Permission profile");
        var workspace = runtime.Workspaces.CreateWorkspace("Permission workspace");
        var session = runtime.Sessions.CreateSession(
            "Permission session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var permissions = new RacingPermissionGateway(session.SessionId);
        using var viewModel = new AgentChatViewModel(
            profiles,
            runtime.Workspaces,
            runtime.Sessions,
            permissions,
            NoOpRunGateway.Instance);
        await viewModel.InitializeAsync();

        runtime.Sessions.UpdateSession(session with
        {
            Title = "Permission session updated",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await permissions.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.IsUnrestrictedModeEnabled = true;
        await permissions.WriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => viewModel.IsUnrestrictedModeEnabled);

        permissions.ReleaseRead();
        await permissions.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(25);

        Assert.True(viewModel.IsUnrestrictedModeEnabled);
    }

    [Fact]
    public async Task ChatViewModel_NewerPermissionReadCanFollowOlderCommandResult()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = CreateSnapshotRuntime(scope.Context);
        using var profiles = runtime.Profiles;
        var profile = await profiles.CreateProfileAsync("Permission profile");
        var workspace = runtime.Workspaces.CreateWorkspace("Permission workspace");
        var session = runtime.Sessions.CreateSession(
            "Permission session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var permissions = new RacingPermissionGateway(
            session.SessionId,
            readRevision: 11,
            writeRevision: 10,
            readEnabled: false);
        using var viewModel = new AgentChatViewModel(
            profiles,
            runtime.Workspaces,
            runtime.Sessions,
            permissions,
            NoOpRunGateway.Instance);
        await viewModel.InitializeAsync();

        runtime.Sessions.UpdateSession(session with
        {
            Title = "Permission session updated",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await permissions.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.IsUnrestrictedModeEnabled = true;
        await permissions.WriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => viewModel.IsUnrestrictedModeEnabled);

        permissions.ReleaseRead();
        await permissions.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => !viewModel.IsUnrestrictedModeEnabled);
    }

    [Fact]
    public async Task Gateway_ResnapshotReusesActivatedChatRequestWithoutDashboardFallback()
    {
        var initial = CreateChatSnapshot(revision: 31);
        var refreshed = initial with
        {
            Revision = 32,
            InitialTranscript = initial.InitialTranscript with { Revision = 32 },
        };
        var client = new ResnapshotChatRuntimeClient(initial, refreshed);
        using var gateway = new AgentAppRuntimeGateway(client);
        var request = new AgentChatSnapshotRequest(
            42,
            initial.SelectedProfile?.ProfileId,
            initial.SelectedWorkspace?.WorkspaceId,
            initial.SelectedSession?.Session.SessionId);
        var reloaded = new TaskCompletionSource<AgentChatSnapshotProjection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ChatSnapshotReloaded += snapshot => reloaded.TrySetResult(snapshot);

        var snapshot = await gateway.LoadChatSnapshotAsync(request);
        gateway.CompleteChatSnapshot(snapshot, applied: true);
        var reconnectSnapshot = await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(refreshed.Revision, reconnectSnapshot.Revision);
        Assert.Equal(2, client.ChatSnapshotRequests.Count);
        Assert.All(client.ChatSnapshotRequests, actual => Assert.Equal(request, actual));
        Assert.Equal(0, client.DashboardInvocationCount);
    }

    [Fact]
    public async Task ChatSnapshot_ReadTransactionDoesNotMixWorkspaceAggregateMutationStages()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = CreateSnapshotRuntime(scope.Context);
        using var profiles = runtime.Profiles;
        using var changes = runtime.Changes;
        var profile = await profiles.CreateProfileAsync("Atomic profile");
        var workspace = runtime.Workspaces.CreateWorkspace("Before mutation");
        var beforePath = CreateWorkspacePath(workspace.WorkspaceId, "before");
        runtime.Workspaces.SaveWorkspaceAggregate(
            workspace.WorkspaceId,
            "Before mutation",
            null,
            [beforePath],
            [],
            null);
        runtime.Sessions.CreateSession(
            "Atomic session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var handler = new AgentChatSnapshotHandler(runtime.Store, changes);
        var mutationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Store.WorkspaceAggregateStageCompleted = stage =>
        {
            if (!string.Equals(stage, "workspace", StringComparison.Ordinal))
            {
                return;
            }
            mutationReached.TrySetResult();
            releaseMutation.Task.GetAwaiter().GetResult();
        };
        var mutation = Task.Run(() => runtime.Workspaces.SaveWorkspaceAggregate(
            workspace.WorkspaceId,
            "After mutation",
            null,
            [CreateWorkspacePath(workspace.WorkspaceId, "after")],
            [],
            null));

        try
        {
            await mutationReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var snapshot = await handler.HandleAsync(new AgentChatSnapshotRequest());

            Assert.Equal("Before mutation", snapshot.SelectedWorkspace?.DisplayName);
            Assert.EndsWith(
                "before",
                Assert.Single(snapshot.SelectedWorkspace!.Paths).HostPath,
                StringComparison.Ordinal);
        }
        finally
        {
            releaseMutation.TrySetResult();
            await mutation.WaitAsync(TimeSpan.FromSeconds(10));
            runtime.Store.WorkspaceAggregateStageCompleted = null;
        }
    }

    [Fact]
    public async Task ChatSnapshot_IncludesActiveRunCheckpoint()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = CreateSnapshotRuntime(scope.Context);
        using var profiles = runtime.Profiles;
        using var changes = runtime.Changes;
        var profile = await profiles.CreateProfileAsync("Running profile");
        var workspace = runtime.Workspaces.CreateWorkspace("Running workspace");
        var session = runtime.Sessions.CreateSession(
            "Running session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var checkpoint = runtime.Sessions.SaveCheckpoint(
            session.SessionId,
            7,
            AgentRunStatus.Running,
            "Running now.");
        var handler = new AgentChatSnapshotHandler(
            runtime.Store,
            changes);

        var snapshot = await handler.HandleAsync(new AgentChatSnapshotRequest(
            PreferredWorkspaceId: workspace.WorkspaceId,
            PreferredSessionId: session.SessionId));

        Assert.Equal(checkpoint, snapshot.SelectedSession?.Checkpoint);
        Assert.Equal(AgentRunStatus.Running, snapshot.SelectedSession?.Checkpoint?.Status);
    }

    [Fact]
    public async Task ChatSnapshot_JsonRoundTripsAndStaysUnderRuntimePayloadLimit()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = CreateSnapshotRuntime(scope.Context);
        using var profiles = runtime.Profiles;
        using var changes = runtime.Changes;
        var profile = await profiles.CreateProfileAsync("Payload profile");
        var workspace = runtime.Workspaces.CreateWorkspace("Payload workspace");
        var session = runtime.Sessions.CreateSession(
            "Payload session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var large = new string('x', 4_000);
        for (var index = 0; index < 180; index++)
        {
            runtime.Sessions.AppendToolResultTurn(
                session.SessionId,
                $"call-{index}",
                "payload_tool",
                large,
                large,
                large,
                large,
                large,
                wasTruncated: false,
                isError: false,
                errorCode: null,
                backendId: "payload",
                presentationPayloadJson: large);
        }
        var handler = new AgentChatSnapshotHandler(
            runtime.Store,
            changes);

        var snapshot = await handler.HandleAsync(new AgentChatSnapshotRequest(
            InitialTranscriptLimit: 500,
            PreferredWorkspaceId: workspace.WorkspaceId,
            PreferredSessionId: session.SessionId));
        var roundTrip = AgentChatSnapshotPayload.RoundTrip(snapshot);

        Assert.False(snapshot.InitialTranscript.HasMore);
        Assert.Equal(180, snapshot.InitialTranscript.Turns.Count);
        Assert.All(snapshot.InitialTranscript.Turns, turn =>
        {
            var item = Assert.Single(turn.Items);
            Assert.True(item.IsToolHeaderProjection);
            Assert.True(item.ToolHasDetails);
            Assert.Null(item.TextContent);
            Assert.Null(item.ArgumentsJson);
            Assert.Null(item.ResultSummary);
            Assert.Null(item.StructuredPayloadJson);
            Assert.Null(item.SourcesJson);
            Assert.Null(item.PresentationPayloadJson);
        });
        Assert.True(
            AgentChatSnapshotPayload.GetSerializedByteCount(snapshot)
            <= AgentChatSnapshotPayload.MaximumSerializedBytes);
        Assert.True(
            AgentChatSnapshotPayload.GetSerializedByteCount(snapshot)
            < AgentChatSnapshotPayload.RuntimeMaximumBytes);
        Assert.Equal(snapshot.SelectedSession?.Session.SessionId, roundTrip.SelectedSession?.Session.SessionId);
        Assert.Equal(snapshot.InitialTranscript.Turns.Count, roundTrip.InitialTranscript.Turns.Count);
    }

    [Fact]
    public async Task ChatSnapshot_OversizedSingleTurnRemainsCursorReachable()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = CreateSnapshotRuntime(scope.Context);
        using var profiles = runtime.Profiles;
        using var changes = runtime.Changes;
        var profile = await profiles.CreateProfileAsync("Oversized turn profile");
        var workspace = runtime.Workspaces.CreateWorkspace("Oversized turn workspace");
        var session = runtime.Sessions.CreateSession(
            "Oversized turn session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var turn = runtime.Sessions.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.Assistant,
            new string('x', 6 * 1024 * 1024));
        var handler = new AgentChatSnapshotHandler(
            runtime.Store,
            changes);

        var snapshot = await handler.HandleAsync(new AgentChatSnapshotRequest(
            InitialTranscriptLimit: 1,
            PreferredWorkspaceId: workspace.WorkspaceId,
            PreferredSessionId: session.SessionId));
        var projectedTurn = Assert.Single(snapshot.InitialTranscript.Turns);

        Assert.Equal(turn.TurnId, projectedTurn.TurnId);
        Assert.True(snapshot.InitialTranscript.HasMore);
        Assert.Equal(turn.TurnId, snapshot.InitialTranscript.Continuation?.TurnId);
        var projectedItem = Assert.Single(projectedTurn.Items);
        Assert.True(projectedItem.WasTruncated);
        Assert.Contains(
            "Content truncated for display",
            projectedItem.TextContent,
            StringComparison.Ordinal);
        Assert.True(
            AgentChatSnapshotPayload.GetSerializedByteCount(snapshot)
            <= AgentChatSnapshotPayload.MaximumSerializedBytes);
        var roundTrip = AgentChatSnapshotPayload.RoundTrip(snapshot);
        Assert.Equal(turn.TurnId, roundTrip.InitialTranscript.Continuation?.TurnId);
    }

    [Fact]
    public async Task ChatSnapshot_DoesNotFanOutToExtensionCatalogs()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new CountingExtensionCatalog();
        var runtime = CreateSnapshotRuntime(scope.Context, catalog);
        using var profiles = runtime.Profiles;
        using var changes = runtime.Changes;
        var profile = await profiles.CreateProfileAsync("Catalog profile");
        var workspace = runtime.Workspaces.CreateWorkspace("Catalog workspace");
        runtime.Sessions.CreateSession(
            "Catalog session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var baselineCalls = catalog.InvocationCount;
        var handler = new AgentChatSnapshotHandler(
            runtime.Store,
            changes);

        _ = await handler.HandleAsync(new AgentChatSnapshotRequest(
            PreferredWorkspaceId: workspace.WorkspaceId));

        Assert.Equal(baselineCalls, catalog.InvocationCount);
    }

    [Fact]
    public async Task ChatViewModel_InitialProjectionUsesSnapshotSessionsForSenderAndChildLinks()
    {
        var snapshot = CreateChatSnapshotWithChildLink(revision: 61);
        using var gateway = new AgentAppRuntimeGateway(new StaticChatSnapshotRuntimeClient(snapshot));
        using var viewModel = new AgentChatViewModel(
            gateway,
            gateway,
            gateway,
            gateway,
            gateway);

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
    public async Task Gateway_LateOldResnapshotCannotOverwriteNewReplayGeneration()
    {
        var snapshots = new[]
        {
            CreateChatSnapshot(70),
            CreateChatSnapshot(71),
            CreateChatSnapshot(72),
            CreateChatSnapshot(73),
        };
        var client = new OrderedResnapshotRuntimeClient(snapshots);
        using var gateway = new AgentAppRuntimeGateway(client);
        var reloadedRevisions = new ConcurrentQueue<long>();
        var finalReload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ChatSnapshotReloaded += snapshot =>
        {
            reloadedRevisions.Enqueue(snapshot.Revision);
            if (snapshot.Revision == 73)
            {
                finalReload.TrySetResult();
            }
        };
        var request = new AgentChatSnapshotRequest();
        var initial = await gateway.LoadChatSnapshotAsync(request);
        gateway.CompleteChatSnapshot(initial, applied: true);
        await client.OldResnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var newer = await gateway.LoadChatSnapshotAsync(request);
        Assert.Equal(72, newer.Revision);
        gateway.CompleteChatSnapshot(newer, applied: true);
        await client.NewReplayResetSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        client.ReleaseOldResnapshot();
        await finalReload.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(71, reloadedRevisions);
        Assert.Equal(73, reloadedRevisions.Last());
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

    [Fact]
    public void RuntimeChangeKind_PreservesV1WireValuesAndAppendsMutation()
    {
        Assert.Equal(0, (int)AgentRuntimeChangeKind.Connected);
        Assert.Equal(6, (int)AgentRuntimeChangeKind.Turn);
        Assert.Equal(7, (int)AgentRuntimeChangeKind.TranscriptReset);
        Assert.Equal(8, (int)AgentRuntimeChangeKind.RunActivity);
        Assert.Equal(9, (int)AgentRuntimeChangeKind.Permission);
        Assert.Equal(10, (int)AgentRuntimeChangeKind.TurnMutation);
    }

    [Fact]
    public void Gateway_RuntimeInstanceChangeForcesResnapshotAcrossOverlappingRevisions()
    {
        using var gateway = new AgentAppRuntimeGateway(new ToggleRuntimeClient(isAvailable: true));
        typeof(AgentAppRuntimeGateway)
            .GetField("_runtimeInstanceId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(gateway, "runtime-1");
        typeof(AgentAppRuntimeGateway)
            .GetField("_revision", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(gateway, 100L);
        var apply = typeof(AgentAppRuntimeGateway).GetMethod(
            "ApplyCurrentChange",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var requiresSnapshot = Assert.IsType<bool>(apply.Invoke(
            gateway,
            [new AgentRuntimeChange(
                100,
                AgentRuntimeChangeKind.Connected,
                RuntimeInstanceId: "runtime-2")]));

        Assert.True(requiresSnapshot);
        Assert.Equal(
            "runtime-2",
            typeof(AgentAppRuntimeGateway)
                .GetField("_runtimeInstanceId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(gateway));
    }

    [Fact]
    public void Gateway_TurnChangedReconstructsAppendAndCompletionMutations()
    {
        using var gateway = new AgentAppRuntimeGateway(new ToggleRuntimeClient(isAvailable: true));
        var changes = new List<AgentTurnRecord>();
        gateway.TurnChanged += (_, turn) => changes.Add(turn);
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var turn = new AgentTurnRecord(
            turnId,
            sessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                0,
                AgentTurnItemKind.Text,
                "first",
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
            now)
        {
            ContentRevision = 1,
            IsStreaming = true,
        };
        var apply = typeof(AgentAppRuntimeGateway).GetMethod(
            "ApplyCurrentChange",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        apply.Invoke(gateway, [new AgentRuntimeChange(
            1,
            AgentRuntimeChangeKind.TurnMutation,
            SessionId: sessionId,
            TurnMutation: new AgentTurnMutation(
                sessionId,
                turnId,
                1,
                AgentTurnMutationKind.Add,
                0,
                null,
                now,
                turn))]);
        apply.Invoke(gateway, [new AgentRuntimeChange(
            2,
            AgentRuntimeChangeKind.TurnMutation,
            SessionId: sessionId,
            TurnMutation: new AgentTurnMutation(
                sessionId,
                turnId,
                2,
                AgentTurnMutationKind.Append,
                "first".Length,
                " second",
                now.AddSeconds(1)))]);
        apply.Invoke(gateway, [new AgentRuntimeChange(
            3,
            AgentRuntimeChangeKind.TurnMutation,
            SessionId: sessionId,
            TurnMutation: new AgentTurnMutation(
                sessionId,
                turnId,
                3,
                AgentTurnMutationKind.Complete,
                "first second".Length,
                null,
                now.AddSeconds(2)))]);

        Assert.Equal(3, changes.Count);
        Assert.Equal("first", Assert.Single(changes[0].Items).TextContent);
        Assert.Equal("first second", Assert.Single(changes[1].Items).TextContent);
        Assert.True(changes[1].IsStreaming);
        Assert.Equal("first second", Assert.Single(changes[2].Items).TextContent);
        Assert.False(changes[2].IsStreaming);
    }

    [Fact]
    public void Gateway_ChatSnapshotPreservesOtherSessionStreamingReconstruction()
    {
        using var gateway = new AgentAppRuntimeGateway(new ToggleRuntimeClient(isAvailable: true));
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var streamingTurn = new AgentTurnRecord(
            turnId,
            sessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                0,
                AgentTurnItemKind.Text,
                "first",
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
            now)
        {
            ContentRevision = 1,
            IsStreaming = true,
        };
        var apply = typeof(AgentAppRuntimeGateway).GetMethod(
            "ApplyCurrentChange",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        apply.Invoke(gateway, [new AgentRuntimeChange(
            1,
            AgentRuntimeChangeKind.TurnMutation,
            SessionId: sessionId,
            TurnMutation: new AgentTurnMutation(
                sessionId,
                turnId,
                1,
                AgentTurnMutationKind.Add,
                0,
                null,
                now,
                streamingTurn))]);
        var applySnapshot = typeof(AgentAppRuntimeGateway).GetMethod(
            "ApplyChatSnapshotCache",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        applySnapshot.Invoke(gateway, [CreateChatSnapshot(1)]);
        var changedTurns = new List<AgentTurnRecord>();
        gateway.TurnChanged += (_, turn) => changedTurns.Add(turn);

        apply.Invoke(gateway, [new AgentRuntimeChange(
            2,
            AgentRuntimeChangeKind.TurnMutation,
            SessionId: sessionId,
            TurnMutation: new AgentTurnMutation(
                sessionId,
                turnId,
                2,
                AgentTurnMutationKind.Append,
                "first".Length,
                " second",
                now.AddSeconds(1))) ]);

        var changed = Assert.Single(changedTurns);
        Assert.Equal("first second", Assert.Single(changed.Items).TextContent);
    }

    [Fact]
    public void Gateway_StaleReplacementDoesNotRaiseCompatibilityTurnChanged()
    {
        using var gateway = new AgentAppRuntimeGateway(new ToggleRuntimeClient(isAvailable: true));
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var current = new AgentTurnRecord(
            turnId,
            sessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [],
            now,
            now)
        {
            ContentRevision = 2,
            IsStreaming = true,
        };
        var stale = current with
        {
            ContentRevision = 1,
            UpdatedAtUtc = now.AddSeconds(-1),
        };
        var cacheTurn = typeof(AgentAppRuntimeGateway).GetMethod(
            "CacheTurn",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        cacheTurn.Invoke(gateway, [current]);
        var changedCount = 0;
        gateway.TurnChanged += (_, _) => changedCount++;
        var apply = typeof(AgentAppRuntimeGateway).GetMethod(
            "ApplyCurrentChange",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        apply.Invoke(gateway, [new AgentRuntimeChange(
            1,
            AgentRuntimeChangeKind.TurnMutation,
            SessionId: sessionId,
            TurnMutation: new AgentTurnMutation(
                sessionId,
                turnId,
                1,
                AgentTurnMutationKind.Replace,
                0,
                null,
                stale.UpdatedAtUtc,
                stale))]);

        Assert.Equal(0, changedCount);
    }

    [Fact]
    public async Task ChangeHub_ReplaysAfterRevision()
    {
        using var runtime = ChangeHubRuntime.Create();
        runtime.Workspaces.CreateWorkspace("first");
        var firstRevision = runtime.Hub.Revision;
        runtime.Workspaces.CreateWorkspace("second");

        await using var subscription = runtime.Hub.SubscribeAsync(
            new AgentChangeSubscription(firstRevision)).GetAsyncEnumerator();

        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(AgentRuntimeChangeKind.Workspace, subscription.Current.Kind);
        Assert.Equal(firstRevision + 1, subscription.Current.Revision);
        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(AgentRuntimeChangeKind.Connected, subscription.Current.Kind);
    }

    [Fact]
    public async Task ChangeHub_PreservesV1TurnsAndNegotiatesCompactMutations()
    {
        using var runtime = ChangeHubRuntime.Create();
        var workspace = runtime.Workspaces.CreateWorkspace("compatibility");
        var session = runtime.Sessions.CreateSession(
            "compatibility",
            workspaceId: workspace.WorkspaceId);
        var revision = runtime.Hub.Revision;
        await using var legacy = runtime.Hub.SubscribeAsync(
            new AgentChangeSubscription(revision)).GetAsyncEnumerator();
        await using var current = runtime.Hub.SubscribeAsync(
            new AgentChangeSubscription(revision, SupportsTurnMutations: true)).GetAsyncEnumerator();
        Assert.True(await legacy.MoveNextAsync());
        Assert.Equal(AgentRuntimeChangeKind.Connected, legacy.Current.Kind);
        Assert.True(await current.MoveNextAsync());
        Assert.Equal(AgentRuntimeChangeKind.Connected, current.Current.Kind);

        var turn = runtime.Sessions.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.Assistant,
            "stream compatibility");

        Assert.True(await legacy.MoveNextAsync());
        Assert.Equal(AgentRuntimeChangeKind.Turn, legacy.Current.Kind);
        Assert.Equal(turn.TurnId, legacy.Current.Turn?.TurnId);
        Assert.Equal("stream compatibility", legacy.Current.Turn?.Items.Single().TextContent);
        Assert.Null(legacy.Current.TurnMutation);
        Assert.True(await current.MoveNextAsync());
        Assert.Equal(AgentRuntimeChangeKind.TurnMutation, current.Current.Kind);
        Assert.Null(current.Current.Turn);
        Assert.Equal(turn.TurnId, current.Current.TurnMutation?.TurnId);
    }

    [Fact]
    public async Task ChangeHub_OldRevisionRequiresResnapshot()
    {
        using var runtime = ChangeHubRuntime.Create();
        for (var index = 0; index < 260; index++)
        {
            runtime.Workspaces.CreateWorkspace($"workspace-{index}");
        }

        await using var subscription = runtime.Hub.SubscribeAsync(
            new AgentChangeSubscription(0)).GetAsyncEnumerator();

        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(AgentRuntimeChangeKind.ResnapshotRequired, subscription.Current.Kind);
        Assert.Equal(runtime.Hub.Revision, subscription.Current.Revision);
    }

    [Fact]
    public async Task ChangeHub_SlowSubscriberIsBoundedAndReset()
    {
        using var runtime = ChangeHubRuntime.Create();
        await using var subscription = runtime.Hub.SubscribeAsync(
            new AgentChangeSubscription(runtime.Hub.Revision)).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(AgentRuntimeChangeKind.Connected, subscription.Current.Kind);

        for (var index = 0; index < 80; index++)
        {
            runtime.Workspaces.CreateWorkspace($"slow-{index}");
        }

        AgentRuntimeChange? terminal = null;
        while (await subscription.MoveNextAsync())
        {
            terminal = subscription.Current;
        }

        Assert.NotNull(terminal);
        Assert.Equal(AgentRuntimeChangeKind.ResnapshotRequired, terminal!.Kind);
        Assert.Equal(runtime.Hub.Revision, terminal.Revision);
    }

    [Fact]
    public async Task Gateway_UnavailableThenAvailableRecoversAndInitializes()
    {
        var client = new ToggleRuntimeClient(isAvailable: false);
        using var gateway = new AgentAppRuntimeGateway(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.InitializeAsync());
        client.IsAvailable = true;

        await WaitUntilAsync(() => gateway.IsRuntimeAvailable);
        await gateway.InitializeAsync();

        Assert.Equal(AgentRuntimeConnectionState.Connected, gateway.ConnectionState);
        Assert.Empty(gateway.ListProfiles());
    }

    [Fact]
    public async Task Gateway_CanceledFirstInitializationDoesNotCancelSharedLoad()
    {
        var client = new ToggleRuntimeClient(isAvailable: true, blockDashboard: true);
        using var gateway = new AgentAppRuntimeGateway(client);
        using var cancellation = new CancellationTokenSource();
        var first = gateway.InitializeAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        client.ReleaseDashboard();
        await gateway.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(gateway.ListProfiles());
        Assert.Equal(1, client.DashboardInvocationCount);
    }

    [Fact]
    public async Task Gateway_RetriesSharedDashboardFaultedAfterCallerCancellation()
    {
        var client = new FaultAfterCanceledDashboardRuntimeClient();
        using var gateway = new AgentAppRuntimeGateway(client);
        using var cancellation = new CancellationTokenSource();
        var canceledWaiter = gateway.InitializeAsync(cancellation.Token);
        await client.DashboardStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);

        client.ReleaseDashboard.TrySetResult();
        await client.FirstDashboardCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await gateway.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, client.DashboardInvocationCount);
    }

    [Fact]
    public async Task Gateway_DisconnectReplaysMissedChangesFromLastRevision()
    {
        var client = new DisconnectingRuntimeClient();
        using var gateway = new AgentAppRuntimeGateway(client);
        var workspaceChanges = 0;
        gateway.WorkspacesChanged += () => Interlocked.Increment(ref workspaceChanges);

        await gateway.InitializeAsync();
        await WaitUntilAsync(() => client.LastDeliveredRevision == 2);

        Assert.True(client.AfterRevisions.Count >= 2);
        Assert.Equal(0, client.AfterRevisions[0]);
        Assert.Equal(0, client.AfterRevisions[1]);
        Assert.Equal(2, client.LastDeliveredRevision);
        Assert.True(Volatile.Read(ref workspaceChanges) >= 2);
    }

    [Fact]
    public async Task PermissionsViewModel_StartsRuntimeObservation()
    {
        var client = new PermissionRuntimeClient();
        using var gateway = new AgentAppRuntimeGateway(client);
        using var viewModel = new AgentPermissionsViewModel(gateway);

        await WaitUntilAsync(() => client.SubscriptionCount > 0);
        await WaitUntilAsync(() => viewModel.Rows.Count > 0);

        Assert.Single(viewModel.Rows);
        Assert.Equal("test.mutate", viewModel.Rows[0].ActionId);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The Runtime gateway did not reconnect in time.");
            await Task.Delay(25);
        }
    }

    private static AgentChatSnapshotProjection CreateChatSnapshot(long revision)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AgentProfileRecord(
            "profile-1",
            "Profile",
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now);
        var workspace = new AgentWorkspaceRecord(
            "workspace-1",
            "Workspace",
            null,
            now,
            now);
        var session = new AgentSessionRecord(
            Guid.NewGuid(),
            "Session",
            AgentSessionState.Active,
            now,
            now,
            RootSessionId: null,
            ProfileId: profile.ProfileId,
            WorkspaceId: workspace.WorkspaceId);
        session = session with { RootSessionId = session.SessionId };
        var checkpoint = new AgentRunCheckpointRecord(
            Guid.NewGuid(),
            session.SessionId,
            1,
            AgentRunStatus.Idle,
            null,
            now);
        var sessionSnapshot = new AgentSessionSnapshot(session, checkpoint);
        return new AgentChatSnapshotProjection(
            revision,
            [profile],
            [workspace],
            [],
            profile,
            workspace,
            sessionSnapshot,
            [sessionSnapshot],
            new AgentTranscriptPage(revision, [], false),
            new AgentChatPermissionProjection(
                revision,
                new AgentSessionPermissionState(session.SessionId, false),
                []));
    }

    private static (AgentChatSnapshotProjection First, AgentChatSnapshotProjection Second)
        CreateWorkspaceSelectionSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AgentProfileRecord(
            "race-profile",
            "Race profile",
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now);
        var firstWorkspace = new AgentWorkspaceRecord(
            "race-workspace-1",
            "First workspace",
            null,
            now,
            now);
        var secondWorkspace = new AgentWorkspaceRecord(
            "race-workspace-2",
            "Second workspace",
            null,
            now,
            now);
        var firstSession = CreateRootSession("First session", firstWorkspace.WorkspaceId, profile.ProfileId, now);
        var secondSession = CreateRootSession("Second session", secondWorkspace.WorkspaceId, profile.ProfileId, now);
        var firstSessionSnapshot = new AgentSessionSnapshot(firstSession, null);
        var secondSessionSnapshot = new AgentSessionSnapshot(secondSession, null);
        var first = new AgentChatSnapshotProjection(
            41,
            [profile],
            [firstWorkspace, secondWorkspace],
            [],
            profile,
            firstWorkspace,
            firstSessionSnapshot,
            [firstSessionSnapshot],
            new AgentTranscriptPage(41, [], false),
            new AgentChatPermissionProjection(
                41,
                new AgentSessionPermissionState(firstSession.SessionId, false),
                []));
        var second = new AgentChatSnapshotProjection(
            42,
            [profile],
            [firstWorkspace, secondWorkspace],
            [],
            profile,
            secondWorkspace,
            secondSessionSnapshot,
            [secondSessionSnapshot],
            new AgentTranscriptPage(42, [], false),
            new AgentChatPermissionProjection(
                42,
                new AgentSessionPermissionState(secondSession.SessionId, false),
                []));
        return (first, second);
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

    private static AgentSessionRecord CreateRootSession(
        string title,
        string workspaceId,
        string profileId,
        DateTimeOffset now)
    {
        var sessionId = Guid.NewGuid();
        return new AgentSessionRecord(
            sessionId,
            title,
            AgentSessionState.Active,
            now,
            now,
            RootSessionId: sessionId,
            ProfileId: profileId,
            WorkspaceId: workspaceId);
    }

    private static SnapshotRuntime CreateSnapshotRuntime(
        IPackageContext context,
        RegressionTestExtensionCatalog? catalog = null)
    {
        catalog ??= new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(context);
        var sessions = new AgentSessionService(store, catalog);
        var workspaces = new AgentWorkspaceService(store, catalog, sessions);
        var executionTargets = new AgentExecutionTargetService(catalog);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            executionTargets,
            catalog);
        var profiles = new AgentProfileService(store, tools, catalog, catalog.BehaviorLoops);
        return new SnapshotRuntime(
            store,
            sessions,
            workspaces,
            profiles,
            new AgentRuntimeChangeHub(profiles, workspaces, sessions));
    }

    private static AgentWorkspacePathRecord CreateWorkspacePath(
        string workspaceId,
        string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentWorkspacePathRecord(
            Guid.NewGuid().ToString("N"),
            workspaceId,
            Path.Combine(Path.GetTempPath(), name),
            true,
            0,
            now,
            now);
    }

    private static AgentPendingAttachmentViewModel CreatePendingAttachment(string fileName)
    {
        var content = new byte[] { 1, 2, 3 };
        return new AgentPendingAttachmentViewModel(
            Guid.NewGuid(),
            new AgentAttachmentUploadRequest(fileName, "text/plain", content),
            new AgentAttachmentInfo(
                fileName,
                "text/plain",
                AgentAttachmentKind.Text,
                content.Length,
                IsText: true,
                WasTruncated: false));
    }

    private static AgentChatSnapshotProjection CreateChatSnapshotWithChildLink(long revision)
    {
        var snapshot = CreateChatSnapshot(revision);
        var now = DateTimeOffset.UtcNow;
        var selectedProfile = snapshot.SelectedProfile!;
        var session = snapshot.SelectedSession!.Session with { ProfileId = "snapshot-subagent" };
        var subagentProfile = selectedProfile with
        {
            ProfileId = "snapshot-subagent",
            DisplayName = "Snapshot subagent",
        };
        var childSession = new AgentSessionRecord(
            Guid.NewGuid(),
            "Child session",
            AgentSessionState.Completed,
            now,
            now,
            ParentSessionId: session.SessionId,
            RootSessionId: session.SessionId,
            ParentToolCallId: "call-1",
            ProfileId: subagentProfile.ProfileId,
            AgentKind: "subagent",
            WorkspaceId: session.WorkspaceId);
        var childCheckpoint = new AgentRunCheckpointRecord(
            Guid.NewGuid(),
            childSession.SessionId,
            1,
            AgentRunStatus.Completed,
            "Child complete.",
            now);
        var messageTurnId = Guid.NewGuid();
        var toolTurnId = Guid.NewGuid();
        var message = new AgentTurnRecord(
            messageTurnId,
            session.SessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                messageTurnId,
                0,
                AgentTurnItemKind.Text,
                "Snapshot response.",
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
        var tool = new AgentTurnRecord(
            toolTurnId,
            session.SessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.ToolCall,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                toolTurnId,
                0,
                AgentTurnItemKind.ToolCall,
                null,
                "call-1",
                "task",
                "{}",
                null,
                null,
                null,
                false,
                false,
                null,
                null)],
            now.AddMilliseconds(1),
            now.AddMilliseconds(1));
        var selectedSession = snapshot.SelectedSession with { Session = session };
        return snapshot with
        {
            Profiles = [selectedProfile, subagentProfile],
            SelectedSession = selectedSession,
            WorkspaceSessions =
            [
                selectedSession,
                new AgentSessionSnapshot(childSession, childCheckpoint),
            ],
            InitialTranscript = new AgentTranscriptPage(revision, [message, tool], false),
        };
    }

    private sealed record SnapshotRuntime(
        AgentLocalStore Store,
        AgentSessionService Sessions,
        AgentWorkspaceService Workspaces,
        AgentProfileService Profiles,
        AgentRuntimeChangeHub Changes);

    private sealed class RacingPermissionGateway(
        Guid sessionId,
        long readRevision = 1,
        long writeRevision = 2,
        bool readEnabled = false)
        : IAgentPermissionGateway,
          IAgentChatPermissionCommandGateway
    {
        private readonly TaskCompletionSource _releaseRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WriteCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentSessionPermissionState GetSessionState(Guid requestedSessionId)
            => new(requestedSessionId, false);

        public void SetSessionUnrestrictedMode(Guid requestedSessionId, bool isEnabled) { }

        public IReadOnlyList<AgentPermissionActionDescriptor> ListActions() => [];

        public IReadOnlyList<AgentPermissionOverride> ListOverrides() => [];

        public void SaveOverride(
            string actionId,
            string boundaryId,
            AgentPermissionDecision decision)
        { }

        public void DeleteOverride(string actionId, string boundaryId) { }

        public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequestsForSessionTree(
            Guid requestedSessionId) => [];

        public async Task<AgentChatPermissionProjection> LoadSessionPermissionsAsync(
            Guid requestedSessionId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(sessionId, requestedSessionId);
            ReadStarted.TrySetResult();
            await _releaseRead.Task.WaitAsync(cancellationToken);
            ReadCompleted.TrySetResult();
            return new AgentChatPermissionProjection(
                readRevision,
                new AgentSessionPermissionState(sessionId, readEnabled),
                []);
        }

        public Task<AgentChatPermissionProjection> SetSessionUnrestrictedModeAsync(
            Guid requestedSessionId,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(sessionId, requestedSessionId);
            Assert.True(isEnabled);
            WriteCompleted.TrySetResult();
            return Task.FromResult(new AgentChatPermissionProjection(
                writeRevision,
                new AgentSessionPermissionState(sessionId, true),
                []));
        }

        public void ReleaseRead() => _releaseRead.TrySetResult();
    }

    private sealed class NoOpRunGateway : IAgentRunGateway
    {
        public static NoOpRunGateway Instance { get; } = new();

        public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(
            Guid sessionId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateCheckpoint(sessionId));

        public Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(
            Guid sessionId,
            Guid rollbackAnchorTurnId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateCheckpoint(sessionId));

        public Task<AgentRunCheckpointRecord?> StopAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(
            Guid sessionId,
            string requestId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        public Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(
            Guid sessionId,
            string requestId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        private static AgentRunCheckpointRecord CreateCheckpoint(Guid sessionId)
            => new(
                Guid.NewGuid(),
                sessionId,
                1,
                AgentRunStatus.Running,
                "Running",
                DateTimeOffset.UtcNow);
    }

    private sealed class ChangeHubRuntime : IDisposable
    {
        private readonly RegressionTestPackageScope _scope;

        private ChangeHubRuntime(
            RegressionTestPackageScope scope,
            AgentSessionService sessions,
            AgentWorkspaceService workspaces,
            AgentRuntimeChangeHub hub)
        {
            _scope = scope;
            Sessions = sessions;
            Workspaces = workspaces;
            Hub = hub;
        }

        public AgentSessionService Sessions { get; }
        public AgentWorkspaceService Workspaces { get; }
        public AgentRuntimeChangeHub Hub { get; }

        public static ChangeHubRuntime Create()
        {
            var scope = RegressionTestPackageScope.Create();
            var catalog = new RegressionTestExtensionCatalog();
            var store = new AgentLocalStore(scope.Context);
            var sessions = new AgentSessionService(store, catalog);
            var workspaces = new AgentWorkspaceService(store, catalog, sessions);
            var executionTargets = new AgentExecutionTargetService(catalog);
            var tools = new AgentToolService(
                sessions,
                workspaces,
                executionTargets,
                catalog);
            var profiles = new AgentProfileService(store, tools, catalog, catalog.BehaviorLoops);
            return new ChangeHubRuntime(
                scope,
                sessions,
                workspaces,
                new AgentRuntimeChangeHub(profiles, workspaces, sessions));
        }

        public void Dispose()
        {
            Hub.Dispose();
            _scope.Dispose();
        }
    }

    private sealed class ToggleRuntimeClient(bool isAvailable, bool blockDashboard = false)
        : IPackageRuntimeClient
    {
        private readonly TaskCompletionSource _dashboardRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _isAvailable = isAvailable;
        private int _dashboardInvocationCount;

        public bool IsAvailable
        {
            get => _isAvailable;
            set => _isAvailable = value;
        }

        public int DashboardInvocationCount => Volatile.Read(ref _dashboardInvocationCount);

        public void ReleaseDashboard() => _dashboardRelease.TrySetResult();

        public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (!IsAvailable)
            {
                throw new InvalidOperationException("Runtime unavailable.");
            }
            if (blockDashboard && ReferenceEquals(operation, AgentRuntimeOperations.Dashboard))
            {
                Interlocked.Increment(ref _dashboardInvocationCount);
                await _dashboardRelease.Task.WaitAsync(cancellationToken);
            }
            else if (ReferenceEquals(operation, AgentRuntimeOperations.Dashboard))
            {
                Interlocked.Increment(ref _dashboardInvocationCount);
            }
            if (ReferenceEquals(operation, AgentRuntimeOperations.Dashboard))
            {
                return (TResponse)(object)new AgentDashboardProjection(0, [], [], []);
            }
            throw new NotSupportedException(operation.OperationId);
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            if (!IsAvailable)
            {
                throw new InvalidOperationException("Runtime unavailable.");
            }
            yield return (TEvent)(object)new AgentRuntimeChange(0, AgentRuntimeChangeKind.Connected);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class UnavailableThenReadyChatRuntimeClient(
        AgentChatSnapshotProjection snapshot) : IPackageRuntimeClient
    {
        private int _isAvailable;
        private int _successfulSnapshotInvocationCount;

        public bool IsAvailable => Volatile.Read(ref _isAvailable) != 0;

        public int SuccessfulSnapshotInvocationCount
            => Volatile.Read(ref _successfulSnapshotInvocationCount);

        public void MakeReady() => Volatile.Write(ref _isAvailable, 1);

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAvailable)
            {
                throw new InvalidOperationException("Runtime unavailable.");
            }
            if (!ReferenceEquals(operation, AgentRuntimeOperations.ChatSnapshot))
            {
                throw new NotSupportedException(operation.OperationId);
            }

            Interlocked.Increment(ref _successfulSnapshotInvocationCount);
            return ValueTask.FromResult((TResponse)(object)snapshot);
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            if (!IsAvailable)
            {
                throw new InvalidOperationException("Runtime unavailable.");
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

        public IReadOnlyList<AgentBehaviorLoopDescriptor> ListBehaviorLoopDescriptors() => [];

        public IReadOnlyList<AgentProviderDescriptor> ListChatProviderDescriptors() => [];

        public IReadOnlyList<AgentEmbeddingProviderDescriptor> ListEmbeddingProviderDescriptors() => [];

        public bool HasProfileCapabilityConsumers(string capabilityKind) => false;

        public Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListSelectableProfileCapabilitiesAsync(
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
    }

    private sealed class PermanentChatStartupFailureRuntimeClient : IPackageRuntimeClient
    {
        private int _chatSnapshotInvocationCount;

        public bool IsAvailable => true;

        public int ChatSnapshotInvocationCount => Volatile.Read(ref _chatSnapshotInvocationCount);

        public TaskCompletionSource ChangeStreamConnected { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(operation, AgentRuntimeOperations.ChatSnapshot))
            {
                Interlocked.Increment(ref _chatSnapshotInvocationCount);
                return ValueTask.FromException<TResponse>(new PackageRuntimeInvocationException(
                    "runtime.v1.validation",
                    isTransient: false,
                    statusCode: 400));
            }
            if (ReferenceEquals(operation, AgentRuntimeOperations.Dashboard))
            {
                return ValueTask.FromResult((TResponse)(object)new AgentDashboardProjection(0, [], [], []));
            }

            throw new NotSupportedException(operation.OperationId);
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
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

    private sealed class CapturingRunRuntimeClient : IPackageRuntimeClient
    {
        public bool IsAvailable => true;

        public ConcurrentQueue<AgentRunCommand> Commands { get; } = new();

        public AgentRunCommandStatusRequest? LastStatusRequest { get; private set; }

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(operation, AgentRuntimeOperations.RunStatus))
            {
                LastStatusRequest = (AgentRunCommandStatusRequest)(object)request;
                return ValueTask.FromResult((TResponse)(object)new AgentRunCommandStatusResult(
                    1,
                    AgentRunCommandStatus.Pending));
            }
            if (!ReferenceEquals(operation, AgentRuntimeOperations.Runs))
            {
                throw new NotSupportedException(operation.OperationId);
            }

            var command = (AgentRunCommand)(object)request;
            Commands.Enqueue(command);
            var checkpoint = new AgentRunCheckpointRecord(
                Guid.NewGuid(),
                command.SessionId,
                1,
                AgentRunStatus.Running,
                "Running.",
                DateTimeOffset.UtcNow);
            return ValueTask.FromResult((TResponse)(object)new AgentRunCommandResult(1, checkpoint));
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class FaultAfterCanceledDashboardRuntimeClient : IPackageRuntimeClient
    {
        private int _dashboardInvocationCount;

        public bool IsAvailable => true;
        public int DashboardInvocationCount => Volatile.Read(ref _dashboardInvocationCount);
        public TaskCompletionSource DashboardStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDashboard { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstDashboardCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (!ReferenceEquals(operation, AgentRuntimeOperations.Dashboard))
            {
                throw new NotSupportedException(operation.OperationId);
            }

            var invocation = Interlocked.Increment(ref _dashboardInvocationCount);
            if (invocation == 1)
            {
                DashboardStarted.TrySetResult();
                try
                {
                    await ReleaseDashboard.Task.WaitAsync(cancellationToken);
                    throw new InvalidOperationException("Injected dashboard failure.");
                }
                finally
                {
                    FirstDashboardCompleted.TrySetResult();
                }
            }

            return (TResponse)(object)new AgentDashboardProjection(0, [], [], []);
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class BlockingChatSnapshotRuntimeClient(AgentChatSnapshotProjection snapshot)
        : IPackageRuntimeClient
    {
        private readonly TaskCompletionSource _snapshotRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _snapshotInvocationCount;

        public bool IsAvailable => true;
        public TaskCompletionSource SnapshotStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<long> AfterRevisions { get; } = new();
        public int SnapshotInvocationCount => Volatile.Read(ref _snapshotInvocationCount);

        public void ReleaseSnapshot() => _snapshotRelease.TrySetResult();

        public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (ReferenceEquals(operation, AgentRuntimeOperations.ChatSnapshot))
            {
                Interlocked.Increment(ref _snapshotInvocationCount);
                SnapshotStarted.TrySetResult();
                await _snapshotRelease.Task.WaitAsync(cancellationToken);
                return (TResponse)(object)snapshot;
            }

            throw new NotSupportedException(operation.OperationId);
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            var subscription = (AgentChangeSubscription)(object)request;
            AfterRevisions.Enqueue(subscription.AfterRevision);
            if (subscription.AfterRevision == snapshot.Revision
                && snapshot.SelectedSession is { } selectedSession)
            {
                var turnId = Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;
                var turn = new AgentTurnRecord(
                    turnId,
                    selectedSession.Session.SessionId,
                    AgentMessageRole.Assistant,
                    AgentTurnKind.Message,
                    [new AgentTurnItemRecord(
                        Guid.NewGuid(),
                        turnId,
                        0,
                        AgentTurnItemKind.Text,
                        "Live after snapshot.",
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
                yield return (TEvent)(object)new AgentRuntimeChange(
                    snapshot.Revision + 1,
                    AgentRuntimeChangeKind.Turn,
                    SessionId: selectedSession.Session.SessionId,
                    Turn: turn);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class RacingChatSnapshotRuntimeClient(
        AgentChatSnapshotProjection first,
        AgentChatSnapshotProjection second)
        : IPackageRuntimeClient
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

        public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (!ReferenceEquals(operation, AgentRuntimeOperations.ChatSnapshot))
            {
                throw new NotSupportedException(operation.OperationId);
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

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class ResnapshotChatRuntimeClient(
        AgentChatSnapshotProjection initial,
        AgentChatSnapshotProjection refreshed)
        : IPackageRuntimeClient
    {
        private int _chatSnapshotInvocationCount;
        private int _dashboardInvocationCount;
        private int _resetSent;

        public bool IsAvailable => true;
        public ConcurrentQueue<AgentChatSnapshotRequest> ChatSnapshotRequests { get; } = new();
        public int DashboardInvocationCount => Volatile.Read(ref _dashboardInvocationCount);

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(operation, AgentRuntimeOperations.ChatSnapshot))
            {
                ChatSnapshotRequests.Enqueue((AgentChatSnapshotRequest)(object)request);
                var response = Interlocked.Increment(ref _chatSnapshotInvocationCount) == 1
                    ? initial
                    : refreshed;
                return ValueTask.FromResult((TResponse)(object)response);
            }
            if (ReferenceEquals(operation, AgentRuntimeOperations.Dashboard))
            {
                Interlocked.Increment(ref _dashboardInvocationCount);
                return ValueTask.FromResult((TResponse)(object)new AgentDashboardProjection(0, [], [], []));
            }
            return ValueTask.FromException<TResponse>(new NotSupportedException(operation.OperationId));
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            var subscription = (AgentChangeSubscription)(object)request;
            if (subscription.AfterRevision == initial.Revision
                && Interlocked.Exchange(ref _resetSent, 1) == 0)
            {
                yield return (TEvent)(object)new AgentRuntimeChange(
                    refreshed.Revision,
                    AgentRuntimeChangeKind.ResnapshotRequired);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class StaticChatSnapshotRuntimeClient(AgentChatSnapshotProjection snapshot)
        : IPackageRuntimeClient
    {
        public bool IsAvailable => true;
        public ConcurrentQueue<AgentChatSnapshotRequest> ChatSnapshotRequests { get; } = new();

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(operation, AgentRuntimeOperations.ChatSnapshot))
            {
                ChatSnapshotRequests.Enqueue((AgentChatSnapshotRequest)(object)request);
            }
            return ReferenceEquals(operation, AgentRuntimeOperations.ChatSnapshot)
                ? ValueTask.FromResult((TResponse)(object)snapshot)
                : ValueTask.FromException<TResponse>(new NotSupportedException(operation.OperationId));
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
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
        : IPackageRuntimeClient
    {
        public bool IsAvailable => true;
        public ConcurrentQueue<AgentChatSnapshotRequest> Requests { get; } = new();

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(operation, AgentRuntimeOperations.ChatSnapshot))
            {
                return ValueTask.FromException<TResponse>(new NotSupportedException(operation.OperationId));
            }

            var chatRequest = (AgentChatSnapshotRequest)(object)request;
            Requests.Enqueue(chatRequest);
            var snapshot = chatRequest.PreferredSessionId == second.SelectedSession?.Session.SessionId
                ? second
                : first;
            return ValueTask.FromResult((TResponse)(object)snapshot);
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class OrderedResnapshotRuntimeClient(AgentChatSnapshotProjection[] snapshots)
        : IPackageRuntimeClient
    {
        private readonly TaskCompletionSource _oldResnapshotRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocationCount;
        private int _oldResetSent;
        private int _newResetSent;

        public bool IsAvailable => true;
        public TaskCompletionSource OldResnapshotStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NewReplayResetSent { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseOldResnapshot() => _oldResnapshotRelease.TrySetResult();

        public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (!ReferenceEquals(operation, AgentRuntimeOperations.ChatSnapshot))
            {
                throw new NotSupportedException(operation.OperationId);
            }

            var invocation = Interlocked.Increment(ref _invocationCount);
            if (invocation == 2)
            {
                OldResnapshotStarted.TrySetResult();
                await _oldResnapshotRelease.Task.ConfigureAwait(false);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return (TResponse)(object)snapshots[invocation - 1];
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            var afterRevision = ((AgentChangeSubscription)(object)request).AfterRevision;
            if (afterRevision == snapshots[0].Revision
                && Interlocked.Exchange(ref _oldResetSent, 1) == 0)
            {
                yield return (TEvent)(object)new AgentRuntimeChange(
                    snapshots[1].Revision,
                    AgentRuntimeChangeKind.ResnapshotRequired);
            }
            else if (afterRevision == snapshots[2].Revision
                     && Interlocked.Exchange(ref _newResetSent, 1) == 0)
            {
                NewReplayResetSent.TrySetResult();
                yield return (TEvent)(object)new AgentRuntimeChange(
                    snapshots[3].Revision,
                    AgentRuntimeChangeKind.ResnapshotRequired);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class DisconnectingRuntimeClient : IPackageRuntimeClient
    {
        private int _subscriptionCount;
        private long _lastDeliveredRevision;

        public bool IsAvailable => true;
        public List<long> AfterRevisions { get; } = [];
        public long LastDeliveredRevision => Interlocked.Read(ref _lastDeliveredRevision);

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (ReferenceEquals(operation, AgentRuntimeOperations.Dashboard))
            {
                return ValueTask.FromResult((TResponse)(object)new AgentDashboardProjection(0, [], [], []));
            }
            return ValueTask.FromException<TResponse>(new NotSupportedException(operation.OperationId));
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            var afterRevision = ((AgentChangeSubscription)(object)request).AfterRevision;
            AfterRevisions.Add(afterRevision);
            if (Interlocked.Increment(ref _subscriptionCount) == 1)
            {
                yield return (TEvent)(object)new AgentRuntimeChange(0, AgentRuntimeChangeKind.Connected);
                yield break;
            }

            for (var revision = afterRevision + 1; revision <= 2; revision++)
            {
                Interlocked.Exchange(ref _lastDeliveredRevision, revision);
                yield return (TEvent)(object)new AgentRuntimeChange(revision, AgentRuntimeChangeKind.Workspace);
            }
            yield return (TEvent)(object)new AgentRuntimeChange(2, AgentRuntimeChangeKind.Connected);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class PermissionRuntimeClient : IPackageRuntimeClient
    {
        private int _subscriptionCount;

        public bool IsAvailable => true;

        public int SubscriptionCount => Volatile.Read(ref _subscriptionCount);

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(operation, AgentRuntimeOperations.Dashboard))
            {
                return ValueTask.FromResult((TResponse)(object)new AgentDashboardProjection(0, [], [], []));
            }
            if (ReferenceEquals(operation, AgentRuntimeOperations.Permissions))
            {
                return ValueTask.FromResult((TResponse)(object)new AgentPermissionProjection(
                    0,
                    SessionState: null,
                    [new AgentPermissionActionDescriptor(
                        "test.mutate",
                        "Mutate test state",
                        "Mutates deterministic test state.",
                        [new AgentPermissionBoundaryDescriptor(
                            "test-boundary",
                            "Test boundary",
                            "Requires approval.",
                            AgentPermissionDecision.Ask)])],
                    Overrides: [],
                    PendingRequests: []));
            }

            return ValueTask.FromException<TResponse>(new NotSupportedException(operation.OperationId));
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            Interlocked.Increment(ref _subscriptionCount);
            yield return (TEvent)(object)new AgentRuntimeChange(0, AgentRuntimeChangeKind.Connected);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class CountingExtensionCatalog : RegressionTestExtensionCatalog
    {
        public int InvocationCount => DiscoveryCount;
    }
}
