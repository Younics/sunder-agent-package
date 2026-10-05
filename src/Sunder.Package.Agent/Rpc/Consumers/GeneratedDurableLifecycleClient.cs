using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Durable_Lifecycle_Observer;
using Wire = Sunder.Generated.Rpc.DurableLifecycleObserver;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedDurableLifecycleClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentDurableLifecycleObserver, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.lifecycle.provider-stale");
    private Wire.AgentRpcIdentity? _identity;

    private Wire.AgentRpcIdentity Identity
    {
        get
        {
            _calls.ThrowIfStopped();
            return _identity ??= _calls.Invoke((scope, token) => Client(scope).DescribeAsync(new(), token), default).AsTask().GetAwaiter().GetResult();
        }
    }
    public string ObserverId => Identity.Id;
    public string DisplayName => Identity.DisplayName;

    public async ValueTask HandleDurableLifecycleEventAsync(AgentDurableLifecycleEventEnvelope lifecycleEvent, CancellationToken cancellationToken = default)
        => _ = await _calls.Invoke((scope, token) => Client(scope).HandleAsync(
            AgentConsumerValues.Convert<Wire.AgentDurableLifecycleEventEnvelope>(lifecycleEvent), token),
            cancellationToken).ConfigureAwait(false);

    private Wire.DurableLifecycleObserverClient Client(ISunderRpcCallScope scope)
        => new PackageApi(scope).DurableLifecycleObserver.CreateClient(scope, new Api.Provider(provider)).SDurable_2dlifecycle_2dobserver;

    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();

}
