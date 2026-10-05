using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.tool.source", ServiceId = "tool-source",
    IncludeSchemaDialect = true, AdditionalDefinitions = [typeof(CollectionIndex)])]
[SunderRpcAppUse(Actions = ["discover", "invoke"])]
[SunderRpcRuntimeUse(Actions = ["discover", "invoke"])]
public interface ToolSourceService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<ToolSourceDescription> DescribeAsync(Unit request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentToolDescriptorCollection> ListToolsAsync(AgentToolSourceContextWire request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<ToolReadinessResult> GetReadinessAsync(ToolReadinessRequest request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<ToolPreflightResult> PreflightAsync(ToolInvocationRequest request, CancellationToken cancellationToken = default);
    [SunderRpcMethod]
    ValueTask<AgentToolResult> ExecuteAsync(ToolInvocationRequest request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<PermissionRequestResult> PermissionAsync(ToolInvocationRequest request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<ToolPresentationResult> PresentationAsync(AgentToolPresentationRequest request, CancellationToken cancellationToken = default);
}
