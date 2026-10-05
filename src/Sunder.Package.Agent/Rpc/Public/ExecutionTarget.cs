using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.execution.target",
    ServiceId = "execution-target", IncludeSchemaDialect = true, AdditionalDefinitions = [typeof(CollectionIndex)])]
[SunderRpcDomainError("agent.execution.facet-unavailable",
    Description = "The execution target does not implement the requested optional facet.")]
[SunderRpcRuntimeUse(Actions = ["discover", "invoke"])]
public interface ExecutionTargetService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentExecutionTargetDescriptor> DescribeAsync(Unit request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<ConfigurationGenerationResolution> ConfigurationGenerationAsync(AgentExecutionTargetContextWire request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentExecutionTargetReadiness> ReadinessAsync(AgentExecutionTargetContextWire request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentExecutionShellDescriptor> ShellAsync(AgentExecutionTargetContextWire request, CancellationToken cancellationToken = default);

    [SunderRpcMethod]
    ValueTask<AgentResolvedResourceWire> ResolveFileAsync(ExecutionPathRequest request, CancellationToken cancellationToken = default);

    [SunderRpcMethod]
    ValueTask<AgentShellCommandResult> ExecuteShellAsync(ExecutionShellRequest request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentFileReadResult> ReadFileAsync(ExecutionFileReadRequest request, CancellationToken cancellationToken = default);

    [SunderRpcMethod]
    ValueTask<AgentFileMutationResultWire> WriteFileAsync(ExecutionFileWriteRequest request, CancellationToken cancellationToken = default);

    [SunderRpcMethod]
    ValueTask<AgentFileMutationResultWire> DeleteFileAsync(ExecutionFileDeleteRequest request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<AgentShellCommandResult> ExecuteProcessAsync(ExecutionProcessRequest request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<AgentResolvedExecutionResourceCollection> ResolveResourcesAsync(ExecutionResourcesRequest request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(AgentExecutionTargetContextWire request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<Unit> ReleaseResourceAuthorityAsync(ResourceCapabilitiesRequest request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<AgentExecutionScopeDescriptor> ExecutionScopeAsync(AgentExecutionTargetContextWire request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<AgentExecutionPathMapping> MapPathAsync(ExecutionPathRequest request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<TextValues> ListPathAsync(AgentExecutionTargetContextWire request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<Unit> AddPathAsync(ExecutionPathRequest request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<AgentFileSearchResult> StructuredFileSearchAsync(ExecutionFileSearchRequest request, CancellationToken cancellationToken = default);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["agent.execution.facet-unavailable"])]
    ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverInstructionsAsync(ExecutionInstructionDiscoveryRequest request, CancellationToken cancellationToken = default);
}
