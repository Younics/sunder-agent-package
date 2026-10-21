using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderService(SunderServiceExposure.Public, ContractId = "younics.ai.agent.prompt.context.contributor", ServiceId = "prompt-context-contributor", IncludeSchemaDialect = true)]
[SunderRuntimeUse(Actions = ["discover", "invoke"])]
public interface PromptContextContributorService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentPromptContextContributorDescriptor> DescribeAsync(CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentPromptContextContribution?> ContributeAsync(AgentPromptContextRequest request, CancellationToken cancellationToken = default);
    ValueTask AcknowledgeAsync(AgentPromptContextReceipt request, CancellationToken cancellationToken = default);
}
