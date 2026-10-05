using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Selectable_Capability_Provider;
using Wire = Sunder.Generated.Rpc.SelectableCapabilityProvider;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedSelectableCapabilityClient : IAgentProfileSelectableCapabilityProvider,
    IAgentProfileSelectableCapabilityChangeNotifier, IDisposable, IAsyncDisposable
{
    private readonly SunderRpcProviderSnapshot _provider;
    private readonly GeneratedProviderCallLifetime _calls;
    private readonly Task _watch;
    private readonly object _gate = new();
    private Task? _stop;
    private Wire.AgentRpcIdentity? _identity;

    public GeneratedSelectableCapabilityClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    {
        _provider = provider;
        _calls = new(rpc, provider, "agent.selectable.provider-stale");
        _watch = Task.Run(WatchAsync);
    }

    private Wire.AgentRpcIdentity Identity
    {
        get
        {
            _calls.ThrowIfStopped();
            return _identity ??= _calls.Invoke((scope, token) => Client(scope).DescribeAsync(new(), token), default).AsTask().GetAwaiter().GetResult();
        }
    }
    public string ProviderId => Identity.Id;
    public string DisplayName => Identity.DisplayName;
    public event Action? SelectableCapabilitiesChanged;
    internal Task ObservationTask => _watch;

    public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(
        AgentProfileSelectableCapabilityRequest request, CancellationToken cancellationToken = default)
        => _calls.Invoke<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>(async (scope, token) =>
            AgentConsumerValues.Convert<AgentProfileSelectableCapabilityDescriptor[]>(await Client(scope).ListAsync(
                AgentConsumerValues.Convert<Wire.AgentProfileSelectableCapabilityRequest>(request), token).ConfigureAwait(false)), cancellationToken);

    private Wire.SelectableCapabilityProviderClient Client(ISunderRpcCallScope scope)
        => new PackageApi(scope).SelectableCapabilityProvider.CreateClient(scope, new Api.Provider(_provider)).SSelectable_2dcapability_2dprovider;

    private async Task WatchAsync()
    {
        try
        {
            await foreach (var _ in _calls.Subscribe((scope, token) => Client(scope).WatchAsync(new(), token)).ConfigureAwait(false))
                SelectableCapabilitiesChanged?.Invoke();
        }
        catch (Exception exception) when (exception is OperationCanceledException or SunderRpcException) { }
    }

    public void Dispose() => _ = DisposeAsync();
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_stop ??= StopAsync());
    }
    private async Task StopAsync()
    {
        try { await _calls.DisposeAsync().ConfigureAwait(false); }
        finally { await _watch.ConfigureAwait(false); }
    }
}
