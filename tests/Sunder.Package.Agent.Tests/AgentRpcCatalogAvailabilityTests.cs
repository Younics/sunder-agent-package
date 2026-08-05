using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Services;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRpcCatalogAvailabilityTests
{
    [Fact]
    public async Task DormantCatalog_StartsWatcherExactlyOnceWhenActivated()
    {
        var client = new RetryingWatchRpcClient();
        using var catalog = AgentRpcCatalog.CreateDormant(client);

        Assert.Equal(0, client.WatchAttempts);
        catalog.StartWatching();
        catalog.StartWatching();

        await client.FirstWatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, client.WatchAttempts);
    }

    [Fact]
    public void UnavailableDiscovery_ReturnsAnEmptyCatalogAndReportsAvailability()
    {
        using var catalog = new AgentRpcCatalog(new UnavailableRpcClient(SunderRpcErrorKind.Unavailable));
        var providerService = new AgentRpcProviderService<object>(
            AgentRpcContractIds.BehaviorLoop,
            static (_, _) => new object());

        Assert.Empty(catalog.GetServiceReferences(AgentRpcServices.ChatProviders));
        Assert.Empty(catalog.GetServiceReferences(providerService));
        Assert.Equal(AgentRpcCatalogAvailability.Unavailable, catalog.Availability);
        Assert.Equal(SunderRpcErrorKind.Unavailable, catalog.AvailabilityError?.Kind);
    }

    [Fact]
    public void PermissionDeniedDiscovery_IsExplicit()
    {
        using var catalog = new AgentRpcCatalog(new UnavailableRpcClient(SunderRpcErrorKind.PermissionDenied));
        var providerService = new AgentRpcProviderService<object>(
            AgentRpcContractIds.BehaviorLoop,
            static (_, _) => new object());

        var serviceFailure = Assert.Throws<SunderRpcException>(
            () => catalog.GetServiceReferences(AgentRpcServices.ChatProviders));
        var providerFailure = Assert.Throws<SunderRpcException>(
            () => catalog.GetServiceReferences(providerService));

        Assert.Equal(SunderRpcErrorKind.PermissionDenied, serviceFailure.Error.Kind);
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, providerFailure.Error.Kind);
        Assert.Equal(AgentRpcCatalogAvailability.PermissionDenied, catalog.Availability);
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, catalog.AvailabilityError?.Kind);
    }

    [Fact]
    public async Task AsyncDiscovery_HonorsCallerCancellation()
    {
        var client = new BlockingDiscoverRpcClient();
        using var catalog = new AgentRpcCatalog(client);
        using var cancellation = new CancellationTokenSource();
        var service = new AgentRpcService<object>(
            "test.catalog",
            static (_, _) => new object());

        var discovery = catalog.GetServiceReferencesAsync(service, cancellation.Token).AsTask();
        await client.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
        Assert.NotEqual(AgentRpcCatalogAvailability.Faulted, catalog.Availability);
    }

    [Fact]
    public async Task AsyncDiscovery_RpcCancellationDoesNotFaultCatalog()
    {
        var client = new BlockingDiscoverRpcClient { ReturnRpcCancellation = true };
        using var catalog = new AgentRpcCatalog(client);
        using var cancellation = new CancellationTokenSource();
        var service = new AgentRpcService<object>(
            "test.catalog",
            static (_, _) => new object());

        var discovery = catalog.GetServiceReferencesAsync(service, cancellation.Token).AsTask();
        await client.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
        Assert.NotEqual(AgentRpcCatalogAvailability.Faulted, catalog.Availability);
    }

    [Fact]
    public async Task AsyncReferenceAcquisition_HonorsCallerCancellation()
    {
        var client = new BlockingProviderLookupRpcClient();
        using var catalog = new AgentRpcCatalog(client);
        using var cancellation = new CancellationTokenSource();
        var service = new AgentRpcService<object>(
            client.Provider.ContractId,
            static (_, _) => new object());
        var reference = Assert.Single(catalog.GetServiceReferences(service));

        var acquisition = reference.TryAcquireAsync(cancellation.Token).AsTask();
        await client.LookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
    }

    [Fact]
    public async Task WatcherRetriesAfterUnavailableAndInvalidatesEveryCatalogProjection()
    {
        var client = new RetryingWatchRpcClient();
        using var catalog = new AgentRpcCatalog(client);
        using var observer = new AgentProfileSelectableCapabilityChangeObserver(catalog);
        var projectionChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var availabilityRecovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.Changed += () => projectionChanged.TrySetResult();
        catalog.AvailabilityChanged += (_, _) =>
        {
            if (catalog.Availability == AgentRpcCatalogAvailability.Available)
            {
                availabilityRecovered.TrySetResult();
            }
        };

        await client.FirstWatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        client.ReleaseFirstWatch.TrySetResult();

        await projectionChanged.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await availabilityRecovered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(client.WatchAttempts >= 2);
        Assert.Equal(AgentRpcCatalogAvailability.Available, catalog.Availability);
    }

    [Fact]
    public void RetiredReference_IsReplacedWhenTheEndpointIsDiscoverableAgain()
    {
        var client = new RecoveringReferenceRpcClient();
        using var catalog = new AgentRpcCatalog(client);
        var service = new AgentRpcService<object>(
            client.Provider.ContractId,
            static (_, _) => new object());
        var first = Assert.Single(catalog.GetServiceReferences(service));
        client.ProviderAvailable = false;

        Assert.False(first.TryAcquire(out _));

        client.ProviderAvailable = true;
        var recovered = Assert.Single(catalog.GetServiceReferences(service));
        Assert.NotSame(first, recovered);
        Assert.True(recovered.TryAcquire(out var lease));
        lease.Dispose();
    }

    [Fact]
    public async Task AsyncReferenceAcquisition_RejectsChangedProviderIdentityAtSameEndpoint()
    {
        var client = new RecoveringReferenceRpcClient();
        using var catalog = new AgentRpcCatalog(client);
        var service = new AgentRpcService<object>(
            client.Provider.ContractId,
            static (_, _) => new object());
        var reference = Assert.Single(catalog.GetServiceReferences(service));
        client.ReplaceSessionGeneration();

        var lease = await reference.TryAcquireAsync();

        Assert.Null(lease);
    }

    [Fact]
    public async Task AsyncSnapshot_CancelsMetadataWhenProviderRetires()
    {
        var client = new RetiringMetadataRpcClient();
        using var catalog = new AgentRpcCatalog(client);
        var metadataStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new AgentRpcService<object>(
            client.Provider.ContractId,
            static (_, _) => new object());
        var snapshot = AgentRpcInvocation.SnapshotAsync(
            catalog,
            service,
            async (_, cancellationToken) =>
            {
                metadataStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "metadata";
            },
            omitUnavailable: true);
        await metadataStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        client.RetireProvider.TrySetResult();
        var result = await snapshot.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(result);
    }

    [Fact]
    public async Task StrictAsyncSnapshot_FailsWhenProviderDisappearsBeforeAcquisition()
    {
        var client = new RecoveringReferenceRpcClient { DisableAfterDiscovery = true };
        using var catalog = new AgentRpcCatalog(client);
        var service = new AgentRpcService<object>(
            client.Provider.ContractId,
            static (_, _) => new object());

        await Assert.ThrowsAsync<AgentPackageUnavailableException>(() =>
            AgentRpcInvocation.SnapshotAsync(
                catalog,
                service,
                static (_, _) => ValueTask.FromResult("metadata"),
                omitUnavailable: false));
    }

    private sealed class UnavailableRpcClient(SunderRpcErrorKind errorKind) : ISunderRpcClient
    {
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SunderRpcProviderSnapshot?>(Failure());

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<bool>(Failure());

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SunderRpcCatalogSnapshot>(Failure());

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (errorKind is SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.PermissionDenied) throw Failure();
            yield break;
        }

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<JsonElement>(Failure());

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (errorKind is SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.PermissionDenied) throw Failure();
            yield break;
        }

        private SunderRpcException Failure()
            => new(new SunderRpcError(
                errorKind,
                "test.rpc.catalog-unavailable",
                "The test RPC catalog is not available."));
    }

    private sealed class RetryingWatchRpcClient : ISunderRpcClient
    {
        private readonly SunderRpcProviderSnapshot _provider = CreateProvider(
            AgentRpcContractIds.ChatProvider,
            "test.rpc.chat-provider");
        private int _watchAttempts;

        public int WatchAttempts => Volatile.Read(ref _watchAttempts);
        public TaskCompletionSource FirstWatchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWatch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SunderRpcProviderSnapshot?>(_provider);

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SunderRpcCatalogSnapshot(0, 0, []));

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _watchAttempts);
            if (attempt == 1)
            {
                FirstWatchStarted.TrySetResult();
                await ReleaseFirstWatch.Task.WaitAsync(cancellationToken);
                throw Failure(SunderRpcErrorKind.Unavailable);
            }

            yield return new SunderRpcCatalogEvent(
                1,
                1,
                SunderRpcCatalogEventKind.Activated,
                _provider);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<JsonElement>(new NotSupportedException());

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class BlockingDiscoverRpcClient : ISunderRpcClient
    {
        public TaskCompletionSource DiscoveryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ReturnRpcCancellation { get; init; }

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SunderRpcProviderSnapshot?>(null);

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
        {
            DiscoveryStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (ReturnRpcCancellation)
            {
                throw Failure(SunderRpcErrorKind.Cancelled);
            }
            return new SunderRpcCatalogSnapshot(0, 0, []);
        }

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<JsonElement>(new NotSupportedException());

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class BlockingProviderLookupRpcClient : ISunderRpcClient
    {
        public SunderRpcProviderSnapshot Provider { get; } = CreateProvider(
            "test.reference.async",
            "test.rpc.blocking-reference");

        public TaskCompletionSource LookupStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
        {
            LookupStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Provider;
        }

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SunderRpcCatalogSnapshot(1, 1, [Provider]));

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<JsonElement>(new NotSupportedException());

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class RecoveringReferenceRpcClient : ISunderRpcClient
    {
        public SunderRpcProviderSnapshot Provider { get; private set; } = CreateProvider(
            "test.reference",
            "test.rpc.recovering-reference");

        public bool ProviderAvailable { get; set; } = true;

        public bool DisableAfterDiscovery { get; init; }

        public void ReplaceSessionGeneration()
            => Provider = Provider with { SessionGeneration = Provider.SessionGeneration + 1 };

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SunderRpcProviderSnapshot?>(ProviderAvailable ? Provider : null);

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
        {
            var snapshot = new SunderRpcCatalogSnapshot(1, 1, [Provider]);
            if (DisableAfterDiscovery) ProviderAvailable = false;
            return ValueTask.FromResult(snapshot);
        }

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<JsonElement>(new NotSupportedException());

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class RetiringMetadataRpcClient : ISunderRpcClient
    {
        public SunderRpcProviderSnapshot Provider { get; } = CreateProvider(
            "test.reference.metadata",
            "test.rpc.retiring-metadata");

        public TaskCompletionSource RetireProvider { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SunderRpcProviderSnapshot?>(Provider);

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SunderRpcCatalogSnapshot(1, 1, [Provider]));

        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
            long afterRevision,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await RetireProvider.Task.WaitAsync(cancellationToken);
            yield return new SunderRpcCatalogEvent(
                2,
                2,
                SunderRpcCatalogEventKind.Deactivated,
                Provider);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask<JsonElement> InvokeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<JsonElement>(new NotSupportedException());

        public async IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private static SunderRpcProviderSnapshot CreateProvider(string contractId, string endpoint)
        => new(
            "test.package",
            "1.0.0",
            "test.provider",
            contractId,
            "1.0.0",
            new string('a', 64),
            Guid.NewGuid(),
            1,
            1,
            new SunderRpcEndpointReference(endpoint),
            1,
            SunderRpcProviderState.Active);

    private static SunderRpcException Failure(SunderRpcErrorKind errorKind)
        => new(new SunderRpcError(
            errorKind,
            "test.rpc.catalog-unavailable",
            "The test RPC catalog is not available."));
}
