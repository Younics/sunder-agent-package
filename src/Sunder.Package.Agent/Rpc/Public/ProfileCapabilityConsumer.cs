using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderService(SunderServiceExposure.Public, ContractId = "younics.ai.agent.profile.capability.consumer", ServiceId = "profile-capability-consumer", IncludeSchemaDialect = true)]
[SunderRuntimeUse(Actions = ["discover", "invoke"])]
public interface ProfileCapabilityConsumerService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentProfileCapabilityConsumerSnapshot> DescribeAsync(CancellationToken cancellationToken = default);
}
