extern alias AgentCore;

using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
using Xunit;
using CorePresentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRuntimeCorrectnessTests
{
    [Fact]
    public async Task ChatSnapshotHandler_ResolvesSelectionsWithoutPersistingBeforeUiApply()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store, catalog);
        var workspaces = new AgentWorkspaceService(store, catalog, sessions);
        var executionTargets = new AgentExecutionTargetService(catalog);
        var tools = new AgentToolService(
            new InstalledPackageToolSource(catalog),
            sessions,
            workspaces,
            executionTargets,
            catalog);
        using var profiles = new AgentProfileService(store, tools, catalog);
        var permissions = new AgentPermissionService(store, new ThrowingPermissionCatalog());
        using var changes = new AgentRuntimeChangeHub(profiles, workspaces, sessions);
        var selections = new AgentChatSelectionStateService(scope.Context);
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
        sessions.AppendTextTurn(
            rootSession.SessionId,
            AgentMessageRole.Assistant,
            "Initial transcript.");
        permissions.SetSessionUnrestrictedMode(rootSession.SessionId, true);
        await selections.SaveSelectedProfileIdAsync("missing-profile");
        await selections.SaveSelectedWorkspaceIdAsync("missing-workspace");
        await selections.SaveSelectedSessionIdAsync(workspace.WorkspaceId, Guid.NewGuid());
        var handler = new AgentChatSnapshotHandler(
            store,
            selections,
            changes);

        var snapshot = await handler.HandleAsync(new AgentChatSnapshotRequest(
            PreferredProfileId: "missing-preferred-profile",
            PreferredWorkspaceId: "missing-preferred-workspace",
            PreferredSessionId: Guid.NewGuid()));

        Assert.Equal(changes.Revision, snapshot.Revision);
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
        Assert.Equal("Initial transcript.", Assert.Single(snapshot.InitialTranscript.Turns).Items[0].TextContent);
        Assert.Equal("missing-profile", await selections.GetSelectedProfileIdAsync());
        Assert.Equal("missing-workspace", await selections.GetSelectedWorkspaceIdAsync());
        Assert.NotEqual(
            rootSession.SessionId,
            await selections.GetSelectedSessionIdAsync(workspace.WorkspaceId));
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
            new InstalledPackageToolSource(catalog),
            sessions,
            workspaces,
            executionTargets,
            catalog);
        using var profiles = new AgentProfileService(store, tools, catalog);
        var permissions = new AgentPermissionService(store, catalog);
        using var changes = new AgentRuntimeChangeHub(profiles, workspaces, sessions);
        var selections = new AgentChatSelectionStateService(scope.Context);
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
        await selections.SaveSelectedProfileIdAsync(storedProfile.ProfileId);
        await selections.SaveSelectedWorkspaceIdAsync(storedWorkspace.WorkspaceId);
        await selections.SaveSelectedSessionIdAsync(storedWorkspace.WorkspaceId, storedSession.SessionId);
        var handler = new AgentChatSnapshotHandler(
            store,
            selections,
            changes);

        var snapshot = await handler.HandleAsync(new AgentChatSnapshotRequest(
            PreferredProfileId: preferredProfile.ProfileId,
            PreferredWorkspaceId: preferredWorkspace.WorkspaceId,
            PreferredSessionId: preferredSession.SessionId));

        Assert.Equal(preferredProfile.ProfileId, snapshot.SelectedProfile?.ProfileId);
        Assert.Equal(preferredWorkspace.WorkspaceId, snapshot.SelectedWorkspace?.WorkspaceId);
        Assert.Equal(preferredSession.SessionId, snapshot.SelectedSession?.Session.SessionId);
        Assert.Equal(storedProfile.ProfileId, await selections.GetSelectedProfileIdAsync());
        Assert.Equal(storedWorkspace.WorkspaceId, await selections.GetSelectedWorkspaceIdAsync());
        Assert.Equal(
            storedSession.SessionId,
            await selections.GetSelectedSessionIdAsync(storedWorkspace.WorkspaceId));
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
            new InstalledPackageToolSource(catalog),
            sessions,
            workspaces,
            executionTargets,
            catalog);
        using var profiles = new AgentProfileService(store, tools, catalog);
        using var changes = new AgentRuntimeChangeHub(profiles, workspaces, sessions);
        var selections = new AgentChatSelectionStateService(scope.Context);
        var profile = await profiles.CreateProfileAsync("Cached profile");
        var workspace = workspaces.CreateWorkspace("Cached workspace");
        var session = sessions.CreateSession(
            "Cached session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        await selections.SaveSelectedProfileIdAsync(profile.ProfileId);
        await selections.SaveSelectedWorkspaceIdAsync(workspace.WorkspaceId);
        await selections.SaveSelectedSessionIdAsync(workspace.WorkspaceId, session.SessionId);
        var handler = new AgentChatSnapshotHandler(store, selections, changes);
        var request = new AgentChatSnapshotRequest();

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
    public async Task ChatSnapshotHandler_InvalidatesWhenStoredSessionSelectionChanges()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store, catalog);
        var workspaces = new AgentWorkspaceService(store, catalog, sessions);
        var executionTargets = new AgentExecutionTargetService(catalog);
        var tools = new AgentToolService(
            new InstalledPackageToolSource(catalog),
            sessions,
            workspaces,
            executionTargets,
            catalog);
        using var profiles = new AgentProfileService(store, tools, catalog);
        using var changes = new AgentRuntimeChangeHub(profiles, workspaces, sessions);
        var selections = new AgentChatSelectionStateService(scope.Context);
        var workspace = workspaces.CreateWorkspace("Selection workspace");
        var firstSession = sessions.CreateSession("First session", workspaceId: workspace.WorkspaceId);
        var secondSession = sessions.CreateSession("Second session", workspaceId: workspace.WorkspaceId);
        await selections.SaveSelectedWorkspaceIdAsync(workspace.WorkspaceId);
        await selections.SaveSelectedSessionIdAsync(workspace.WorkspaceId, firstSession.SessionId);
        var handler = new AgentChatSnapshotHandler(store, selections, changes);
        var request = new AgentChatSnapshotRequest();

        var first = await handler.HandleAsync(request);
        await selections.SaveSelectedSessionIdAsync(workspace.WorkspaceId, secondSession.SessionId);
        var second = await handler.HandleAsync(request);

        Assert.NotSame(first, second);
        Assert.Equal(firstSession.SessionId, first.SelectedSession?.Session.SessionId);
        Assert.Equal(secondSession.SessionId, second.SelectedSession?.Session.SessionId);
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
        var selections = new AgentChatSelectionStateService(scope.Context);
        await selections.SaveSelectedWorkspaceIdAsync(workspace.WorkspaceId);
        var handler = new AgentChatSnapshotHandler(runtime.Store, selections, changes);
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
            new AgentChatSelectionStateService(scope.Context),
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
            new AgentChatSelectionStateService(scope.Context),
            changes);

        var snapshot = await handler.HandleAsync(new AgentChatSnapshotRequest(
            InitialTranscriptLimit: 500,
            PreferredWorkspaceId: workspace.WorkspaceId,
            PreferredSessionId: session.SessionId));
        var roundTrip = AgentChatSnapshotPayload.RoundTrip(snapshot);

        Assert.True(snapshot.InitialTranscript.HasMore);
        Assert.InRange(snapshot.InitialTranscript.Turns.Count, 1, 179);
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
            new AgentChatSelectionStateService(scope.Context),
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
    }

    [Fact]
    public async Task Gateway_DisconnectReplaysMissedChangesFromLastRevision()
    {
        var client = new DisconnectingRuntimeClient();
        using var gateway = new AgentAppRuntimeGateway(client);
        var workspaceChanges = 0;
        gateway.WorkspacesChanged += () => Interlocked.Increment(ref workspaceChanges);

        await WaitUntilAsync(() => client.LastDeliveredRevision == 2);

        Assert.True(client.AfterRevisions.Count >= 2);
        Assert.Equal(0, client.AfterRevisions[0]);
        Assert.Equal(0, client.AfterRevisions[1]);
        Assert.Equal(2, client.LastDeliveredRevision);
        Assert.True(Volatile.Read(ref workspaceChanges) >= 2);
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
        IPackageExtensionCatalog? catalog = null)
    {
        catalog ??= new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(context);
        var sessions = new AgentSessionService(store, catalog);
        var workspaces = new AgentWorkspaceService(store, catalog, sessions);
        var executionTargets = new AgentExecutionTargetService(catalog);
        var tools = new AgentToolService(
            new InstalledPackageToolSource(catalog),
            sessions,
            workspaces,
            executionTargets,
            catalog);
        var profiles = new AgentProfileService(store, tools, catalog);
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

    private sealed class ChangeHubRuntime : IDisposable
    {
        private readonly RegressionTestPackageScope _scope;

        private ChangeHubRuntime(
            RegressionTestPackageScope scope,
            AgentWorkspaceService workspaces,
            AgentRuntimeChangeHub hub)
        {
            _scope = scope;
            Workspaces = workspaces;
            Hub = hub;
        }

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
                new InstalledPackageToolSource(catalog),
                sessions,
                workspaces,
                executionTargets,
                catalog);
            var profiles = new AgentProfileService(store, tools, catalog);
            return new ChangeHubRuntime(scope, workspaces, new AgentRuntimeChangeHub(profiles, workspaces, sessions));
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

        public bool IsAvailable
        {
            get => _isAvailable;
            set => _isAvailable = value;
        }

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
                await _dashboardRelease.Task.WaitAsync(cancellationToken);
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

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class ThrowingPermissionCatalog : IPackageExtensionCatalog
    {
        public IReadOnlyList<TContract> GetExtensions<TContract>(
            PackageExtensionPoint<TContract> extensionPoint)
            => throw new InvalidOperationException("Global permission actions must not be read for Chat snapshots.");

        public IReadOnlyList<PackageExtensionContribution<TContract>> GetExtensionContributions<TContract>(
            PackageExtensionPoint<TContract> extensionPoint)
            => throw new InvalidOperationException("Global permission actions must not be read for Chat snapshots.");
    }

    private sealed class CountingExtensionCatalog : IPackageExtensionCatalog
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public IReadOnlyList<TContract> GetExtensions<TContract>(
            PackageExtensionPoint<TContract> extensionPoint)
        {
            Interlocked.Increment(ref _invocationCount);
            return [];
        }

        public IReadOnlyList<PackageExtensionContribution<TContract>> GetExtensionContributions<TContract>(
            PackageExtensionPoint<TContract> extensionPoint)
        {
            Interlocked.Increment(ref _invocationCount);
            return [];
        }
    }
}
