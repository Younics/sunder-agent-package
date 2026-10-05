using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Subagents.Rpc;

public sealed partial class SubagentRuntimeBindings
{
    private readonly Dictionary<string, Reference<ISubagentChatCatalog>> _chats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reference<ISubagentToolCatalog>> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reference<IAgentProfileSelectableCapabilityProvider>> _capabilities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reference<ISubagentBehaviorCatalog>> _loops = new(StringComparer.Ordinal);
    public IReadOnlyList<SubagentProviderReference<ISubagentChatCatalog>> GetChatProviders()
    {
        if (!IsStarted()) return [];
        var contract = _api.CSunder_Agent_Chat_Provider;
        try
        {
            var snapshot = contract.ListAsync(_lifetime.Token).AsTask().GetAwaiter().GetResult();
            return Reconcile(_chats, snapshot.Providers.Select(value => (value.Snapshot, Value: value)).ToArray(), snapshot.ResetRequired,
                value => new(value.Snapshot, token => contract.GetAsync(value, token).AsTask().GetAwaiter().GetResult()?.Snapshot,
                    token => new ChatCatalogClient(_rpc, contract, value, token)));
        }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.Unavailable) { Invalidate(_chats); return []; }
    }
    public IReadOnlyList<SubagentProviderReference<ISubagentToolCatalog>> GetToolSources()
    {
        if (!IsStarted()) return [];
        var contract = _api.CSunder_Agent_Tool_Source;
        try
        {
            var snapshot = contract.ListAsync(_lifetime.Token).AsTask().GetAwaiter().GetResult();
            return Reconcile(_tools, snapshot.Providers.Select(value => (value.Snapshot, Value: value)).ToArray(), snapshot.ResetRequired,
                value => new(value.Snapshot, token => contract.GetAsync(value, token).AsTask().GetAwaiter().GetResult()?.Snapshot,
                    token => new ToolCatalogClient(_rpc, contract, value, token)));
        }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.Unavailable) { Invalidate(_tools); return []; }
    }
    public IReadOnlyList<SubagentProviderReference<IAgentProfileSelectableCapabilityProvider>> GetSelectableCapabilityProviders()
    {
        if (!IsStarted()) return [];
        var contract = _api.CSunder_Agent_Selectable_Capability_Provider;
        try
        {
            var snapshot = contract.ListAsync(_lifetime.Token).AsTask().GetAwaiter().GetResult();
            return Reconcile(_capabilities, snapshot.Providers.Select(value => (value.Snapshot, Value: value)).ToArray(), snapshot.ResetRequired,
                value => new(value.Snapshot, token => contract.GetAsync(value, token).AsTask().GetAwaiter().GetResult()?.Snapshot,
                    token => new CapabilityCatalogClient(_rpc, contract, value, token)));
        }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.Unavailable) { Invalidate(_capabilities); return []; }
    }
    public IReadOnlyList<SubagentProviderReference<ISubagentBehaviorCatalog>> GetBehaviorLoops()
    {
        if (!IsStarted()) return [];
        var contract = _api.CSunder_Agent_Behavior_Loop;
        try
        {
            var snapshot = contract.ListAsync(_lifetime.Token).AsTask().GetAwaiter().GetResult();
            return Reconcile(_loops, snapshot.Providers.Select(value => (value.Snapshot, Value: value)).ToArray(), snapshot.ResetRequired,
                value => new(value.Snapshot, token => contract.GetAsync(value, token).AsTask().GetAwaiter().GetResult()?.Snapshot,
                    token => new BehaviorCatalogClient(_rpc, contract, value, token)));
        }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.Unavailable) { Invalidate(_loops); return []; }
    }
    private IEnumerable<Task> AdditionalWatches()
        => [Task.Run(() => WatchAsync(ChatChanges, _chats, SubagentCatalogChange.ChatProviders)),
            Task.Run(() => WatchAsync(ToolChanges, _tools, SubagentCatalogChange.Tools)),
            Task.Run(() => WatchAsync(CapabilityChanges, _capabilities, SubagentCatalogChange.SelectableCapabilities)),
            Task.Run(() => WatchAsync(BehaviorChanges, _loops, SubagentCatalogChange.BehaviorLoops))];
    private IEnumerable<IRetirable> AdditionalReferences()
        => _chats.Values.Cast<IRetirable>().Concat(_tools.Values).Concat(_capabilities.Values).Concat(_loops.Values);
    private void ClearAdditionalReferences() { _chats.Clear(); _tools.Clear(); _capabilities.Clear(); _loops.Clear(); }
    private async IAsyncEnumerable<Change> ChatChanges(long revision, long sequence, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var value in _api.CSunder_Agent_Chat_Provider.WatchAsync(revision, sequence, token).ConfigureAwait(false))
            yield return new(value.Revision, value.Sequence, value.Kind, value.Provider?.Snapshot);
    }
    private async IAsyncEnumerable<Change> ToolChanges(long revision, long sequence, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var value in _api.CSunder_Agent_Tool_Source.WatchAsync(revision, sequence, token).ConfigureAwait(false))
            yield return new(value.Revision, value.Sequence, value.Kind, value.Provider?.Snapshot);
    }
    private async IAsyncEnumerable<Change> CapabilityChanges(long revision, long sequence, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var value in _api.CSunder_Agent_Selectable_Capability_Provider.WatchAsync(revision, sequence, token).ConfigureAwait(false))
            yield return new(value.Revision, value.Sequence, value.Kind, value.Provider?.Snapshot);
    }
    private async IAsyncEnumerable<Change> BehaviorChanges(long revision, long sequence, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var value in _api.CSunder_Agent_Behavior_Loop.WatchAsync(revision, sequence, token).ConfigureAwait(false))
            yield return new(value.Revision, value.Sequence, value.Kind, value.Provider?.Snapshot);
    }
}
