using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRpcInvariantReportingTests
{
    [Fact]
    public void CurrentReference_SendsItsExactEndpoint()
    {
        var client = new RecordingRpcClient(CreateProvider("rpc1_exact"));
        using var catalog = AgentRpcCatalog.CreateDormant(client);
        var service = CreateService(client.Provider.ContractId);
        var reference = Assert.Single(catalog.GetServiceReferences(service));
        var exception = new InvalidOperationException("invariant failure");

        var accepted = catalog.TryReportInvariantViolation(reference, exception);

        Assert.True(accepted);
        Assert.Equal(reference.Endpoint, client.ReportedEndpoint);
        Assert.Same(exception, client.ReportedException);
        Assert.Equal(1, client.ReportCount);
    }

    [Fact]
    public void StaleAndForeignReferences_AreRejectedWithoutTransportCalls()
    {
        var provider = CreateProvider("rpc1_shared");
        var ownerClient = new RecordingRpcClient(provider);
        var foreignClient = new RecordingRpcClient(provider);
        using var owner = AgentRpcCatalog.CreateDormant(ownerClient);
        using var foreign = AgentRpcCatalog.CreateDormant(foreignClient);
        var service = CreateService(provider.ContractId);
        var ownerReference = Assert.Single(owner.GetServiceReferences(service));
        Assert.Single(foreign.GetServiceReferences(service));

        Assert.False(foreign.TryReportInvariantViolation(
            ownerReference,
            new InvalidOperationException("foreign")));
        Assert.Equal(0, foreignClient.ReportCount);

        ownerClient.ProviderAvailable = false;
        Assert.Empty(owner.GetServiceReferences(service));
        Assert.False(owner.TryReportInvariantViolation(
            ownerReference,
            new InvalidOperationException("stale")));
        Assert.Equal(0, ownerClient.ReportCount);
    }

    [Fact]
    public void TransportAndPermissionFailures_FailClosed()
    {
        var client = new RecordingRpcClient(CreateProvider("rpc1_failure"))
        {
            ReportFailure = new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.PermissionDenied,
                "rpc.permission.denied",
                "Denied.")),
        };
        using var catalog = AgentRpcCatalog.CreateDormant(client);
        var reference = Assert.Single(catalog.GetServiceReferences(CreateService(client.Provider.ContractId)));

        Assert.False(catalog.TryReportInvariantViolation(
            reference,
            new InvalidOperationException("invariant")));
        Assert.Equal(1, client.ReportCount);
    }

    private static AgentRpcService<object> CreateService(string contractId)
        => new(contractId, static (_, _) => new object());

    private static SunderRpcProviderSnapshot CreateProvider(string endpoint)
        => new(
            "provider.package",
            "1.0.0",
            "provider.package.service",
            "test.invariant.contract",
            "1.0.0",
            new string('a', 64),
            Guid.NewGuid(),
            1,
            1,
            new SunderRpcEndpointReference(endpoint),
            1,
            SunderRpcProviderState.Active);

    private sealed class RecordingRpcClient(SunderRpcProviderSnapshot provider) : ISunderRpcClient
    {
        public SunderRpcProviderSnapshot Provider { get; } = provider;

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
                ProviderAvailable && string.Equals(contractId, Provider.ContractId, StringComparison.Ordinal)
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
