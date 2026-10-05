using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.tool.execution.observed",
    ServiceId = "tool-execution-observed", IncludeSchemaDialect = true)]
[SunderRpcRuntimeUse(Actions = ["discover", "invoke", "subscribe"])]
public interface ToolExecutionObservedService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<ObservedToolExecutionDescription> DescribeAsync(Unit request, CancellationToken cancellationToken = default);
    [SunderRpcMethod]
    IAsyncEnumerable<ObservedToolInvocationEvent> ExecuteAsync(ToolInvocationRequest request, CancellationToken cancellationToken = default);
}

public sealed class ObservedToolExecutionDescription
{
    public required Identifier SourceId { get; init; }
}

public sealed class ObservedToolInvocationEvent
{
    [SunderRpcFacet(AllowedStrings = ["dispatched", "result"])]
    public required string Kind { get; init; }
    public SunderRpcOptional<AgentToolResult> Result { get; init; }
}
