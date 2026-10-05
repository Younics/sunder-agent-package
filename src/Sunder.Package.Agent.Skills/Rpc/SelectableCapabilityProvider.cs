using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Selectable_Capability_Provider;

namespace Sunder.Package.Agent.Skills.Rpc;

[SunderRpcProvider(typeof(ISelectableCapabilitiesService), ProviderId = "skills.selectable.capabilities")]
public sealed partial class SelectableCapabilityProvider(IAgentProfileSelectableCapabilityProvider provider) : ISelectableCapabilitiesService
{
    public ValueTask<Wire.AgentRpcIdentity> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new Wire.AgentRpcIdentity { Id = provider.ProviderId, DisplayName = provider.DisplayName }); }

    public async ValueTask<IReadOnlyList<Wire.AgentProfileSelectableCapabilityDescriptor>> ListAsync(SunderRpcInvocationContext context, Wire.AgentProfileSelectableCapabilityRequest request, CancellationToken token)
        => SkillsProviderValues.Convert<Wire.AgentProfileSelectableCapabilityDescriptor[]>(await provider.ListCapabilitiesAsync(
            SkillsProviderValues.Convert<Domain.AgentProfileSelectableCapabilityRequest>(request), token).ConfigureAwait(false));

    public async IAsyncEnumerable<Wire.AgentRpcChange> WatchAsync(SunderRpcInvocationContext context, Wire.Unit request, [EnumeratorCancellation] CancellationToken token)
    {
        if (provider is not IAgentProfileSelectableCapabilityChangeNotifier notifier)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            yield break;
        }
        var channel = Channel.CreateBounded<Wire.AgentRpcChange>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        long revision = 0;
        void Changed() => channel.Writer.TryWrite(new() { Revision = Interlocked.Increment(ref revision) });
        notifier.SelectableCapabilitiesChanged += Changed;
        try
        {
            await foreach (var value in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            { token.ThrowIfCancellationRequested(); yield return value; }
        }
        finally { notifier.SelectableCapabilitiesChanged -= Changed; channel.Writer.TryComplete(); }
    }
}
