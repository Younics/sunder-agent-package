extern alias AgentCore;

using AgentCore::Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;
using AgentAppRuntimePayloadLimits = AgentCore::Sunder.Package.Agent.Runtime.AgentRuntimePayloadLimits;
using AgentChatSnapshotHandler = Sunder.Package.Agent.Runtime.AgentChatSnapshotHandler;
using AgentRuntimeChangeHub = Sunder.Package.Agent.Runtime.AgentRuntimeChangeHub;
using AgentRuntimeTransportBudgetState = Sunder.Package.Agent.Runtime.AgentRuntimeTransportBudgetState;
using WorkerModels = Sunder.Package.Agent.Contracts.Models;
using WorkerRuntime = Sunder.Package.Agent.Runtime;
using static Sunder.Package.Agent.Tests.RuntimeSnapshotFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Runtime correctness")]
public sealed class ChatSnapshotHandlerTests
{
    [Fact]
    public async Task ChatSnapshotHandler_ResolvesExplicitSelectionsWithoutReadingAppState()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store, catalog.SessionCleaners);
        var workspaces = new AgentWorkspaceService(store, catalog.SessionCleaners, sessions);
        var executionTargets = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            executionTargets,
            catalog.ToolSources,
            catalog.ObservedToolExecutions);
        using var profiles = catalog.CreateProfileService(store, tools);
        var permissions = new AgentPermissionService(store, catalog.PermissionSurfaces);
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
            WorkerModels.AgentRunStatus.Completed,
            "Snapshot complete.");
        var transcriptContent = new string('x', 10_000);
        sessions.AppendTextTurn(
            rootSession.SessionId,
            WorkerModels.AgentMessageRole.Assistant,
            transcriptContent);
        permissions.SetSessionUnrestrictedMode(rootSession.SessionId, true);
        var handler = new AgentChatSnapshotHandler(store, changes);

        var snapshot = await handler.HandleAsync(new WorkerRuntime.AgentChatSnapshotRequest(
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
        var sessions = new AgentSessionService(store, catalog.SessionCleaners);
        var workspaces = new AgentWorkspaceService(store, catalog.SessionCleaners, sessions);
        var executionTargets = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            executionTargets,
            catalog.ToolSources,
            catalog.ObservedToolExecutions);
        using var profiles = catalog.CreateProfileService(store, tools);
        var permissions = new AgentPermissionService(store, catalog.PermissionSurfaces);
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

        var snapshot = await handler.HandleAsync(new WorkerRuntime.AgentChatSnapshotRequest(
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
        var sessions = new AgentSessionService(store, catalog.SessionCleaners);
        var workspaces = new AgentWorkspaceService(store, catalog.SessionCleaners, sessions);
        var executionTargets = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            executionTargets,
            catalog.ToolSources,
            catalog.ObservedToolExecutions);
        using var profiles = catalog.CreateProfileService(store, tools);
        using var changes = new AgentRuntimeChangeHub(profiles, workspaces, sessions);
        var profile = await profiles.CreateProfileAsync("Cached profile");
        var workspace = workspaces.CreateWorkspace("Cached workspace");
        var session = sessions.CreateSession(
            "Cached session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var handler = new AgentChatSnapshotHandler(store, changes);
        var request = new WorkerRuntime.AgentChatSnapshotRequest(
            PreferredProfileId: profile.ProfileId,
            PreferredWorkspaceId: workspace.WorkspaceId,
            PreferredSessionId: session.SessionId);

        var first = await handler.HandleAsync(request);
        var cached = await handler.HandleAsync(request);
        sessions.AppendTextTurn(session.SessionId, WorkerModels.AgentMessageRole.User, "Invalidate the cache.");
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
        var sessions = new AgentSessionService(store, catalog.SessionCleaners);
        var workspaces = new AgentWorkspaceService(store, catalog.SessionCleaners, sessions);
        var executionTargets = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            executionTargets,
            catalog.ToolSources,
            catalog.ObservedToolExecutions);
        using var profiles = catalog.CreateProfileService(store, tools);
        using var changes = new AgentRuntimeChangeHub(profiles, workspaces, sessions);
        var workspace = workspaces.CreateWorkspace("Selection workspace");
        var firstSession = sessions.CreateSession("First session", workspaceId: workspace.WorkspaceId);
        var secondSession = sessions.CreateSession("Second session", workspaceId: workspace.WorkspaceId);
        var handler = new AgentChatSnapshotHandler(store, changes);
        var firstRequest = new WorkerRuntime.AgentChatSnapshotRequest(
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
            var snapshot = await handler.HandleAsync(new WorkerRuntime.AgentChatSnapshotRequest());

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
            WorkerModels.AgentRunStatus.Running,
            "Running now.");
        var handler = new AgentChatSnapshotHandler(
            runtime.Store,
            changes);

        var snapshot = await handler.HandleAsync(new WorkerRuntime.AgentChatSnapshotRequest(
            PreferredWorkspaceId: workspace.WorkspaceId,
            PreferredSessionId: session.SessionId));

        Assert.Equal(checkpoint, snapshot.SelectedSession?.Checkpoint);
        Assert.Equal(WorkerModels.AgentRunStatus.Running, snapshot.SelectedSession?.Checkpoint?.Status);
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

        var snapshot = await handler.HandleAsync(new WorkerRuntime.AgentChatSnapshotRequest(
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
            <= AgentAppRuntimePayloadLimits.GetResponsePayloadBytes(
                AgentAppRuntimePayloadLimits.StandardFallbackProfile));
        Assert.True(
            AgentChatSnapshotPayload.GetSerializedByteCount(snapshot)
            < AgentAppRuntimePayloadLimits.StandardFallbackProfile.ResponseValueBytes);
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
            WorkerModels.AgentMessageRole.Assistant,
            new string('x', 6 * 1024 * 1024));
        var transportBudgets = new AgentRuntimeTransportBudgetState();
        _ = transportBudgets.Negotiate(new WorkerRuntime.AgentTransportProfileRequest(
            1024 * 1024,
            1024 * 1024,
            1024 * 1024,
            64 * 1024 * 1024,
            RuntimeReported: true));
        var handler = new AgentChatSnapshotHandler(
            runtime.Store,
            changes,
            transportBudgets);

        var snapshot = await handler.HandleAsync(new WorkerRuntime.AgentChatSnapshotRequest(
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
            <= AgentAppRuntimePayloadLimits.GetResponsePayloadBytes(AgentRoleBoundary.As<AgentTransportProfile>(transportBudgets.Current)));
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

        _ = await handler.HandleAsync(new WorkerRuntime.AgentChatSnapshotRequest(
            PreferredWorkspaceId: workspace.WorkspaceId));

        Assert.Equal(baselineCalls, catalog.InvocationCount);
    }

    private static WorkerModels.AgentWorkspacePathRecord CreateWorkspacePath(
        string workspaceId,
        string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkerModels.AgentWorkspacePathRecord(
            Guid.NewGuid().ToString("N"),
            workspaceId,
            Path.Combine(Path.GetTempPath(), name),
            true,
            0,
            now,
            now);
    }

    private sealed class CountingExtensionCatalog : RegressionTestExtensionCatalog
    {
        public int InvocationCount => DiscoveryCount;
    }
}
