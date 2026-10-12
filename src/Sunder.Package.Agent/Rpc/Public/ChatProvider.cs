using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderService(SunderServiceExposure.Public, ContractId = "sunder.agent.chat.provider", ServiceId = "chat-provider", IncludeSchemaDialect = true)]
[SunderRuntimeUse(Actions = ["discover", "invoke", "subscribe"])]
public interface ChatProviderService
{
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentProviderDescriptor> DescribeAsync(CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<IReadOnlyList<AgentModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<AgentProviderRunCapabilities> GetCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default);
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    ValueTask<string?> ResolveUtilityModelAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentChatStreamEvent> StreamChatAsync(AgentChatStreamRequest request, CancellationToken cancellationToken = default);
}
