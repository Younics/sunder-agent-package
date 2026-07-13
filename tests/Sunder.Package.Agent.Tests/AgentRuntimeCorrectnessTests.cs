using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRuntimeCorrectnessTests
{
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
                return (TResponse)(object)new AgentDashboardProjection(0, [], [], [], [], [], []);
            }
            if (ReferenceEquals(operation, AgentRuntimeOperations.Sessions))
            {
                return (TResponse)(object)new AgentSessionPage(0, [], 0, false);
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
                return ValueTask.FromResult((TResponse)(object)new AgentDashboardProjection(0, [], [], [], [], [], []));
            }
            if (ReferenceEquals(operation, AgentRuntimeOperations.Sessions))
            {
                return ValueTask.FromResult((TResponse)(object)new AgentSessionPage(0, [], 0, false));
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
}
