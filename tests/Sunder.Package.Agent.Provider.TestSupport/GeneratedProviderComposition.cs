using System.Reflection;
using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Provider.TestSupport;

public static class GeneratedProviderComposition
{
    public static ISunderAppPackageModule App(Assembly role)
        => (ISunderAppPackageModule)Activator.CreateInstance(role.GetType("Sunder.Generated.Package.AppModule", throwOnError: true)!)!;

    public static SunderWorkerOptions Worker(Assembly role, IPackageContext context)
        => (SunderWorkerOptions)role.GetType("Sunder.Generated.Package.WorkerEntryPoint", throwOnError: true)!
            .GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic, [typeof(IPackageContext), typeof(ISunderRpcClient)])!
            .Invoke(null, [context, new UnusedRpcClient()])!;

    private sealed class UnusedRpcClient : ISunderRpcClient
    {
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Provider composition must not resolve packages before activation.");
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Provider composition must not report package faults before activation.");
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Provider composition must not discover packages before activation.");
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Provider composition must not invoke packages before activation.");
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Provider composition must not subscribe before activation.");
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Provider composition must not watch packages before activation.");
    }
}
