using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentProfileCapabilityConsumer
{
    string ConsumerId { get; }

    string DisplayName { get; }

    IReadOnlyList<AgentProfileCapabilityConsumerDescriptor> ListConsumedCapabilities();
}
