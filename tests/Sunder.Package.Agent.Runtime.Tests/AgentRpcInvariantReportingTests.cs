using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRpcInvariantReportingTests
{
    [Fact]
    public async Task CurrentReference_SendsItsExactEndpointAndRetiresAfterAcceptedReport()
    {
        var client = new RecordingRpcClient(CreateProvider("rpc1_exact"));
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(client.Provider.ContractId);
        var provider = bindings.CreateProvider(client.Provider, static (_, _, _) => new object());
        var reference = provider.Reference;
        var exception = new InvalidOperationException("invariant failure");

        var accepted = await reference.TryReportInvariantViolationAsync(exception);

        Assert.True(accepted);
        Assert.Equal(client.Provider.Endpoint, client.ReportedEndpoint);
        Assert.Same(exception, client.ReportedException);
        Assert.Equal(1, client.ReportCount);
        Assert.True(reference.RetirementToken.IsCancellationRequested);
        var repeated = await Assert.ThrowsAsync<SunderRpcException>(() => reference.TryReportInvariantViolationAsync(exception).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, repeated.Error.Kind);
        Assert.Equal(1, client.ReportCount);
    }

    [Fact]
    public async Task ReferenceReporting_UsesItsReceiverAndRejectsLateIdentityChanges()
    {
        var provider = CreateProvider("rpc1_shared");
        var ownerClient = new RecordingRpcClient(provider);
        var foreignClient = new RecordingRpcClient(provider);
        await using var owner = new SunderRpcServiceBindingLifetime(ownerClient);
        await using var foreign = new SunderRpcServiceBindingLifetime(foreignClient);
        await owner.DiscoverAsync(provider.ContractId);
        await foreign.DiscoverAsync(provider.ContractId);
        var ownerReference = owner.CreateProvider(provider, static (_, _, _) => new object()).Reference;
        var foreignReference = foreign.CreateProvider(provider, static (_, _, _) => new object()).Reference;

        Assert.True(await foreignReference.TryReportInvariantViolationAsync(new InvalidOperationException("foreign receiver")));
        Assert.Equal(1, foreignClient.ReportCount);
        Assert.Equal(0, ownerClient.ReportCount);

        ownerClient.Provider = provider with { SessionGeneration = provider.SessionGeneration + 1 };
        Assert.False(await ownerReference.TryReportInvariantViolationAsync(new InvalidOperationException("stale")));
        Assert.Equal(0, ownerClient.ReportCount);
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.PermissionDenied)]
    [InlineData(SunderRpcErrorKind.Unavailable)]
    [InlineData(SunderRpcErrorKind.Protocol)]
    public async Task TransportAndPermissionFailures_PreserveUnsafeStructuredMetadata(SunderRpcErrorKind kind)
    {
        var failure = new SunderRpcException(new SunderRpcError(kind, "fixture.report-failure", "Rejected.",
            SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        var client = new RecordingRpcClient(CreateProvider("rpc1_failure"))
        {
            ReportFailure = failure,
        };
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(client.Provider.ContractId);
        var reference = bindings.CreateProvider(client.Provider, static (_, _, _) => new object()).Reference;

        var observed = await Assert.ThrowsAsync<SunderRpcException>(() => reference.TryReportInvariantViolationAsync(
            new InvalidOperationException("invariant")).AsTask());
        Assert.Same(failure, observed);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, observed.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, observed.Error.RetrySafety);
        Assert.Equal(1, client.ReportCount);
    }

    private static SunderRpcProviderSnapshot CreateProvider(string endpoint)
        => new(
            "provider.package",
            "1.0.0",
            "provider.package.service",
            "test.invariant.contract",
            "1.0.0",
            new string('a', 64),
            new string('b', 64),
            Guid.NewGuid(),
            1,
            1,
            new SunderRpcEndpointReference(endpoint),
            1,
            SunderRpcProviderState.Active);

    private sealed class RecordingRpcClient(SunderRpcProviderSnapshot provider) : ISunderRpcClient
    {
        public SunderRpcProviderSnapshot Provider { get; set; } = provider;

        public bool ProviderAvailable { get; set; } = true;

        public Exception? ReportFailure { get; init; }

        public int ReportCount { get; private set; }

        public SunderRpcEndpointReference? ReportedEndpoint { get; private set; }

        public Exception? ReportedException { get; private set; }

        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
            SunderRpcEndpointReference endpoint,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SunderRpcProviderSnapshot?>(
                ProviderAvailable && endpoint == Provider.Endpoint ? Provider : null);

        public ValueTask<bool> TryReportInvariantViolationAsync(
            SunderRpcEndpointReference endpoint,
            Exception exception,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportCount++;
            ReportedEndpoint = endpoint;
            ReportedException = exception;
            return ReportFailure is null
                ? ValueTask.FromResult(true)
                : ValueTask.FromException<bool>(ReportFailure);
        }

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
            string contractId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SunderRpcCatalogSnapshot(
                1,
                1,
                ProviderAvailable && (string.IsNullOrEmpty(contractId) || string.Equals(contractId, Provider.ContractId, StringComparison.Ordinal))
                    ? [Provider]
                    : []));

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
            => throw new NotSupportedException();

        public IAsyncEnumerable<JsonElement> SubscribeAsync(
            SunderRpcEndpointReference endpoint,
            string serviceId,
            string methodId,
            JsonElement request,
            SunderRpcCallOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
