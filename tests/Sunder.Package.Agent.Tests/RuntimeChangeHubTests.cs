extern alias AgentCore;

using AgentCore::Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;
using AgentRuntimeChangeHub = Sunder.Package.Agent.Runtime.AgentRuntimeChangeHub;
using AgentRuntimeTransportBudgetState = Sunder.Package.Agent.Runtime.AgentRuntimeTransportBudgetState;
using WorkerModels = Sunder.Package.Agent.Contracts.Models;
using WorkerRuntime = Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.Tests;

[Collection("Runtime correctness")]
public sealed class RuntimeChangeHubTests
{
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
    public async Task ChangeHub_ReplaysAfterRevision()
    {
        using var runtime = ChangeHubRuntime.Create();
        runtime.Workspaces.CreateWorkspace("first");
        var firstRevision = runtime.Hub.Revision;
        runtime.Workspaces.CreateWorkspace("second");

        await using var subscription = runtime.Hub.SubscribeAsync(
            new WorkerRuntime.AgentChangeSubscription(firstRevision)).GetAsyncEnumerator();

        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(WorkerRuntime.AgentRuntimeChangeKind.Workspace, subscription.Current.Kind);
        Assert.Equal(firstRevision + 1, subscription.Current.Revision);
        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(WorkerRuntime.AgentRuntimeChangeKind.Connected, subscription.Current.Kind);
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
            new WorkerRuntime.AgentChangeSubscription(revision)).GetAsyncEnumerator();
        await using var current = runtime.Hub.SubscribeAsync(
            new WorkerRuntime.AgentChangeSubscription(revision, SupportsTurnMutations: true)).GetAsyncEnumerator();
        Assert.True(await legacy.MoveNextAsync());
        Assert.Equal(WorkerRuntime.AgentRuntimeChangeKind.Connected, legacy.Current.Kind);
        Assert.True(await current.MoveNextAsync());
        Assert.Equal(WorkerRuntime.AgentRuntimeChangeKind.Connected, current.Current.Kind);

        var turn = runtime.Sessions.AppendTextTurn(
            session.SessionId,
            WorkerModels.AgentMessageRole.Assistant,
            "stream compatibility");

        Assert.True(await legacy.MoveNextAsync());
        Assert.Equal(WorkerRuntime.AgentRuntimeChangeKind.Turn, legacy.Current.Kind);
        Assert.Equal(turn.TurnId, legacy.Current.Turn?.TurnId);
        Assert.Equal("stream compatibility", legacy.Current.Turn?.Items.Single().TextContent);
        Assert.Null(legacy.Current.TurnMutation);
        Assert.True(await current.MoveNextAsync());
        Assert.Equal(WorkerRuntime.AgentRuntimeChangeKind.TurnMutation, current.Current.Kind);
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
            new WorkerRuntime.AgentChangeSubscription(0)).GetAsyncEnumerator();

        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(WorkerRuntime.AgentRuntimeChangeKind.ResnapshotRequired, subscription.Current.Kind);
        Assert.Equal(runtime.Hub.Revision, subscription.Current.Revision);
    }

    [Fact]
    public async Task ChangeHub_SlowSubscriberIsBoundedAndReset()
    {
        using var runtime = ChangeHubRuntime.Create();
        await using var subscription = runtime.Hub.SubscribeAsync(
            new WorkerRuntime.AgentChangeSubscription(runtime.Hub.Revision)).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(WorkerRuntime.AgentRuntimeChangeKind.Connected, subscription.Current.Kind);

        for (var index = 0; index < 80; index++)
        {
            runtime.Workspaces.CreateWorkspace($"slow-{index}");
        }

        WorkerRuntime.AgentRuntimeChange? terminal = null;
        while (await subscription.MoveNextAsync())
        {
            terminal = subscription.Current;
        }

        Assert.NotNull(terminal);
        Assert.Equal(WorkerRuntime.AgentRuntimeChangeKind.ResnapshotRequired, terminal!.Kind);
        Assert.Equal(runtime.Hub.Revision, terminal.Revision);
    }

    [Fact]
    public async Task ChangeHub_SlowSubscriberIsByteBoundedAndReset()
    {
        var budgets = CreateSmallTransportBudgets();
        using var runtime = ChangeHubRuntime.Create(budgets);
        var workspace = runtime.Workspaces.CreateWorkspace("byte-bounded-subscriber");
        var session = runtime.Sessions.CreateSession(
            "byte-bounded-subscriber",
            workspaceId: workspace.WorkspaceId);
        await using var subscription = runtime.Hub.SubscribeAsync(
            new WorkerRuntime.AgentChangeSubscription(runtime.Hub.Revision, SupportsTurnMutations: true)).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(WorkerRuntime.AgentRuntimeChangeKind.Connected, subscription.Current.Kind);

        for (var index = 0; index < 3; index++)
        {
            runtime.Sessions.AppendTextTurn(
                session.SessionId,
                WorkerModels.AgentMessageRole.Assistant,
                new string((char)('a' + index), 750_000));
        }

        var deliveredChanges = 0;
        WorkerRuntime.AgentRuntimeChange? terminal = null;
        while (await subscription.MoveNextAsync())
        {
            terminal = subscription.Current;
            if (terminal.Kind == WorkerRuntime.AgentRuntimeChangeKind.TurnMutation)
            {
                deliveredChanges++;
            }
        }

        Assert.Equal(2, deliveredChanges);
        Assert.Equal(WorkerRuntime.AgentRuntimeChangeKind.ResnapshotRequired, terminal?.Kind);
        Assert.Equal(runtime.Hub.Revision, terminal?.Revision);
    }

    [Fact]
    public async Task ChangeHub_ReplayIsByteBoundedAndRequiresResnapshot()
    {
        var budgets = CreateSmallTransportBudgets();
        using var runtime = ChangeHubRuntime.Create(budgets);
        var workspace = runtime.Workspaces.CreateWorkspace("byte-bounded-replay");
        var session = runtime.Sessions.CreateSession(
            "byte-bounded-replay",
            workspaceId: workspace.WorkspaceId);
        var beforeLargeChanges = runtime.Hub.Revision;

        for (var index = 0; index < 6; index++)
        {
            runtime.Sessions.AppendTextTurn(
                session.SessionId,
                WorkerModels.AgentMessageRole.Assistant,
                new string((char)('a' + index), 750_000));
        }

        await using var subscription = runtime.Hub.SubscribeAsync(
            new WorkerRuntime.AgentChangeSubscription(beforeLargeChanges)).GetAsyncEnumerator();

        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(WorkerRuntime.AgentRuntimeChangeKind.ResnapshotRequired, subscription.Current.Kind);
        Assert.Equal(runtime.Hub.Revision, subscription.Current.Revision);
    }

    private static AgentRuntimeTransportBudgetState CreateSmallTransportBudgets()
    {
        const int valueBytes = 1024 * 1024;
        var budgets = new AgentRuntimeTransportBudgetState();
        _ = budgets.Negotiate(new WorkerRuntime.AgentTransportProfileRequest(
            valueBytes,
            valueBytes,
            valueBytes,
            valueBytes,
            RuntimeReported: true));
        return budgets;
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

        public static ChangeHubRuntime Create(
            AgentRuntimeTransportBudgetState? transportBudgets = null)
        {
            var scope = RegressionTestPackageScope.Create();
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
            var profiles = catalog.CreateProfileService(store, tools);
            return new ChangeHubRuntime(
                scope,
                sessions,
                workspaces,
                new AgentRuntimeChangeHub(profiles, workspaces, sessions, transportBudgets));
        }

        public void Dispose()
        {
            Hub.Dispose();
            _scope.Dispose();
        }
    }
}
