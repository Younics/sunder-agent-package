using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderService(SunderServiceExposure.Public, ContractId = "sunder.agent.tool.execution.observed", ServiceId = "tool-execution-observed", IncludeSchemaDialect = true)]
[SunderRuntimeUse(Actions = ["discover", "invoke", "subscribe"])]
public interface ToolExecutionObservedService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<string> DescribeAsync(CancellationToken cancellationToken = default);
    [SunderMethod(TimeoutSeconds = 2100)]
    IAsyncEnumerable<AgentObservedToolExecutionEvent> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request,
        CancellationToken cancellationToken = default);
}
