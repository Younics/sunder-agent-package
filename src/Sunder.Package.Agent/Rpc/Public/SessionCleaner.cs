using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderService(SunderServiceExposure.Public, ContractId = "younics.ai.agent.session.cleaner", ServiceId = "session-cleaner", IncludeSchemaDialect = true)]
[SunderRuntimeUse(Actions = ["discover", "invoke"])]
public interface SessionCleanerService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
