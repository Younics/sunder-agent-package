extern alias AgentCore;

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using AgentCore::Sunder.Package.Agent.HistorySearch;
using AgentCore::Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;
using Xunit;
using AgentPackageUnavailableException = AgentCore::Sunder.Package.Agent.Services.AgentPackageUnavailableException;
using static Sunder.Package.Agent.Tests.RuntimeSnapshotFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Runtime correctness")]
[Trait("Lane", "PackageCompatibility")]
public sealed class RuntimeGatewayReplayTests
{
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
    public void Gateway_RuntimeInstanceChangeForcesResnapshotAcrossOverlappingRevisions()
    {
        using var gateway = new AgentAppRuntimeGateway(
            new ToggleRuntimeClient(isAvailable: true));
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
        using var gateway = new AgentAppRuntimeGateway(
            new ToggleRuntimeClient(isAvailable: true));
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
        using var gateway = new AgentAppRuntimeGateway(
            new ToggleRuntimeClient(isAvailable: true));
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
        applySnapshot.Invoke(gateway, [CreateChatSnapshot(1), false]);
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
        using var gateway = new AgentAppRuntimeGateway(
            new ToggleRuntimeClient(isAvailable: true));
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
    public async Task Gateway_UnavailableThenAvailableRecoversAndInitializes()
    {
        var client = new ToggleRuntimeClient(isAvailable: false);
        using var gateway = new AgentAppRuntimeGateway(client);

        await Assert.ThrowsAsync<SunderRpcException>(() => gateway.InitializeAsync());
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
    public async Task Gateway_EarlyChangeEofBeforeIdentityResnapshotsReplacementBeforeConnected()
    {
        var original = CreateProfileDashboardFixture(
            revision: 100,
            runtimeInstanceId: "runtime-original",
            displayName: "Original runtime profile");
        var replacement = CreateProfileDashboardFixture(
            revision: 1,
            runtimeInstanceId: "runtime-replacement",
            displayName: "Replacement runtime profile");
        var client = new EarlyEofReplacementRuntimeClient(
            original.Dashboard,
            replacement.Dashboard,
            replacement.Snapshot.RuntimeInstanceId!);
        await using var gateway = new AgentAppRuntimeGateway(client);
        var states = new ConcurrentQueue<AgentRuntimeConnectionState>();
        gateway.ConnectionStateChanged += states.Enqueue;

        await gateway.InitializeAsync();
        await client.FirstSubscriptionEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await client.ReplacementDashboardStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AgentRuntimeConnectionState.Reconnecting, gateway.ConnectionState);
        Assert.DoesNotContain(AgentRuntimeConnectionState.Connected, states);

        client.ReleaseReplacementDashboard();
        await client.ReplacementDashboardCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => gateway.ConnectionState == AgentRuntimeConnectionState.Connected);

        Assert.Equal(
            "Replacement runtime profile",
            Assert.Single(gateway.ListProfiles()).DisplayName);
        Assert.Equal(2, client.DashboardInvocationCount);
    }

    [Fact]
    public async Task Gateway_CleanHistoryEofBacksOffAndCarriesKnownRuntimeInstance()
    {
        var client = new CleanHistoryEofRuntimeClient();
        await using var gateway = new AgentAppRuntimeGateway(client);
        await gateway.InitializeAsync();
        await WaitUntilAsync(() => gateway.ConnectionState == AgentRuntimeConnectionState.Connected);

        gateway.StartObservingHistoryStatus();
        await client.FirstHistorySubscriptionEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await gateway.DisposeAsync();

        Assert.Equal(1, client.HistorySubscriptionCount);
        Assert.Equal(
            CleanHistoryEofRuntimeClient.RuntimeInstanceId,
            client.FirstHistorySubscription?.RuntimeInstanceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gateway_CanceledSoleProjectionWaiterIsRetainedAndDrained(
        bool loadCatalog)
    {
        var client = new FaultingProjectionRuntimeClient(loadCatalog);
        var gateway = new AgentAppRuntimeGateway(client);
        using var cancellation = new CancellationTokenSource();
        Task projectionLoad = loadCatalog
            ? gateway.LoadCatalogAsync(new AgentCatalogRequest(), cancellation.Token)
            : gateway.LoadDashboardAsync(cancellation.Token);
        await client.ProducerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => projectionLoad);

        var disposal = gateway.DisposeAsync().AsTask();
        try
        {
            await WaitUntilAsync(() => gateway.ConnectionState == AgentRuntimeConnectionState.Disposed);
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            client.ReleaseProducer();
        }

        await client.ProducerFaulted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, client.InvocationCount);
    }

    [Fact]
    public async Task Gateway_DisposeCancelsAndDrainsChangeObservation()
    {
        var client = new BlockingObservationRuntimeClient();
        var gateway = new AgentAppRuntimeGateway(client);
        await gateway.InitializeAsync();
        await client.ObservationBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await gateway.DisposeAsync();

        Assert.True(client.ObservationEnded.Task.IsCompletedSuccessfully);
        Assert.Equal(AgentRuntimeConnectionState.Disposed, gateway.ConnectionState);
    }

    [Fact]
    public async Task Gateway_DisposeCancelsAndDrainsActiveResnapshot()
    {
        var initial = CreateChatSnapshot(41) with { RuntimeInstanceId = "runtime-resnapshot" };
        var client = new BlockingResnapshotRuntimeClient(initial);
        var gateway = new AgentAppRuntimeGateway(client);
        var reloadCount = 0;
        gateway.ChatSnapshotReloaded += _ => Interlocked.Increment(ref reloadCount);
        var snapshot = await gateway.LoadChatSnapshotAsync(new AgentChatSnapshotRequest());
        gateway.CompleteChatSnapshot(snapshot, applied: true);
        await client.ResnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await gateway.DisposeAsync();

        Assert.True(client.ResnapshotEnded.Task.IsCompletedSuccessfully);
        Assert.Equal(0, Volatile.Read(ref reloadCount));
        Assert.Equal(AgentRuntimeConnectionState.Disposed, gateway.ConnectionState);
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.ProviderFaulted)]
    [InlineData(SunderRpcErrorKind.Cancelled)]
    public async Task Gateway_OperationFailuresDoNotChangeOwnerRuntimeAvailability(SunderRpcErrorKind errorKind)
    {
        var client = new ProviderFaultRuntimeClient(errorKind);
        await using var gateway = new AgentAppRuntimeGateway(client);
        await gateway.InitializeAsync();
        await WaitUntilAsync(() => gateway.ConnectionState == AgentRuntimeConnectionState.Connected);

        var providerFault = await Assert.ThrowsAsync<SunderRpcException>(
            () => gateway.LoadCatalogAsync(new AgentCatalogRequest()));
        var unavailablePackage = new AgentPackageUnavailableException("nested-provider");

        Assert.Equal(errorKind, providerFault.Error.Kind);
        Assert.False(gateway.IsRetryableRuntimeFailure(providerFault));
        Assert.False(gateway.IsRetryableRuntimeFailure(unavailablePackage));
        Assert.Equal(1, client.CatalogInvocationCount);
        Assert.Equal(AgentRuntimeConnectionState.Connected, gateway.ConnectionState);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SunderRpcErrorKind.Cancelled)]
    [InlineData(SunderRpcErrorKind.StaleEndpoint)]
    [InlineData(SunderRpcErrorKind.Unavailable)]
    public async Task Gateway_DisconnectReplaysMissedChangesFromLastRevision(SunderRpcErrorKind? errorKind)
    {
        var client = new DisconnectingRuntimeClient(errorKind);
        using var gateway = new AgentAppRuntimeGateway(client);
        var workspaceChanges = 0;
        gateway.WorkspacesChanged += () => Interlocked.Increment(ref workspaceChanges);

        await gateway.InitializeAsync();
        await WaitUntilAsync(() => client.LastDeliveredRevision == 2
            && Volatile.Read(ref workspaceChanges) == 2
            && gateway.ConnectionState == AgentRuntimeConnectionState.Connected);

        Assert.Equal(2, client.AfterRevisions.Count);
        Assert.Equal(0, client.AfterRevisions[0]);
        Assert.Equal(1, client.AfterRevisions[1]);
        Assert.Equal(2, client.LastDeliveredRevision);
        Assert.Equal(2, Volatile.Read(ref workspaceChanges));
    }

    private sealed class EarlyEofReplacementRuntimeClient(
        AgentDashboardProjection original,
        AgentDashboardProjection replacement,
        string replacementRuntimeInstanceId) : OwnerServiceStub
    {
        private readonly TaskCompletionSource _originalDashboardReturned = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseReplacementDashboard = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dashboardInvocationCount;
        private int _subscriptionCount;

        public int DashboardInvocationCount => Volatile.Read(ref _dashboardInvocationCount);
        public TaskCompletionSource FirstSubscriptionEnded { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReplacementDashboardStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReplacementDashboardCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseReplacementDashboard() => _releaseReplacementDashboard.TrySetResult();

        public override async ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (request is not AgentDashboardRequest)
            {
                throw new NotSupportedException(typeof(TRequest).Name);
            }

            if (Interlocked.Increment(ref _dashboardInvocationCount) == 1)
            {
                _originalDashboardReturned.TrySetResult();
                return (TResponse)(object)original;
            }

            ReplacementDashboardStarted.TrySetResult();
            await _releaseReplacementDashboard.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            ReplacementDashboardCompleted.TrySetResult();
            return (TResponse)(object)replacement;
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            if (request is not AgentChangeSubscription)
            {
                throw new NotSupportedException(typeof(TRequest).Name);
            }

            if (Interlocked.Increment(ref _subscriptionCount) == 1)
            {
                await _originalDashboardReturned.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                FirstSubscriptionEnded.TrySetResult();
                yield break;
            }

            yield return (TEvent)(object)new AgentRuntimeChange(
                replacement.Revision,
                AgentRuntimeChangeKind.Connected,
                RuntimeInstanceId: replacementRuntimeInstanceId);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class CleanHistoryEofRuntimeClient : OwnerServiceStub
    {
        internal const string RuntimeInstanceId = "runtime-history";
        private int _historySubscriptionCount;

        public int HistorySubscriptionCount => Volatile.Read(ref _historySubscriptionCount);
        public HistorySearchStatusSubscription? FirstHistorySubscription { get; private set; }
        public TaskCompletionSource FirstHistorySubscriptionEnded { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            return request is AgentDashboardRequest
                ? ValueTask.FromResult((TResponse)(object)new AgentDashboardProjection(7, [], [], []))
                : ValueTask.FromException<TResponse>(new NotSupportedException(typeof(TRequest).Name));
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            if (request is AgentChangeSubscription)
            {
                yield return (TEvent)(object)new AgentRuntimeChange(
                    7,
                    AgentRuntimeChangeKind.Connected,
                    RuntimeInstanceId: RuntimeInstanceId);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }
            if (request is HistorySearchStatusSubscription)
            {
                Interlocked.Increment(ref _historySubscriptionCount);
                FirstHistorySubscription ??= (HistorySearchStatusSubscription)(object)request;
                FirstHistorySubscriptionEnded.TrySetResult();
                yield break;
            }

            throw new NotSupportedException(typeof(TRequest).Name);
        }
    }

    private sealed class FaultingProjectionRuntimeClient(bool catalog) : OwnerServiceStub
    {
        private readonly TaskCompletionSource _releaseProducer = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public TaskCompletionSource ProducerStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProducerFaulted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseProducer() => _releaseProducer.TrySetResult();

        public override async ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            var expected = catalog
                ? request is AgentCatalogRequest
                : request is AgentDashboardRequest;
            if (!expected)
            {
                throw new NotSupportedException(typeof(TRequest).Name);
            }

            Interlocked.Increment(ref _invocationCount);
            ProducerStarted.TrySetResult();
            await _releaseProducer.Task.ConfigureAwait(false);
            ProducerFaulted.TrySetResult();
            throw new InvalidOperationException("Injected retained projection producer failure.");
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

    private sealed class BlockingObservationRuntimeClient : OwnerServiceStub
    {
        public TaskCompletionSource ObservationBlocked { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ObservationEnded { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            return request is AgentDashboardRequest
                ? ValueTask.FromResult((TResponse)(object)new AgentDashboardProjection(3, [], [], []))
                : ValueTask.FromException<TResponse>(new NotSupportedException(typeof(TRequest).Name));
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            if (request is not AgentChangeSubscription)
            {
                throw new NotSupportedException(typeof(TRequest).Name);
            }

            yield return (TEvent)(object)new AgentRuntimeChange(
                3,
                AgentRuntimeChangeKind.Connected,
                RuntimeInstanceId: "runtime-observation");
            ObservationBlocked.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                ObservationEnded.TrySetResult();
            }
        }
    }

    private sealed class BlockingResnapshotRuntimeClient(
        AgentChatSnapshotProjection initial) : OwnerServiceStub
    {
        private int _snapshotInvocationCount;

        public TaskCompletionSource ResnapshotStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResnapshotEnded { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

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
            if (Interlocked.Increment(ref _snapshotInvocationCount) == 1)
            {
                return (TResponse)(object)initial;
            }

            ResnapshotStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("The infinite resnapshot delay completed unexpectedly.");
            }
            finally
            {
                ResnapshotEnded.TrySetResult();
            }
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            if (request is not AgentChangeSubscription)
            {
                throw new NotSupportedException(typeof(TRequest).Name);
            }

            yield return (TEvent)(object)new AgentRuntimeChange(
                initial.Revision + 1,
                AgentRuntimeChangeKind.ResnapshotRequired,
                RuntimeInstanceId: initial.RuntimeInstanceId);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ProviderFaultRuntimeClient(SunderRpcErrorKind errorKind) : OwnerServiceStub
    {
        private int _catalogInvocationCount;
        public int CatalogInvocationCount => Volatile.Read(ref _catalogInvocationCount);

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request is AgentDashboardRequest)
            {
                return ValueTask.FromResult((TResponse)(object)new AgentDashboardProjection(5, [], [], []));
            }
            if (request is AgentCatalogRequest)
            {
                Interlocked.Increment(ref _catalogInvocationCount);
                return ValueTask.FromException<TResponse>(new SunderRpcException(new SunderRpcError(
                    errorKind,
                    errorKind == SunderRpcErrorKind.Cancelled ? "rpc.call.cancelled" : "rpc.provider.handler-fault",
                    "The operation failed.")));
            }
            return ValueTask.FromException<TResponse>(new NotSupportedException(typeof(TRequest).Name));
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            if (request is not AgentChangeSubscription)
            {
                throw new NotSupportedException(typeof(TRequest).Name);
            }

            yield return (TEvent)(object)new AgentRuntimeChange(
                5,
                AgentRuntimeChangeKind.Connected,
                RuntimeInstanceId: "runtime-provider-fault");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ToggleRuntimeClient(bool isAvailable, bool blockDashboard = false)
        : OwnerServiceStub
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

        public override async ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (!IsAvailable)
            {
                throw RuntimeUnavailable();
            }
            if (blockDashboard && request is AgentDashboardRequest)
            {
                Interlocked.Increment(ref _dashboardInvocationCount);
                await _dashboardRelease.Task.WaitAsync(cancellationToken);
            }
            else if (request is AgentDashboardRequest)
            {
                Interlocked.Increment(ref _dashboardInvocationCount);
            }
            if (request is AgentDashboardRequest)
            {
                return (TResponse)(object)new AgentDashboardProjection(0, [], [], []);
            }
            throw new NotSupportedException(typeof(TRequest).Name);
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
            yield return (TEvent)(object)new AgentRuntimeChange(0, AgentRuntimeChangeKind.Connected);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FaultAfterCanceledDashboardRuntimeClient : OwnerServiceStub
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

        public override async ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (request is not AgentDashboardRequest)
            {
                throw new NotSupportedException(typeof(TRequest).Name);
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

    private sealed class OrderedResnapshotRuntimeClient(AgentChatSnapshotProjection[] snapshots)
        : OwnerServiceStub
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

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
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

    private sealed class DisconnectingRuntimeClient(SunderRpcErrorKind? errorKind) : OwnerServiceStub
    {
        private int _subscriptionCount;
        private long _lastDeliveredRevision;

        public bool IsAvailable => true;
        public List<long> AfterRevisions { get; } = [];
        public long LastDeliveredRevision => Interlocked.Read(ref _lastDeliveredRevision);

        public override ValueTask<TResponse> RespondAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (request is AgentDashboardRequest)
            {
                return ValueTask.FromResult((TResponse)(object)new AgentDashboardProjection(0, [], [], []));
            }
            return ValueTask.FromException<TResponse>(new NotSupportedException(typeof(TRequest).Name));
        }

        public override async IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            var afterRevision = ((AgentChangeSubscription)(object)request).AfterRevision;
            AfterRevisions.Add(afterRevision);
            if (Interlocked.Increment(ref _subscriptionCount) == 1)
            {
                yield return (TEvent)(object)new AgentRuntimeChange(0, AgentRuntimeChangeKind.Connected,
                    RuntimeInstanceId: "runtime-retained");
                yield return (TEvent)(object)new AgentRuntimeChange(1, AgentRuntimeChangeKind.Workspace);
                if (errorKind is { } kind)
                {
                    throw new SunderRpcException(new SunderRpcError(kind,
                        kind == SunderRpcErrorKind.Cancelled ? "rpc.call.cancelled" : "rpc.app-session.retired",
                        "The previous caller session retired during publication."));
                }
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
