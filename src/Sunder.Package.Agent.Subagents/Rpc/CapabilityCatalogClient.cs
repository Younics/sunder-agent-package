using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Selectable_Capability_Provider;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Selectable_Capability_Provider;

namespace Sunder.Package.Agent.Subagents.Rpc;

internal sealed class CapabilityCatalogClient : IAgentProfileSelectableCapabilityProvider, IAgentProfileSelectableCapabilityChangeNotifier, IAsyncDisposable
{
    private readonly ISunderRpcClient _rpc;
    private readonly Api.Contract _contract;
    private readonly Api.Provider _provider;
    private readonly CancellationTokenSource _lifetime;
    private readonly CatalogClientInvocation<Wire.SelectableCapabilityProviderClient> _calls;
    private readonly Task _watch;
    private Wire.AgentRpcIdentity? _description;
    internal CapabilityCatalogClient(ISunderRpcClient rpc, Api.Contract contract, Api.Provider provider, CancellationToken retirement)
    {
        _rpc = rpc; _contract = contract; _provider = provider;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(retirement);
        _calls = new(rpc, scope => contract.CreateClient(scope, provider).SSelectable_2dcapability_2dprovider,
            async token => await contract.GetAsync(provider, token).ConfigureAwait(false) is { State: SunderRpcProviderState.Active } current && current.SessionGeneration == provider.SessionGeneration, _lifetime.Token);
        _watch = Task.Run(WatchAsync);
    }
    private Wire.AgentRpcIdentity Description => _description ??= _calls.Invoke((client, token) => client.DescribeAsync(new(), token));
    public string ProviderId => Description.Id;
    public string DisplayName => Description.DisplayName;
    public event Action? SelectableCapabilitiesChanged;
    public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(AgentProfileSelectableCapabilityRequest request, CancellationToken cancellationToken = default)
        => _calls.InvokeAsync<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>(async (client, token) => SubagentProviderValues.Convert<AgentProfileSelectableCapabilityDescriptor[]>(
            await client.ListAsync(SubagentProviderValues.Convert<Wire.AgentProfileSelectableCapabilityRequest>(request), token).ConfigureAwait(false)), cancellationToken);
    private async Task WatchAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await _calls.RequireCurrent(_lifetime.Token).ConfigureAwait(false);
                await using var scope = await _rpc.CreateCallScopeAsync(cancellationToken: _lifetime.Token).ConfigureAwait(false);
                await foreach (var _ in _contract.CreateClient(scope, _provider).SSelectable_2dcapability_2dprovider.WatchAsync(new(), _lifetime.Token).ConfigureAwait(false)) Notify();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is OperationCanceledException or SunderRpcException) { Notify(); }
            try { await Task.Delay(250, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
    }
    private void Notify()
    {
        if (SelectableCapabilitiesChanged is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        { try { handler(); } catch { /* Independent listeners cannot stop catalog change observation. */ } }
    }
    public async ValueTask DisposeAsync()
    {
        try { await _lifetime.CancelAsync().ConfigureAwait(false); await _watch.ConfigureAwait(false); }
        finally { _lifetime.Dispose(); }
    }
}
