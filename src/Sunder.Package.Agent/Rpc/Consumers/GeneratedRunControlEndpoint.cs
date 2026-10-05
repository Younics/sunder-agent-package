using Sunder.Generated.Rpc;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Run_Control;
using Wire = Sunder.Generated.Rpc.RunControl;

namespace Sunder.Package.Agent.Runtime.Rpc;

// Shared by caller-bound invocation adapters; the catalog owns this endpoint lifetime.
internal sealed class GeneratedRunControlEndpoint(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider) : IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.run-control.stale", "The Agent run-control provider activation is stale.");
    internal SunderRpcEndpointReference Endpoint => provider.Endpoint;
    internal static SunderRpcCallOptions ExtendedOptions() => new(DateTimeOffset.UtcNow.AddMinutes(35));
    internal ValueTask<T> Invoke<T>(Func<Wire.RunControlClient, CancellationToken, ValueTask<T>> invoke, CancellationToken token, bool extended)
        => _calls.Invoke((scope, cancellation) => invoke(Client(scope), cancellation), token, extended ? ExtendedOptions() : null);
    internal IAsyncEnumerable<T> Subscribe<T>(Func<ISunderRpcCallScope, Wire.RunControlClient, CancellationToken, IAsyncEnumerable<T>> subscribe, CancellationToken token)
        => _calls.Subscribe((scope, cancellation) => subscribe(scope, Client(scope), cancellation), token);
    private Wire.RunControlClient Client(ISunderRpcCallScope scope)
        => new PackageApi(scope).RunControl.CreateClient(scope, new Api.Provider(provider)).SRun_2dcontrol;
    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
