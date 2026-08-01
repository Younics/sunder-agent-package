using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SubagentRuntimeChangeTests
{
    [Fact]
    public async Task ChangeStream_SlowSubscriberOverSixtyFourEventsRequiresResnapshot()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var service = new SubagentService(new SubagentStore(scope.Context));
        using var stream = new SubagentRuntimeChangeStream(
            service,
            catalog);
        await using var subscription = stream.SubscribeAsync(
            new SubagentChangeSubscription(stream.Revision)).GetAsyncEnumerator();
        Assert.Equal(
            SubagentChangeKind.Connected,
            (await ReadUntilAsync(subscription, change => change.Kind == SubagentChangeKind.Connected)).Kind);

        for (var index = 0; index < 80; index++)
        {
            service.NotifySubagentsImported();
        }

        SubagentChanged? terminal = null;
        while (await subscription.MoveNextAsync())
        {
            terminal = subscription.Current;
        }

        Assert.NotNull(terminal);
        Assert.Equal(SubagentChangeKind.ResnapshotRequired, terminal!.Kind);
        Assert.Equal(stream.Revision, terminal.Revision);
    }

    [Fact]
    public async Task ChangeStream_ReacquiresRuntimeCatalogPublishedAfterConstructionAndAfterRetirement()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var service = new SubagentService(new SubagentStore(scope.Context));
        using var stream = new SubagentRuntimeChangeStream(service, catalog);
        using var subscriptionCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = stream.SubscribeAsync(
            new SubagentChangeSubscription(stream.Revision),
            subscriptionCancellation.Token).GetAsyncEnumerator();
        Assert.Equal(
            SubagentChangeKind.Connected,
            (await ReadUntilAsync(subscription, change => change.Kind == SubagentChangeKind.Connected)).Kind);

        var firstRuntime = new TestRuntimeCatalog();
        catalog.AddProvider(AgentRpcServices.RuntimeCatalogs, firstRuntime);
        await firstRuntime.SessionSubscription.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var firstSessionId = Guid.NewGuid();
        firstRuntime.RaiseSessionChanged(firstSessionId);
        var firstChange = await ReadUntilAsync(
            subscription,
            change => change.Kind == SubagentChangeKind.Session
                      && change.SessionId == firstSessionId);
        Assert.Equal(firstSessionId, firstChange.SessionId);

        await catalog.RetireProviderAsync(firstRuntime);
        _ = await ReadUntilAsync(
            subscription,
            change => change.Kind == SubagentChangeKind.ResnapshotRequired);
        _ = await ReadUntilAsync(
            subscription,
            change => change.Kind == SubagentChangeKind.Catalog);
        var replacementRuntime = new TestRuntimeCatalog();
        catalog.AddProvider(AgentRpcServices.RuntimeCatalogs, replacementRuntime);
        await replacementRuntime.SessionSubscription.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _ = await ReadUntilAsync(
            subscription,
            change => change.Kind == SubagentChangeKind.ResnapshotRequired);
        _ = await ReadUntilAsync(
            subscription,
            change => change.Kind == SubagentChangeKind.Catalog);
        var replacementSessionId = Guid.NewGuid();
        replacementRuntime.RaiseSessionChanged(replacementSessionId);
        var replacementChange = await ReadUntilAsync(
            subscription,
            change => change.Kind == SubagentChangeKind.Session
                      && change.SessionId == replacementSessionId);
        Assert.Equal(replacementSessionId, replacementChange.SessionId);
    }

    [Fact]
    public async Task AppGateway_ReconnectsFromLastRevisionAndNewRuntimeRequiresResnapshot()
    {
        var client = new ReconnectingSubagentRuntimeClient();
        using var gateway = new SubagentAppRuntimeGateway(client);
        var sessionChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.SessionChanged += _ => sessionChanged.TrySetResult();
        gateway.ResnapshotRequired += () => resnapshot.TrySetResult();

        await gateway.InitializeAsync();
        await sessionChanged.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await client.SecondSubscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await resnapshot.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal([0, 6], client.AfterRevisions.ToArray());
    }

    [Fact]
    public async Task AppGateway_OverflowResnapshotResumesFromTerminalRevision()
    {
        var client = new OverflowResnapshotRuntimeClient();
        using var gateway = new SubagentAppRuntimeGateway(client);
        var resnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ResnapshotRequired += () => resnapshot.TrySetResult();
        gateway.TurnChanged += (_, _) => turnChanged.TrySetResult();

        await gateway.InitializeAsync();
        await resnapshot.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await turnChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([0, 65], client.AfterRevisions.ToArray());
    }

    [Fact]
    public async Task ResnapshotRequired_ReloadsSelectedTranscriptAcrossRowMoveAndAcceptsLaterChange()
    {
        var gateway = new ResnapshotSubsessionGateway();
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);
        await viewModel.InitializeAsync();
        Assert.Equal("initial", Assert.Single(
            viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>()).Content);
        var selectedRowMoveObserved = false;
        viewModel.Subsessions.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move)
            {
                selectedRowMoveObserved = true;
                viewModel.SelectedSubsession = null;
            }
        };

        gateway.RaiseResnapshotRequired();
        await WaitUntilAsync(() => gateway.RecentReadCount >= 2
                                   && viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>()
                                       .SingleOrDefault()?.Content == "resnapshot");

        Assert.Equal(gateway.ChildSessionId, viewModel.SelectedSubsession?.SessionId);
        Assert.True(selectedRowMoveObserved);
        Assert.Equal("resnapshot", Assert.Single(
            viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>()).Content);

        gateway.RaiseTurnChanged("later change");
        await WaitUntilAsync(() => viewModel.Messages
            .OfType<SubsessionTextTranscriptRowViewModel>()
            .Any(row => row.Content == "later change"));

        Assert.Equal(gateway.ChildSessionId, viewModel.SelectedSubsession?.SessionId);
    }

    [Fact]
    public async Task InitialTranscriptTransportFailureSurfacesActionableStatus()
    {
        using var gateway = new SubagentAppRuntimeGateway(new MissingPageSubagentRuntimeClient());
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);

        await viewModel.InitializeAsync();

        Assert.Contains("Unable to load subsession transcript", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("did not return the recent subsession transcript page", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Empty(viewModel.Messages);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The subsession runtime state did not settle in time.");
            await Task.Delay(10);
        }
    }

    private static async Task<SubagentChanged> ReadUntilAsync(
        IAsyncEnumerator<SubagentChanged> subscription,
        Func<SubagentChanged, bool> predicate)
    {
        while (await subscription.MoveNextAsync())
        {
            if (predicate(subscription.Current)) return subscription.Current;
        }

        throw new InvalidOperationException("The subagent change stream completed before the expected change.");
    }

    private sealed class ReconnectingSubagentRuntimeClient : IPackageRuntimeClient
    {
        private int _subscriptionCount;

        public bool IsAvailable => true;
        public ConcurrentQueue<long> AfterRevisions { get; } = new();
        public TaskCompletionSource SecondSubscriptionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
            => ValueTask.FromException<TResponse>(new NotSupportedException(operation.OperationId));

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            var subscription = (SubagentChangeSubscription)(object)request;
            AfterRevisions.Enqueue(subscription.AfterRevision);
            if (Interlocked.Increment(ref _subscriptionCount) == 1)
            {
                yield return (TEvent)(object)new SubagentChanged(
                    5,
                    SubagentChangeKind.Connected,
                    RuntimeInstanceId: "runtime-one");
                yield return (TEvent)(object)new SubagentChanged(
                    6,
                    SubagentChangeKind.Session,
                    SessionId: Guid.NewGuid(),
                    RuntimeInstanceId: "runtime-one");
                yield break;
            }

            SecondSubscriptionStarted.TrySetResult();
            yield return (TEvent)(object)new SubagentChanged(
                6,
                SubagentChangeKind.Connected,
                RuntimeInstanceId: "runtime-two");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class OverflowResnapshotRuntimeClient : IPackageRuntimeClient
    {
        private readonly Guid _sessionId = Guid.NewGuid();
        private int _subscriptionCount;

        public bool IsAvailable => true;
        public ConcurrentQueue<long> AfterRevisions { get; } = new();

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
            => ValueTask.FromException<TResponse>(new NotSupportedException(operation.OperationId));

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            var subscription = (SubagentChangeSubscription)(object)request;
            AfterRevisions.Enqueue(subscription.AfterRevision);
            if (Interlocked.Increment(ref _subscriptionCount) == 1)
            {
                yield return (TEvent)(object)new SubagentChanged(
                    1,
                    SubagentChangeKind.Connected,
                    RuntimeInstanceId: "runtime");
                yield return (TEvent)(object)new SubagentChanged(
                    65,
                    SubagentChangeKind.ResnapshotRequired,
                    RuntimeInstanceId: "runtime");
                yield break;
            }

            yield return (TEvent)(object)new SubagentChanged(
                65,
                SubagentChangeKind.Connected,
                RuntimeInstanceId: "runtime");
            yield return (TEvent)(object)new SubagentChanged(
                66,
                SubagentChangeKind.Turn,
                SessionId: _sessionId,
                Turn: CreateTurn(_sessionId, "after resnapshot"),
                RuntimeInstanceId: "runtime");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        private static AgentTurnRecord CreateTurn(Guid sessionId, string content)
        {
            var turnId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            return new AgentTurnRecord(
                turnId,
                sessionId,
                AgentMessageRole.Assistant,
                AgentTurnKind.Message,
                [new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    turnId,
                    0,
                    AgentTurnItemKind.Text,
                    content,
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
    }

    private sealed class ResnapshotSubsessionGateway :
        ISubsessionSessionReader,
        ISubsessionCheckpointReader,
        ISubsessionTranscriptPageReader,
        ISubsessionChangeNotifications
    {
        private readonly Guid _rootSessionId = Guid.NewGuid();
        private readonly Guid _otherChildSessionId = Guid.NewGuid();
        private int _catalogReadCount;
        private int _recentReadCount;

        internal Guid ChildSessionId { get; } = Guid.NewGuid();
        internal int RecentReadCount => Volatile.Read(ref _recentReadCount);

        public event Action<Guid>? SessionChanged
        {
            add { }
            remove { }
        }
        public event Action<Guid, AgentTurnRecord>? TurnChanged;
        public event Action? ResnapshotRequired;

        public Task<SubsessionSessionCatalog> ListSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var read = Interlocked.Increment(ref _catalogReadCount);
            var selectedUpdatedAt = read == 1 ? now : now.AddMinutes(-1);
            var otherUpdatedAt = read == 1 ? now.AddMinutes(-1) : now;
            return Task.FromResult(new SubsessionSessionCatalog(
            [
                new AgentSessionRecord(
                    _rootSessionId,
                    "Root",
                    AgentSessionState.Active,
                    now,
                    now,
                    RootSessionId: _rootSessionId),
                new AgentSessionRecord(
                    ChildSessionId,
                    "Child",
                    AgentSessionState.Active,
                    now,
                    selectedUpdatedAt,
                    ParentSessionId: _rootSessionId,
                    RootSessionId: _rootSessionId),
                new AgentSessionRecord(
                    _otherChildSessionId,
                    "Other child",
                    AgentSessionState.Active,
                    now,
                    otherUpdatedAt,
                    ParentSessionId: _rootSessionId,
                    RootSessionId: _rootSessionId),
            ],
            []));
        }

        public Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentRunCheckpointRecord>>([]);

        public Task<SubsessionTranscriptPage> ListRecentTurnsAsync(
            Guid sessionId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var read = Interlocked.Increment(ref _recentReadCount);
            return Task.FromResult(new SubsessionTranscriptPage(
                [CreateTurn(sessionId, read == 1 ? "initial" : "resnapshot")],
                false));
        }

        public Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(
            Guid sessionId,
            DateTimeOffset beforeCreatedAtUtc,
            Guid beforeTurnId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionTranscriptPage([], false));

        public Task<SubsessionTranscriptPage> ListTurnsAfterAsync(
            Guid sessionId,
            DateTimeOffset afterCreatedAtUtc,
            Guid afterTurnId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionTranscriptPage([], false));

        public Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(
            Guid sessionId,
            Guid turnId,
            DateTimeOffset turnCreatedAtUtc,
            Guid itemId,
            int beforeLimit,
            int afterLimit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SubsessionAroundTurnPage([], false, false, turnId));

        internal void RaiseResnapshotRequired() => ResnapshotRequired?.Invoke();

        internal void RaiseTurnChanged(string content)
            => TurnChanged?.Invoke(ChildSessionId, CreateTurn(ChildSessionId, content));

        private static AgentTurnRecord CreateTurn(Guid sessionId, string content)
        {
            var turnId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            return new AgentTurnRecord(
                turnId,
                sessionId,
                AgentMessageRole.Assistant,
                AgentTurnKind.Message,
                [new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    turnId,
                    0,
                    AgentTurnItemKind.Text,
                    content,
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
    }

    private sealed class TestRuntimeCatalog : IAgentRuntimeCatalog
    {
        private Action<Guid>? _sessionChanged;

        public TaskCompletionSource SessionSubscription { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<Guid>? SessionChanged
        {
            add
            {
                _sessionChanged += value;
                SessionSubscription.TrySetResult();
            }
            remove => _sessionChanged -= value;
        }

        public event Action<Guid, AgentTurnRecord>? TurnChanged
        {
            add { }
            remove { }
        }

        public event Action<string>? ProfileChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<AgentSessionRecord> ListSessions() => [];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId) => [];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId) => [];
        public AgentSessionRecord? GetSession(Guid sessionId) => null;
        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => [];
        public AgentWorkspaceRecord? GetWorkspace(string workspaceId) => null;
        public AgentProfileRecord? GetSessionProfile(Guid sessionId) => null;
        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;
        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => null;
        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;
        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(
            Guid sessionId,
            DateTimeOffset beforeCreatedAtUtc,
            Guid beforeTurnId,
            int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(
            Guid sessionId,
            DateTimeOffset afterCreatedAtUtc,
            Guid afterTurnId,
            int limit) => [];
        public IReadOnlyList<AgentProfileRecord> ListProfiles() => [];
        public AgentProfileRecord? GetProfile(string profileId) => null;
        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => null;
        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind) => null;

        public void RaiseSessionChanged(Guid sessionId) => _sessionChanged?.Invoke(sessionId);
    }

    private sealed class MissingPageSubagentRuntimeClient : IPackageRuntimeClient
    {
        private readonly Guid _rootSessionId = Guid.NewGuid();
        private readonly Guid _childSessionId = Guid.NewGuid();

        public bool IsAvailable => true;

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = (SubagentQuery)(object)request;
            object projection = query.Kind == SubagentQueryKind.RuntimeCatalog
                ? CreateRuntimeCatalog()
                : query.Kind == SubagentQueryKind.RecentTurns
                    ? new SubagentProjection()
                    : throw new NotSupportedException(query.Kind.ToString());
            return ValueTask.FromResult((TResponse)projection);
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

        private SubagentProjection CreateRuntimeCatalog()
        {
            var now = DateTimeOffset.UtcNow;
            return new SubagentProjection(
                Sessions:
                [
                    new AgentSessionRecord(
                        _rootSessionId,
                        "Root",
                        AgentSessionState.Active,
                        now,
                        now,
                        RootSessionId: _rootSessionId),
                    new AgentSessionRecord(
                        _childSessionId,
                        "Child",
                        AgentSessionState.Active,
                        now,
                        now,
                        ParentSessionId: _rootSessionId,
                        RootSessionId: _rootSessionId),
                ],
                Profiles: [],
                Checkpoints: []);
        }
    }
}
