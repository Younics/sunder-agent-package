using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Profile_Capability_Consumer;

namespace Sunder.Package.Agent.Memory.Semantic.Rpc;

[SunderRpcProvider(typeof(IProfileCapabilitiesService), ProviderId = "semantic.memory.profile.capabilities")]
public sealed partial class ProfileCapabilityProvider(IAgentProfileCapabilityConsumer consumer) : IProfileCapabilitiesService
{
    public ValueTask<Wire.ProfileCapabilityConsumerSnapshot> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new Wire.ProfileCapabilityConsumerSnapshot
        {
            ConsumerId = consumer.ConsumerId,
            DisplayName = consumer.DisplayName,
            Capabilities = consumer.ListConsumedCapabilities().Select(value => new Wire.AgentProfileCapabilityConsumerDescriptor
            {
                CapabilityKind = value.CapabilityKind, DisplayName = value.DisplayName, Description = value.Description,
            }).ToArray(),
        });
    }
}
