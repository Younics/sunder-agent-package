using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderService(SunderServiceExposure.Public, ContractId = "sunder.agent.selectable.capability.provider", ServiceId = "selectable-capability-provider", IncludeSchemaDialect = true)]
[SunderRuntimeUse(Actions = ["discover", "invoke", "subscribe"])]
public interface SelectableCapabilityProviderService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListAsync(AgentProfileRecord? profile, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    IAsyncEnumerable<AgentCapabilityCatalogChange> WatchAsync(CancellationToken cancellationToken = default);
}
