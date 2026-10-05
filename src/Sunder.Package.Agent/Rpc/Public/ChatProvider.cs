using Sunder.Sdk.Rpc;
using Wire = Sunder.Package.Agent.Rpc.Public.Chat;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.chat.provider",
    ServiceId = "chat-provider", IncludeSchemaDialect = true)]
[SunderRpcRuntimeUse(Actions = ["discover", "invoke", "subscribe"])]
public interface ChatProviderService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<Wire.AgentProviderDescriptor> DescribeAsync(Unit request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<Wire.ChatModels> ListModelsAsync(Unit request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<Wire.AgentProviderReadiness> GetReadinessAsync(Unit request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<Wire.AgentProviderRunCapabilities> GetCapabilitiesAsync(Wire.ChatCapabilitiesRequest request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<Wire.UtilityModelResolution> ResolveUtilityModelAsync(Unit request, CancellationToken cancellationToken = default);
    [SunderRpcMethod]
    IAsyncEnumerable<Wire.AgentChatStreamEvent> StreamChatAsync(Wire.AgentChatStreamRequest request, CancellationToken cancellationToken = default);
}
