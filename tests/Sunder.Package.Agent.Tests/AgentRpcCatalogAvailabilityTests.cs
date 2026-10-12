using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRpcCatalogAvailabilityTests
{
    [Fact]
    public async Task GeneratedCatalogs_StartOneSharedObserverOnFirstDiscovery()
    {
        var client = new CatalogClient();
        await using var receiver = new GeneratedServiceTestScope(client);
        var targets = receiver.Providers<AgentServices.IExecutionTargetService>();
        var chat = receiver.Providers<AgentServices.IChatProviderService>();
        Assert.Equal(0, client.WatchAttempts);

        Assert.Single((await targets.ListAsync()).Providers);
        Assert.Empty((await chat.ListAsync()).Providers);

        Assert.Equal(1, client.WatchAttempts);
        Assert.Equal((11L, 47L), client.FirstWatchCursor);
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.Unavailable)]
    [InlineData(SunderRpcErrorKind.PermissionDenied)]
    public async Task DiscoveryFailure_PreservesClassificationInsteadOfReturningAnEmptyCatalog(SunderRpcErrorKind kind)
    {
        var failure = Failure(kind);
        var client = new CatalogClient { DiscoveryFailure = failure };
        await using var receiver = new GeneratedServiceTestScope(client);

        var actual = await Assert.ThrowsAsync<SunderRpcException>(() =>
            receiver.Providers<AgentServices.IExecutionTargetService>().ListAsync().AsTask());

        Assert.Same(failure, actual);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, actual.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, actual.Error.RetrySafety);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_CallerCancellationDoesNotRetireAnExistingEntry(bool rpcCancellation)
    {
        var client = new CatalogClient { ReturnRpcCancellation = rpcCancellation };
        await using var receiver = new GeneratedServiceTestScope(client);
        var targets = receiver.Providers<AgentServices.IExecutionTargetService>();
        var entry = Assert.Single((await targets.ListAsync()).Providers);
        client.BlockDiscovery = true;
        using var cancellation = new CancellationTokenSource();
        var discovery = targets.ListAsync(cancellation.Token).AsTask();
        await client.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cancellation.CancelAsync();

        var cancelled = await Assert.ThrowsAsync<OperationCanceledException>(() => discovery);
        Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        if (rpcCancellation)
        {
            var remote = Assert.IsType<SunderRpcException>(cancelled.InnerException);
            Assert.Same(Assert.IsType<SunderRpcException>(client.CancellationFailure), remote);
            Assert.Equal(SunderRpcErrorKind.Cancelled, remote.Error.Kind);
            Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, remote.Error.DispatchState);
            Assert.Equal(SunderRpcRetrySafety.Unsafe, remote.Error.RetrySafety);
        }
        Assert.False(entry.RetirementToken.IsCancellationRequested);
        client.BlockDiscovery = false;
        await entry.EnsureCurrentAsync();
    }

    [Fact]
    public async Task ExactValidation_HonorsCallerCancellation()
    {
        var client = new CatalogClient();
        await using var receiver = new GeneratedServiceTestScope(client);
        var entry = Assert.Single((await receiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers);
        client.BlockLookup = true;
        using var cancellation = new CancellationTokenSource();
        var validation = entry.Reference.EnsureCurrentAsync(cancellation.Token).AsTask();
        await client.LookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cancellation.CancelAsync();

        var cancelled = await Assert.ThrowsAsync<TaskCanceledException>(() => validation);
        // This metadata query is linked to the SDK receiver lifetime; unlike a
        // generated service call it does not translate the broker's task token.
        Assert.True(cancelled.CancellationToken.IsCancellationRequested);
        Assert.False(entry.RetirementToken.IsCancellationRequested);
        client.BlockLookup = false;
        await entry.EnsureCurrentAsync();
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.Unavailable)]
    [InlineData(SunderRpcErrorKind.StaleEndpoint)]
    public async Task WatchDisconnect_PermanentlyRetiresEntriesAndRecoveryCreatesAFreshProxy(SunderRpcErrorKind kind)
    {
        var client = new CatalogClient();
        await using var receiver = new GeneratedServiceTestScope(client);
        var targets = receiver.Providers<AgentServices.IExecutionTargetService>();
        var first = Assert.Single((await targets.ListAsync()).Providers);
        var service = first.Service;
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = first.RetirementToken.Register(() => retired.TrySetResult());

        client.FailWatches(Failure(kind));

        await retired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.SecondWatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ServiceProvider<AgentServices.IExecutionTargetService> recovered;
        while (true)
        {
            try { recovered = Assert.Single((await targets.ListAsync(timeout.Token)).Providers); break; }
            catch (SunderRpcException error) when (error.Error.Kind is SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.StaleEndpoint)
            { await Task.Delay(10, timeout.Token); }
        }
        Assert.NotSame(first, recovered);
        Assert.NotSame(service, recovered.Service);
        Assert.Equal(first.ActivationId, recovered.ActivationId);
        await recovered.EnsureCurrentAsync();
        await Assert.ThrowsAsync<SunderRpcException>(() => first.EnsureCurrentAsync().AsTask());
        Assert.True(first.RetirementToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ReceiverDisposal_DrainsTheOpenWatchAndRetiresBorrowedEntries()
    {
        var client = new CatalogClient();
        await using var receiver = new GeneratedServiceTestScope(client);
        var entry = Assert.Single((await receiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers);

        await receiver.DisposeAsync();

        Assert.Equal(0, client.ActiveWatchers);
        Assert.True(entry.RetirementToken.IsCancellationRequested);
        await Assert.ThrowsAsync<SunderRpcException>(() => entry.EnsureCurrentAsync().AsTask());
    }

    [Fact]
    public async Task WithdrawnEntry_RemainsRetiredWhenTheSameEndpointIsDiscoverableAgain()
    {
        var client = new CatalogClient();
        await using var receiver = new GeneratedServiceTestScope(client);
        var targets = receiver.Providers<AgentServices.IExecutionTargetService>();
        var first = Assert.Single((await targets.ListAsync()).Providers);
        client.ProviderAvailable = false;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint,
            (await Assert.ThrowsAsync<SunderRpcException>(() => first.EnsureCurrentAsync().AsTask())).Error.Kind);

        client.ProviderAvailable = true;
        var recovered = Assert.Single((await targets.ListAsync()).Providers);

        Assert.NotSame(first, recovered);
        await recovered.Reference.EnsureCurrentAsync();
        Assert.True(first.RetirementToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("descriptor")]
    [InlineData("abi")]
    public async Task ExactValidation_RejectsChangedIdentityAtTheSameEndpoint(string member)
    {
        var client = new CatalogClient();
        await using var receiver = new GeneratedServiceTestScope(client);
        var entry = Assert.Single((await receiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers);
        client.Provider = member switch
        {
            "session" => client.Provider with { SessionGeneration = client.Provider.SessionGeneration + 1 },
            "descriptor" => client.Provider with { DescriptorSha256 = new string('a', 64) },
            _ => client.Provider with { AbiSha256 = new string('b', 64) },
        };

        var error = await Assert.ThrowsAsync<SunderRpcException>(() => entry.Reference.EnsureCurrentAsync().AsTask());

        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, error.Error.Kind);
        Assert.True(entry.RetirementToken.IsCancellationRequested);
        Assert.Equal(0, client.InvocationCount);
    }

    [Fact]
    public async Task Discovery_RejectsAProviderWithoutItsReviewedAbiHash()
    {
        var client = new CatalogClient();
        client.Provider = client.Provider with { AbiSha256 = null! };
        await using var receiver = new GeneratedServiceTestScope(client);

        var error = await Assert.ThrowsAsync<SunderRpcException>(() =>
            receiver.Providers<AgentServices.IExecutionTargetService>().ListAsync().AsTask());

        Assert.Equal(SunderRpcErrorKind.Protocol, error.Error.Kind);
        Assert.Equal(0, client.InvocationCount);
    }

    [Fact]
    public async Task TargetPresentation_OmitsMetadataWithdrawnDuringTheGeneratedCall()
    {
        var client = new CatalogClient { BlockMetadata = true };
        await using var receiver = new GeneratedServiceTestScope(client);
        var service = new AgentExecutionTargetService(receiver.Providers<AgentServices.IExecutionTargetService>());
        var listing = service.ListTargetsAsync();
        await client.MetadataStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        client.Withdraw();

        Assert.Empty(await listing.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(client.MetadataCancelled);
        Assert.Equal(1, client.InvocationCount);
    }

    [Fact]
    public async Task StrictTargetSelection_RejectsWithdrawalBeforeDispatch()
    {
        var client = new CatalogClient { WithdrawOnLookup = true };
        await using var receiver = new GeneratedServiceTestScope(client);
        var service = new AgentExecutionTargetService(receiver.Providers<AgentServices.IExecutionTargetService>());
        var now = DateTimeOffset.UtcNow;
        var binding = new AgentWorkspaceBindingRecord("binding", "workspace", RegressionTestBindings.ExecutionTargets,
            "test-target", "primary-execution-target", true, 0, now, now);

        var error = await Assert.ThrowsAsync<SunderRpcException>(() => service.ResolveTargetAsync(binding).AsTask());

        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, error.Error.Kind);
        Assert.Equal(0, client.InvocationCount);
    }

    private static SunderRpcException Failure(SunderRpcErrorKind kind)
        => new(new(kind, "test.rpc.catalog-failure", "Controlled catalog failure.",
            SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));

    // The broker supplies snapshots and controlled failures. All catalog, exact
    // binding, cancellation and recovery behavior under test is generated/SDK code.
    private sealed class CatalogClient : ISunderRpcClient
    {
        private readonly object _gate = new();
        private readonly List<Channel<SunderRpcCatalogEvent>> _watchers = [];
        private readonly List<SunderRpcCatalogEvent> _history = [];
        private long _revision = 11;
        private long _sequence = 47;
        private int _watchAttempts;
        public SunderRpcProviderSnapshot Provider { get; set; } = CreateProvider();
        public bool ProviderAvailable { get; set; } = true;
        public bool BlockDiscovery { get; set; }
        public bool BlockLookup { get; set; }
        public bool BlockMetadata { get; init; }
        public bool WithdrawOnLookup { get; init; }
        public bool ReturnRpcCancellation { get; init; }
        public SunderRpcException? DiscoveryFailure { get; init; }
        public SunderRpcException? CancellationFailure { get; private set; }
        public int WatchAttempts => Volatile.Read(ref _watchAttempts);
        public int ActiveWatchers { get { lock (_gate) return _watchers.Count; } }
        public (long Revision, long Sequence) FirstWatchCursor { get; private set; }
        public int InvocationCount { get; private set; }
        public bool MetadataCancelled { get; private set; }
        public TaskCompletionSource DiscoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LookupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MetadataStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondWatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static SunderRpcProviderSnapshot CreateProvider()
        {
            var contract = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ExecutionTargets);
            return new("test.package", "2.0.0", "test.target", contract.ContractId, contract.Version,
                contract.Sha256, contract.AbiSha256!, Guid.NewGuid(), 1, 1, new("test.rpc.target"), 11, SunderRpcProviderState.Active);
        }

        public async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DiscoveryFailure is not null) throw DiscoveryFailure;
            if (BlockDiscovery)
            {
                DiscoveryStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (ReturnRpcCancellation)
                {
                    CancellationFailure = Failure(SunderRpcErrorKind.Cancelled);
                    throw CancellationFailure;
                }
            }
            lock (_gate) return new(_revision, _sequence, ProviderAvailable
                && (string.IsNullOrEmpty(contractId) || Provider.ContractId == contractId) ? [Provider] : []);
        }

        public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BlockLookup)
            {
                LookupStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (WithdrawOnLookup) ProviderAvailable = false;
            return ProviderAvailable ? Provider : null;
        }

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var channel = Channel.CreateUnbounded<SunderRpcCatalogEvent>();
            lock (_gate)
            {
                var attempt = Interlocked.Increment(ref _watchAttempts);
                if (attempt == 1) FirstWatchCursor = (afterRevision, afterSequence);
                if (attempt == 2) SecondWatchStarted.TrySetResult();
                var exactCursor = afterRevision == _revision && afterSequence == _sequence
                    || afterRevision == 11 && afterSequence == 47
                    || _history.Any(item => item.Revision == afterRevision && item.Sequence == afterSequence);
                if (!exactCursor) channel.Writer.TryWrite(new(_revision, _sequence, SunderRpcCatalogEventKind.ResetRequired, null));
                else foreach (var item in _history.Where(item => item.Sequence > afterSequence)) channel.Writer.TryWrite(item);
                _watchers.Add(channel);
            }
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken)) yield return item;
            }
            finally
            {
                lock (_gate) _watchers.Remove(channel);
                channel.Writer.TryComplete();
            }
        }

        internal void FailWatches(Exception error)
        {
            lock (_gate) foreach (var watcher in _watchers) watcher.Writer.TryComplete(error);
        }

        internal void Withdraw()
        {
            lock (_gate)
            {
                ProviderAvailable = false;
                var item = new SunderRpcCatalogEvent(++_revision, ++_sequence, SunderRpcCatalogEventKind.Deactivated,
                    Provider with { CatalogRevision = _revision, State = SunderRpcProviderState.Inactive });
                _history.Add(item);
                foreach (var watcher in _watchers) watcher.Writer.TryWrite(item);
            }
        }

        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ISunderRpcCallScope>(new CallScope(this));
        }

        public async ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId,
            JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal("execution-target", serviceId);
            Assert.Equal("describe", methodId);
            InvocationCount++;
            MetadataStarted.TrySetResult();
            try { if (BlockMetadata) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException)
            {
                MetadataCancelled = true;
                // The broker classifies withdrawal separately from caller cancellation.
                if (!ProviderAvailable) throw Failure(SunderRpcErrorKind.StaleEndpoint);
                throw;
            }
            throw new InvalidOperationException("This scenario must cancel metadata before a response is returned.");
        }

        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId,
            JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private sealed class CallScope(CatalogClient client) : ISunderRpcCallScope
        {
            public DateTimeOffset DeadlineUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(1);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
                => client.GetProviderAsync(endpoint, cancellationToken);
            public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
                => client.DiscoverAsync(contractId, cancellationToken);
            public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
                => client.WatchAsync(afterRevision, afterSequence, cancellationToken);
            public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId,
                JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
                => client.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
            public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId,
                JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
            public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source,
                SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath,
                SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
        }
    }
}
