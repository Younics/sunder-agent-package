using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.workspace.execution.resolver",
    ServiceId = "workspace-execution-resolver", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "agent.workspace.execution.resolver")]
public sealed partial class WorkspaceExecutionResolverService(IAgentWorkspaceExecutionResolver resolver)
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public async ValueTask<AgentWorkspaceRecordCollection> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        => new() { Value = (await resolver.ListWorkspacesAsync(cancellationToken).ConfigureAwait(false))
            .Select(AgentWorkspaceRecord.FromDomain).ToArray() };

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public async ValueTask<AgentWorkspaceExecutionResolutionWire> ResolveAsync(StringRequest request, CancellationToken cancellationToken = default)
    {
        var resolution = await resolver.ResolveAsync(request.Value.Value, cancellationToken).ConfigureAwait(false);
        var handle = resolution.ExecutionTargetHandle ?? resolution.ExecutionTargetReference?.ToHandle()
            ?? throw new InvalidOperationException("Workspace execution resolution did not retain an exact RPC target handle.");
        return new()
        {
            Binding = AgentWorkspaceBindingRecord.FromDomain(resolution.Binding),
            ExecutionTarget = AgentRpcProviderHandle.FromDomain(handle),
            Scope = AgentExecutionScopeDescriptor.FromDomain(resolution.Scope),
            Target = AgentExecutionTargetDescriptor.FromDomain(resolution.Target),
            Workspace = AgentWorkspaceRecord.FromDomain(resolution.Workspace),
        };
    }
}
