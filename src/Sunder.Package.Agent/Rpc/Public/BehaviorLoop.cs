using Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderService(SunderServiceExposure.Public, ContractId = "younics.ai.agent.behavior.loop", ServiceId = "behavior-loop", IncludeSchemaDialect = true)]
[SunderRuntimeUse(Required = true, Actions = ["discover", "invoke"])]
public interface BehaviorLoopService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentBehaviorLoopDescriptor> DescribeAsync(CancellationToken cancellationToken = default);

    [SunderMethod(TimeoutSeconds = 2100)]
    ValueTask<AgentBehaviorLoopResult> RunAsync(AgentBehaviorLoopContext context, Guid invocationId,
        ServiceReference<IRunControlService> runControl, CancellationToken cancellationToken = default);
}
