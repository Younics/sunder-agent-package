using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed partial class OpenAiManagementSourceTests
{
    private sealed class Loopback : ISunderRpcClient
    {
        private readonly SunderWorkerProviderRegistration _registration;
        private readonly SunderRpcContractDescriptor _descriptor;
        private readonly SunderRpcProviderSnapshot _provider;
        internal Loopback(SunderWorkerProviderRegistration registration, SunderRpcContractDescriptor descriptor)
        {
            _registration = registration; _descriptor = descriptor;
            _provider = new("sunder.package.agent.provider.openai", "2.0.0", registration.ProviderId, registration.ContractId,
                registration.ContractVersion, registration.DescriptorSha256, registration.AbiSha256, Guid.NewGuid(), 1, 1, new("source-endpoint"), 1, SunderRpcProviderState.Active);
        }
        internal bool Available { get; set; } = true;
        internal int ScopesCreated { get; private set; }
        internal int ScopesDisposed { get; private set; }
        internal JsonElement LastResponse { get; private set; }
        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); ScopesCreated++; return ValueTask.FromResult<ISunderRpcCallScope>(new Scope(this)); }
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SunderRpcProviderSnapshot?>(Available && endpoint == _provider.Endpoint ? _provider : null);
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(new SunderRpcCatalogSnapshot(1, 1, Available && contractId == _provider.ContractId ? [_provider] : [])); }
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public async ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Assert.True(Available); Assert.Equal(_provider.Endpoint, endpoint);
            var method = _descriptor.FindService(serviceId)!.FindMethod(methodId)!;
            Assert.True(_descriptor.IsValid(method.RequestSchemaReference, request, out var error), error);
            var response = await _registration.Handler.InvokeUnaryAsync(null!, serviceId, methodId, request, cancellationToken);
            Assert.True(_descriptor.IsValid(method.OutputSchemaReference, response, out error), error);
            LastResponse = response;
            return response;
        }
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private sealed class Scope(Loopback owner) : ISunderRpcCallScope
        {
            private bool _disposed;
            public DateTimeOffset DeadlineUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(1);
            public ValueTask DisposeAsync() { if (!_disposed) { _disposed = true; owner.ScopesDisposed++; } return ValueTask.CompletedTask; }
            public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
                => owner.GetProviderAsync(endpoint, cancellationToken);
            public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
                => owner.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);
            public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
                => owner.DiscoverAsync(contractId, cancellationToken);
            public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
            public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
                SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            { ObjectDisposedException.ThrowIf(_disposed, this); return owner.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken); }
            public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
                SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string path, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();
            public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
