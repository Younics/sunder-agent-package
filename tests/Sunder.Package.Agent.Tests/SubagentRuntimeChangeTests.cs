extern alias AgentWorker;
extern alias SubagentsWorker;
extern alias SubagentsApp;

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Services;
using Sunder.Sdk.Rpc;
using SubagentsApp::Sunder.Package.Agent.Subagents.PackageViews;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Services;
using AgentTranscriptToolDetailRequest = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTranscriptToolDetailRequest;
using AgentCompactTranscriptRequest = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentCompactTranscriptRequest;
using AgentTranscriptActivityMembersRequest = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTranscriptActivityMembersRequest;
using AppAgentRunCheckpointRecord = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentRunCheckpointRecord;
using AppAgentTurnRecord = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnRecord;
using AppAgentTurnItemKind = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnItemKind;
using AppAgentTurnItemRecord = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnItemRecord;
using AppAgentTurnKind = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnKind;
using AppAgentMessageRole = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentMessageRole;
using AppAgentSessionRecord = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionRecord;
using AppAgentSessionState = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionState;
using SubagentSaveRequest = SubagentsApp::Sunder.Package.Agent.Subagents.Runtime.SubagentSaveRequest;
using SubagentAppRuntimeGateway = SubagentsApp::Sunder.Package.Agent.Subagents.Runtime.SubagentAppRuntimeGateway;
using AppRuntime = SubagentsApp::Sunder.Package.Agent.Subagents.Runtime;
using SubagentManagementServices = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Subagents_Services;
using WorkerAgentRunCheckpointRecord = AgentWorker::Sunder.Package.Agent.Contracts.Models.AgentRunCheckpointRecord;
using WorkerAgentTurnRecord = AgentWorker::Sunder.Package.Agent.Contracts.Models.AgentTurnRecord;
using WorkerGeneratedAgentMessageRole = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentMessageRole;
using WorkerGeneratedAgentSessionRecord = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionRecord;
using WorkerGeneratedAgentSessionState = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionState;
using WorkerGeneratedAgentTurnItemKind = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnItemKind;
using WorkerGeneratedAgentTurnItemRecord = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnItemRecord;
using WorkerGeneratedAgentTurnKind = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnKind;
using WorkerGeneratedAgentTurnRecord = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnRecord;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SubagentRuntimeChangeTests
{
    private static long _nextTurnSequence;
    private static SunderRpcOffsetDateTime RpcTimestamp(DateTimeOffset value)
        => new(value.ToString("O", CultureInfo.InvariantCulture));

    [Fact]
    public async Task CommittedSubagentMutation_IsolatesThrowingListenerAndPublishesChange()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var service = new SubagentService(new SubagentStore(scope.Context));
        service.SubagentsChanged += static () => throw new InvalidOperationException("Injected listener failure.");
        await using var management = await catalog.CreateSubagentRuntimeManagementAsync(service);
        var stream = management.Changes;
        await using var subscription = stream.SubscribeAsync(stream.Revision).GetAsyncEnumerator();
        _ = await ReadUntilAsync(subscription, change => change.Kind == SubagentChangeKind.Connected);

        var created = service.CreateSubagent("Listener-isolated Subagent");
        var change = await ReadUntilAsync(
            subscription,
            item => item.Kind == SubagentChangeKind.Subagents);

        Assert.Equal(SubagentChangeKind.Subagents, change.Kind);
        Assert.Equal(created.SubagentId, Assert.Single(service.ListSubagents()).SubagentId);
    }

    [Fact]
    public void CommittedSubagentMutation_PropagatesWrappedFatalListenerFailure()
    {
        using var scope = RegressionTestPackageScope.Create();
        var service = new SubagentService(new SubagentStore(scope.Context));
        var fatal = new Exception(
            "Wrapped fatal listener failure.",
            new OutOfMemoryException("Injected fatal listener failure."));
        service.SubagentsChanged += () => throw fatal;

        var thrown = Assert.Throws<Exception>(() => service.CreateSubagent("Persisted before fatal listener"));

        Assert.Same(fatal, thrown);
        Assert.Single(service.ListSubagents());
    }

    [Fact]
    public async Task ChangeStream_SlowSubscriberOverSixtyFourEventsRequiresResnapshot()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var service = new SubagentService(new SubagentStore(scope.Context));
        await using var management = await catalog.CreateSubagentRuntimeManagementAsync(service);
        var stream = management.Changes;
        await using var subscription = stream.SubscribeAsync(stream.Revision).GetAsyncEnumerator();
        Assert.Equal(
            SubagentChangeKind.Connected,
            (await ReadUntilAsync(subscription, change => change.Kind == SubagentChangeKind.Connected)).Kind);

        for (var index = 0; index < 80; index++)
        {
            service.NotifySubagentsImported();
        }
        var burstRevision = stream.Revision;

        SubagentChanged? terminal = null;
        while (await subscription.MoveNextAsync())
        {
            terminal = subscription.Current;
        }

        Assert.NotNull(terminal);
        Assert.Equal(SubagentChangeKind.ResnapshotRequired, terminal!.Kind);
        // The terminal cursor covers the entire burst. Independent metadata may
        // arrive after it was yielded; that cannot retroactively advance it.
        Assert.InRange(terminal.Revision, burstRevision, stream.Revision);
    }

    [Fact]
    public async Task ChangeStream_ReacquiresRuntimeCatalogPublishedAfterConstructionAndAfterRetirement()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var service = new SubagentService(new SubagentStore(scope.Context));
        await using var management = await catalog.CreateSubagentRuntimeManagementAsync(service);
        var stream = management.Changes;
        using var subscriptionCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = stream.SubscribeAsync(
            stream.Revision,
            subscriptionCancellation.Token).GetAsyncEnumerator();
        Assert.Equal(
            SubagentChangeKind.Connected,
            (await ReadUntilAsync(subscription, change => change.Kind == SubagentChangeKind.Connected)).Kind);

        var firstRuntime = new TestRuntimeCatalog();
        catalog.AddRuntimeCatalog(firstRuntime);
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
        catalog.AddRuntimeCatalog(replacementRuntime);
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
    public async Task ChangeStream_RuntimeCatalogResetRequiresResnapshot()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var service = new SubagentService(new SubagentStore(scope.Context));
        await using var management = await catalog.CreateSubagentRuntimeManagementAsync(service);
        var stream = management.Changes;
        using var subscriptionCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = stream.SubscribeAsync(
            stream.Revision,
            subscriptionCancellation.Token).GetAsyncEnumerator();
        _ = await ReadUntilAsync(subscription, change => change.Kind == SubagentChangeKind.Connected);
        var runtime = new TestRuntimeCatalog();
        catalog.AddRuntimeCatalog(runtime);
        await runtime.SessionSubscription.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _ = await ReadUntilAsync(subscription, change => change.Kind == SubagentChangeKind.Catalog);

        runtime.RaiseResetRequired();

        var reset = await ReadUntilAsync(
            subscription,
            change => change.Kind == SubagentChangeKind.ResnapshotRequired);
        Assert.Equal(SubagentChangeKind.ResnapshotRequired, reset.Kind);
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
        gateway.TurnChanged += (_, _, _) => turnChanged.TrySetResult();

        await gateway.InitializeAsync();
        await resnapshot.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await turnChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([0, 65], client.AfterRevisions.ToArray());
    }

    [Fact]
    public async Task AppGateway_FailedChangeProcessingReconnectsBeforeAdvancingRevision()
    {
        var client = new FailingCatalogRefreshRuntimeClient();
        using var gateway = new SubagentAppRuntimeGateway(client);
        var catalogChanged = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.CatalogChanged += static () => throw new InvalidOperationException("Injected app listener failure.");
        gateway.CatalogChanged += () => catalogChanged.TrySetResult();

        await gateway.InitializeAsync();
        await catalogChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([0, 1], client.AfterRevisions.ToArray());
        Assert.Equal("provider.replayed", Assert.Single(
            await gateway.ListChatProvidersAsync()).ProviderId);
    }

    [Fact]
    public async Task AppGateway_MalformedChangeResnapshotsAndViewModelTracksAvailability()
    {
        var client = new MalformedChangeRuntimeClient();
        using var gateway = new SubagentAppRuntimeGateway(client);
        using var viewModel = new SubagentsViewModel(gateway);
        var resnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.ResnapshotRequired += () => resnapshot.TrySetResult();

        await gateway.InitializeAsync();
        await resnapshot.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await client.SecondSubscriptionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => viewModel.IsRuntimeAvailable);

        Assert.Equal([0, 0], client.AfterRevisions.ToArray());
        Assert.True(gateway.IsOwnerStreamAvailable);
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
    public async Task DetachedTranscript_LateBootstrapResetReconcilesTheExactLivePage()
    {
        var gateway = new ResnapshotSubsessionGateway();
        var initial = ResnapshotSubsessionGateway.CreateTurn(gateway.ChildSessionId, "initial");
        var live = ResnapshotSubsessionGateway.CreateTurn(gateway.ChildSessionId, "live during reset");
        gateway.RecentTurns = [initial];
        var dispatcher = new ResetProbeDispatcher();
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway, dispatcher);
        await viewModel.InitializeAsync();
        var initialRow = Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>());
        await dispatcher.InvokeAsync(() => Assert.True(viewModel.DetachTranscriptFromLatest()));
        var read = gateway.HoldNextRecentRead();
        gateway.RaiseResnapshotRequired();
        await read.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        gateway.RaiseTurnChanged(live);
        var loop = typeof(SubsessionsViewModel).GetField("_runtimeRefresh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(viewModel)!;
        var drained = (Task)loop.GetType().GetProperty("WhenIdle")!.GetValue(loop)!;
        gateway.RecentTurns = [initial, live]; // Exact IDs, not equivalent text under new identities.
        read.Release.TrySetResult();
        await drained.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(viewModel.IsTranscriptFollowingLatest);
        Assert.False(viewModel.IsTranscriptLoading);
        Assert.False(viewModel.HasLoadError, viewModel.StatusText);
        Assert.Equal(2, viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>().Count());
        Assert.Same(initialRow, viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>().First());

        // A reset intentionally reconciles an authoritative page, including the
        // live record it contains. It is not the ordinary detached-live path.
        // The diagnostic two-second rejection is retained under its own frame.
        Assert.False(viewModel.HasNewerTranscriptRows);
        Assert.Contains(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>(), row => row.RowId == live.TurnId);
    }

    [Fact]
    public async Task InitialTranscriptTransportFailureSurfacesActionableStatus()
    {
        using var gateway = new SubagentAppRuntimeGateway(new MissingPageSubagentRuntimeClient());
        using var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);

        await viewModel.InitializeAsync();

        Assert.Contains("Unable to load subsession transcript", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("did not return the Compact transcript page", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Empty(viewModel.Messages);
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.Unavailable, "runtime.v1.unavailable")]
    [InlineData(SunderRpcErrorKind.Unavailable, "rpc.transport.unavailable")]
    [InlineData(SunderRpcErrorKind.StaleEndpoint, "rpc.caller-retired")]
    public async Task AppGateway_RuntimeSnapshotRecoversKnownPublicationAvailabilityAsOneSharedRead(SunderRpcErrorKind kind, string code)
    {
        var failure = new SunderRpcException(new(kind, code, "The publication is still in progress.",
            SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new PublicationSnapshotClient(async (count, _) =>
        {
            if (count == 1) { await release.Task; throw failure; }
            return SubagentProjectionContent.Create(new SubagentProjection(Sessions: []));
        });
        await using var gateway = new SubagentAppRuntimeGateway(client);
        var first = gateway.ListSessionsAsync();
        var second = gateway.ListSessionsAsync();
        release.SetResult();
        Assert.Empty((await first.WaitAsync(TimeSpan.FromSeconds(5))).Sessions);
        Assert.Empty((await second.WaitAsync(TimeSpan.FromSeconds(5))).Sessions);
        Assert.Equal(2, client.Reads); // Retry the known read, never each waiting presentation operation.
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.PermissionDenied, "rpc.permission-denied")]
    [InlineData(SunderRpcErrorKind.Protocol, "rpc.binding.invalid-catalog")]
    [InlineData(SunderRpcErrorKind.Unavailable, "package.policy.security-blocked")]
    [InlineData(SunderRpcErrorKind.StaleEndpoint, "rpc.unrelated-stale-endpoint")]
    public async Task AppGateway_RuntimeSnapshotDoesNotRetrySecurityProtocolOrUnrelatedFailures(SunderRpcErrorKind kind, string code)
    {
        var failure = new SunderRpcException(new(kind, code, "Do not replace this failure."));
        var client = new PublicationSnapshotClient((_, _) => Task.FromException<SunderContent>(failure));
        await using var gateway = new SubagentAppRuntimeGateway(client);
        Assert.Same(failure, await Assert.ThrowsAsync<SunderRpcException>(() => gateway.ListSessionsAsync()));
        Assert.Equal(1, client.Reads);
    }

    [Fact]
    public async Task AppGateway_PublicationReadRecoveryNeverReplaysCreateMutation()
    {
        var failure = new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "rpc.transport.unavailable", "Uncertain mutation.",
            SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        var client = new PublicationSnapshotClient((_, _) => throw new InvalidOperationException("No metadata read was requested.")) { CreateFailure = failure };
        await using var gateway = new SubagentAppRuntimeGateway(client);
        Assert.Same(failure, await Assert.ThrowsAsync<SunderRpcException>(() => gateway.CreateSubagentAsync("A subagent")));
        Assert.Equal(1, client.Creates);
        Assert.Equal(0, client.Reads);
    }

    [Fact]
    public async Task AppGateway_RuntimeSnapshotPersistentAvailabilityFailureRemainsBoundedAndActionable()
    {
        var failure = new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "runtime.v1.unavailable", "Still unavailable."));
        var client = new PublicationSnapshotClient((_, _) => Task.FromException<SunderContent>(failure));
        await using var gateway = new SubagentAppRuntimeGateway(client);
        Assert.Same(failure, await Assert.ThrowsAsync<SunderRpcException>(() => gateway.ListSessionsAsync().WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.InRange(client.Reads, 2, 6);
    }

    [Fact]
    public async Task AppGateway_RuntimeSnapshotCancelsOnlyTheWaiterAndDisposalCancelsOwnedRecovery()
    {
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new PublicationSnapshotClient(async (count, token) =>
        {
            if (count == 1) throw new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "runtime.v1.unavailable", "Publishing."));
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return SubagentProjectionContent.Create(new SubagentProjection(Sessions: []));
        });
        var gateway = new SubagentAppRuntimeGateway(client);
        var cancelledWaiter = gateway.ListSessionsAsync(caller.Token);
        var remainingWaiter = gateway.ListSessionsAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);
        Assert.False(remainingWaiter.IsCompleted);
        await gateway.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => remainingWaiter.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, client.Reads);
    }

    private sealed class PublicationSnapshotClient(Func<int, CancellationToken, Task<SunderContent>> read) : SubagentManagementClientStub
    {
        internal int Reads;
        internal int Creates;
        internal Exception? CreateFailure { get; init; }
        public override ValueTask<SunderContent> RuntimeCatalogAsync(CancellationToken cancellationToken = default)
            => new(read(Interlocked.Increment(ref Reads), cancellationToken));
        public override ValueTask<SunderContent> CreateAsync(string displayName, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Creates);
            return ValueTask.FromException<SunderContent>(CreateFailure ?? new InvalidOperationException("No mutation was requested."));
        }
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

    private abstract class SubagentManagementClientStub : SubagentManagementServices.ISubagentsManagementService
    {
        public virtual ValueTask<SunderContent> ManagementAsync(CancellationToken cancellationToken = default) => Unavailable();
        public virtual ValueTask<SunderContent> ProviderModelsAsync(string providerId, CancellationToken cancellationToken = default) => Unavailable();
        public virtual ValueTask<SunderContent> RuntimeCatalogAsync(CancellationToken cancellationToken = default) => Unavailable();
        public virtual ValueTask<SunderContent> RecentTurnsAsync(Guid sessionId, int limit, CancellationToken cancellationToken = default) => Unavailable();
        public virtual ValueTask<SunderContent> TurnsBeforeAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken cancellationToken = default) => Unavailable();
        public virtual ValueTask<SunderContent> TurnsAfterAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken cancellationToken = default) => Unavailable();
        public virtual ValueTask<SunderContent> AroundTurnAsync(Guid sessionId, Guid turnId, DateTimeOffset createdAtUtc, Guid itemId, int beforeLimit, int afterLimit, CancellationToken cancellationToken = default) => Unavailable();
        public virtual ValueTask<SunderContent> ToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken cancellationToken = default) => Unavailable();
        public virtual ValueTask<SunderContent> CompactTranscriptAsync(AgentCompactTranscriptRequest request, CancellationToken cancellationToken = default) => Unavailable();
        public virtual ValueTask<SunderContent> ActivityMembersAsync(AgentTranscriptActivityMembersRequest request, CancellationToken cancellationToken = default) => Unavailable();
        public virtual ValueTask<SunderContent> CreateAsync(string displayName, CancellationToken cancellationToken = default) => Unavailable();
        public virtual ValueTask<SunderContent> SaveAsync(SubagentSaveRequest request, CancellationToken cancellationToken = default) => Unavailable();
        public virtual ValueTask<SunderContent> DeleteAsync(string subagentId, CancellationToken cancellationToken = default) => Unavailable();

        public virtual async IAsyncEnumerable<SunderContent> ChangesAsync(
            long afterRevision,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        protected static SunderContent Content<T>(T projection)
            => SubagentProjectionContent.Create(projection);

        private static ValueTask<SunderContent> Unavailable()
            => ValueTask.FromException<SunderContent>(new NotSupportedException());
    }

    private sealed class ReconnectingSubagentRuntimeClient : SubagentManagementClientStub
    {
        private int _subscriptionCount;

        public ConcurrentQueue<long> AfterRevisions { get; } = new();
        public TaskCompletionSource SecondSubscriptionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async IAsyncEnumerable<SunderContent> ChangesAsync(
            long afterRevision,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            AfterRevisions.Enqueue(afterRevision);
            if (Interlocked.Increment(ref _subscriptionCount) == 1)
            {
                yield return Content(new SubagentChanged(
                    5,
                    SubagentChangeKind.Connected,
                    RuntimeInstanceId: "runtime-one"));
                yield return Content(new SubagentChanged(
                    6,
                    SubagentChangeKind.Session,
                    SessionId: Guid.NewGuid(),
                    RuntimeInstanceId: "runtime-one"));
                yield break;
            }

            SecondSubscriptionStarted.TrySetResult();
            yield return Content(new SubagentChanged(
                6,
                SubagentChangeKind.Connected,
                RuntimeInstanceId: "runtime-two"));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class OverflowResnapshotRuntimeClient : SubagentManagementClientStub
    {
        private readonly Guid _sessionId = Guid.NewGuid();
        private int _subscriptionCount;

        public ConcurrentQueue<long> AfterRevisions { get; } = new();

        public override async IAsyncEnumerable<SunderContent> ChangesAsync(
            long afterRevision,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            AfterRevisions.Enqueue(afterRevision);
            if (Interlocked.Increment(ref _subscriptionCount) == 1)
            {
                yield return Content(new SubagentChanged(
                    1,
                    SubagentChangeKind.Connected,
                    RuntimeInstanceId: "runtime"));
                yield return Content(new SubagentChanged(
                    65,
                    SubagentChangeKind.ResnapshotRequired,
                    RuntimeInstanceId: "runtime"));
                yield break;
            }

            yield return Content(new SubagentChanged(
                65,
                SubagentChangeKind.Connected,
                RuntimeInstanceId: "runtime"));
            yield return Content(new SubagentChanged(
                66,
                SubagentChangeKind.Turn,
                SessionId: _sessionId,
                Turn: CreateTurn(_sessionId, "after resnapshot"),
                RuntimeInstanceId: "runtime"));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        private static WorkerGeneratedAgentTurnRecord CreateTurn(Guid sessionId, string content)
        {
            var turnId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            return new WorkerGeneratedAgentTurnRecord
            {
                TurnId = turnId,
                TurnSequence = Interlocked.Increment(ref _nextTurnSequence),
                SessionId = sessionId,
                Role = WorkerGeneratedAgentMessageRole.Assistant,
                Kind = WorkerGeneratedAgentTurnKind.Message,
                Items =
                [
                    new WorkerGeneratedAgentTurnItemRecord
                    {
                        ItemId = Guid.NewGuid(),
                        TurnId = turnId,
                        SequenceNumber = 0,
                        Kind = WorkerGeneratedAgentTurnItemKind.Text,
                        TextContent = content,
                        IsError = false,
                        IsToolHeaderProjection = false,
                        ToolDetailRevision = 0,
                        WasTruncated = false,
                    },
                ],
                ContentRevision = 1,
                IsStreaming = false,
                CreatedAtUtc = RpcTimestamp(now),
                UpdatedAtUtc = RpcTimestamp(now),
            };
        }
    }

    private sealed class MalformedChangeRuntimeClient : SubagentManagementClientStub
    {
        private int _subscriptionCount;

        internal ConcurrentQueue<long> AfterRevisions { get; } = new();
        internal TaskCompletionSource SecondSubscriptionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async IAsyncEnumerable<SunderContent> ChangesAsync(
            long afterRevision,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            AfterRevisions.Enqueue(afterRevision);
            var subscription = Interlocked.Increment(ref _subscriptionCount);
            yield return Content(new SubagentChanged(
                0,
                SubagentChangeKind.Connected,
                RuntimeInstanceId: "runtime"));
            if (subscription == 1)
            {
                yield return Content(new SubagentChanged(
                    1,
                    (SubagentChangeKind)int.MaxValue,
                    RuntimeInstanceId: "runtime"));
                yield break;
            }

            SecondSubscriptionStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailingCatalogRefreshRuntimeClient : SubagentManagementClientStub
    {
        private int _subscriptionCount;
        private int _managementQueryCount;

        public ConcurrentQueue<long> AfterRevisions { get; } = new();

        public override async ValueTask<SunderContent> ManagementAsync(
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _managementQueryCount) == 1)
            {
                throw new IOException("Injected catalog refresh failure.");
            }
            return Content(new SubagentProjection(Providers:
            [
                new SubagentProviderProjection(
                    "provider.replayed",
                    "Replayed Provider",
                    "test.provider.package"),
            ]));
        }

        public override async IAsyncEnumerable<SunderContent> ChangesAsync(
            long afterRevision,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            AfterRevisions.Enqueue(afterRevision);
            if (Interlocked.Increment(ref _subscriptionCount) == 1)
            {
                yield return Content(new SubagentChanged(
                    1,
                    SubagentChangeKind.Connected,
                    RuntimeInstanceId: "runtime"));
                yield return Content(new SubagentChanged(
                    2,
                    SubagentChangeKind.Catalog,
                    RuntimeInstanceId: "runtime"));
            }
            else
            {
                yield return Content(new SubagentChanged(
                    2,
                    SubagentChangeKind.Catalog,
                    RuntimeInstanceId: "runtime"));
                yield return Content(new SubagentChanged(
                    2,
                    SubagentChangeKind.Connected,
                    RuntimeInstanceId: "runtime"));
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ResnapshotSubsessionGateway :
        AppRuntime.ISubsessionSessionReader,
        AppRuntime.ISubsessionCheckpointReader,
        AppRuntime.ISubsessionTranscriptPageReader,
        AppRuntime.ISubsessionChangeNotifications
    {
        private readonly Guid _rootSessionId = Guid.NewGuid();
        private readonly Guid _otherChildSessionId = Guid.NewGuid();
        private int _catalogReadCount;
        private int _recentReadCount;
        private RecentReadGate? _nextRecentRead;
        internal IReadOnlyList<AppAgentTurnRecord>? RecentTurns { get; set; }

        internal Guid ChildSessionId { get; } = Guid.NewGuid();
        internal int RecentReadCount => Volatile.Read(ref _recentReadCount);

        public event Action<Guid>? SessionChanged
        {
            add { }
            remove { }
        }
        public event Action<Guid, AppAgentTurnRecord, SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTranscriptActivityGroup?>? TurnChanged;
        public event Action? ResnapshotRequired;

        public Task<AppRuntime.SubsessionSessionCatalog> ListSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var read = Interlocked.Increment(ref _catalogReadCount);
            var selectedUpdatedAt = read == 1 ? now : now.AddMinutes(-1);
            var otherUpdatedAt = read == 1 ? now.AddMinutes(-1) : now;
            return Task.FromResult(new AppRuntime.SubsessionSessionCatalog(
            [
                new AppAgentSessionRecord
                {
                    SessionId = _rootSessionId,
                    Title = "Root",
                    State = AppAgentSessionState.Active,
                    CreatedAtUtc = RpcTimestamp(now),
                    UpdatedAtUtc = RpcTimestamp(now),
                    RootSessionId = _rootSessionId,
                },
                new AppAgentSessionRecord
                {
                    SessionId = ChildSessionId,
                    Title = "Child",
                    State = AppAgentSessionState.Active,
                    CreatedAtUtc = RpcTimestamp(now),
                    UpdatedAtUtc = RpcTimestamp(selectedUpdatedAt),
                    ParentSessionId = _rootSessionId,
                    RootSessionId = _rootSessionId,
                },
                new AppAgentSessionRecord
                {
                    SessionId = _otherChildSessionId,
                    Title = "Other child",
                    State = AppAgentSessionState.Active,
                    CreatedAtUtc = RpcTimestamp(now),
                    UpdatedAtUtc = RpcTimestamp(otherUpdatedAt),
                    ParentSessionId = _rootSessionId,
                    RootSessionId = _rootSessionId,
                },
            ],
            []));
        }

        public Task<IReadOnlyList<AppAgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AppAgentRunCheckpointRecord>>([]);

        public async Task<AppRuntime.SubsessionTranscriptPage> ListRecentTurnsAsync(
            Guid sessionId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var read = Interlocked.Increment(ref _recentReadCount);
            if (Interlocked.Exchange(ref _nextRecentRead, null) is { } gate)
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
            }
            return new AppRuntime.SubsessionTranscriptPage(
                RecentTurns ?? [CreateTurn(sessionId, read == 1 ? "initial" : "resnapshot")], false);
        }

        public Task<AppRuntime.SubsessionTranscriptPage> ListTurnsBeforeAsync(
            Guid sessionId,
            DateTimeOffset beforeCreatedAtUtc,
            Guid beforeTurnId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AppRuntime.SubsessionTranscriptPage([], false));

        public Task<AppRuntime.SubsessionTranscriptPage> ListTurnsAfterAsync(
            Guid sessionId,
            DateTimeOffset afterCreatedAtUtc,
            Guid afterTurnId,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AppRuntime.SubsessionTranscriptPage([], false));

        public Task<AppRuntime.SubsessionAroundTurnPage> LoadAroundTurnAsync(
            Guid sessionId,
            Guid turnId,
            DateTimeOffset turnCreatedAtUtc,
            Guid itemId,
            int beforeLimit,
            int afterLimit,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AppRuntime.SubsessionAroundTurnPage([], false, false, turnId));

        internal void RaiseResnapshotRequired() => ResnapshotRequired?.Invoke();
        internal RecentReadGate HoldNextRecentRead() => _nextRecentRead = new();

        internal void RaiseTurnChanged(string content)
            => TurnChanged?.Invoke(ChildSessionId, CreateTurn(ChildSessionId, content), null);
        internal void RaiseTurnChanged(AppAgentTurnRecord turn) => TurnChanged?.Invoke(ChildSessionId, turn, null);

        internal static AppAgentTurnRecord CreateTurn(Guid sessionId, string content)
        {
            var turnId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            return new AppAgentTurnRecord
            {
                TurnId = turnId,
                TurnSequence = Interlocked.Increment(ref _nextTurnSequence),
                SessionId = sessionId,
                Role = AppAgentMessageRole.Assistant,
                Kind = AppAgentTurnKind.Message,
                Items =
                [
                    new AppAgentTurnItemRecord
                    {
                        ItemId = Guid.NewGuid(),
                        TurnId = turnId,
                        SequenceNumber = 0,
                        Kind = AppAgentTurnItemKind.Text,
                        TextContent = content,
                        IsError = false,
                        IsToolHeaderProjection = false,
                        ToolDetailRevision = 0,
                        WasTruncated = false,
                    },
                ],
                ContentRevision = 1,
                IsStreaming = false,
                CreatedAtUtc = RpcTimestamp(now),
                UpdatedAtUtc = RpcTimestamp(now),
            };
        }
    }

    private sealed class RecentReadGate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ResetProbeDispatcher : SubagentsApp::Sunder.Package.Agent.Shared.Presentation.IPresentationDispatcher
    {
        private readonly object _gate = new();
        public bool CheckAccess() => Monitor.IsEntered(_gate);
        public Task InvokeAsync(Action action) { lock (_gate) action(); return Task.CompletedTask; }
    }

    private sealed class TestRuntimeCatalog : IAgentRuntimeCatalog
    {
        private Action<Guid>? _sessionChanged;
        private Action? _resetRequired;

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

        public event Action<Guid, WorkerAgentTurnRecord>? TurnChanged
        {
            add { }
            remove { }
        }

        public event Action<string>? ProfileChanged
        {
            add { }
            remove { }
        }

        public event Action? ResetRequired
        {
            add => _resetRequired += value;
            remove => _resetRequired -= value;
        }

        public IReadOnlyList<AgentSessionRecord> ListSessions() => [];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId) => [];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId) => [];
        public AgentSessionRecord? GetSession(Guid sessionId) => null;
        public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentWorkspaceRecord>>([]);
        public ValueTask<AgentWorkspaceRecord?> GetWorkspaceAsync(
            string workspaceId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentWorkspaceRecord?>(null);
        public AgentProfileRecord? GetSessionProfile(Guid sessionId) => null;
        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;
        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => null;
        public WorkerAgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;
        public IReadOnlyList<WorkerAgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];
        public IReadOnlyList<WorkerAgentTurnRecord> ListTurnsBefore(
            Guid sessionId,
            DateTimeOffset beforeCreatedAtUtc,
            Guid beforeTurnId,
            int limit) => [];
        public IReadOnlyList<WorkerAgentTurnRecord> ListTurnsAfter(
            Guid sessionId,
            DateTimeOffset afterCreatedAtUtc,
            Guid afterTurnId,
            int limit) => [];
        public IReadOnlyList<AgentProfileRecord> ListProfiles() => [];
        public AgentProfileRecord? GetProfile(string profileId) => null;
        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => null;
        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind) => null;

        public void RaiseSessionChanged(Guid sessionId) => _sessionChanged?.Invoke(sessionId);

        public void RaiseResetRequired() => _resetRequired?.Invoke();
    }

    private sealed class MissingPageSubagentRuntimeClient : SubagentManagementClientStub
    {
        private readonly Guid _rootSessionId = Guid.NewGuid();
        private readonly Guid _childSessionId = Guid.NewGuid();

        public override ValueTask<SunderContent> RuntimeCatalogAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Content(CreateRuntimeCatalog()));
        }

        public override ValueTask<SunderContent> RecentTurnsAsync(
            Guid sessionId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Content(new SubagentProjection()));
        }

        public override ValueTask<SunderContent> CompactTranscriptAsync(AgentCompactTranscriptRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Content(new SubagentProjection()));
        }

        public override async IAsyncEnumerable<SunderContent> ChangesAsync(
            long afterRevision,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
                    new WorkerGeneratedAgentSessionRecord
                    {
                        SessionId = _rootSessionId,
                        Title = "Root",
                        State = WorkerGeneratedAgentSessionState.Active,
                        CreatedAtUtc = RpcTimestamp(now),
                        UpdatedAtUtc = RpcTimestamp(now),
                        RootSessionId = _rootSessionId,
                    },
                    new WorkerGeneratedAgentSessionRecord
                    {
                        SessionId = _childSessionId,
                        Title = "Child",
                        State = WorkerGeneratedAgentSessionState.Active,
                        CreatedAtUtc = RpcTimestamp(now),
                        UpdatedAtUtc = RpcTimestamp(now),
                        ParentSessionId = _rootSessionId,
                        RootSessionId = _rootSessionId,
                    },
                ],
                Profiles: [],
                Checkpoints: []);
        }
    }
}
