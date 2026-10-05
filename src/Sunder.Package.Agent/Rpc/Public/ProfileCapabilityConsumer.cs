using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.profile.capability.consumer",
    ServiceId = "profile-capability-consumer", IncludeSchemaDialect = true)]
[SunderRpcRuntimeUse(Actions = ["discover", "invoke"])]
public interface ProfileCapabilityConsumerService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<ProfileCapabilityConsumerSnapshot> DescribeAsync(Unit request, CancellationToken cancellationToken = default);
}

public sealed class AgentProfileCapabilityConsumerDescriptor
{
    public required TextValue CapabilityKind { get; init; }
    public required DisplayText Description { get; init; }
    public required DisplayText DisplayName { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentProfileCapabilityConsumerDescriptorCollection
{
    public required IReadOnlyList<AgentProfileCapabilityConsumerDescriptor> Value { get; init; }
}

public sealed class ProfileCapabilityConsumerSnapshot
{
    public required AgentProfileCapabilityConsumerDescriptorCollection Capabilities { get; init; }
    public required Identifier ConsumerId { get; init; }
    public required DisplayText DisplayName { get; init; }
}
