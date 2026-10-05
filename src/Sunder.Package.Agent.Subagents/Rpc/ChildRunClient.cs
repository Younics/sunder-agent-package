using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Child_Run_Executor;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Child_Run_Executor;

namespace Sunder.Package.Agent.Subagents.Rpc;

internal sealed class ChildRunClient(ISunderRpcClient rpc, Api.Contract contract, Api.Provider provider, CancellationToken retirement) : IAgentChildRunExecutor
{
    public async ValueTask<AgentChildRunResult> RunChildAsync(AgentChildRunRequest request, CancellationToken cancellationToken = default)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retirement);
        invocation.Token.ThrowIfCancellationRequested();
        await using var scope = await rpc.CreateCallScopeAsync(new(DateTimeOffset.UtcNow.AddMinutes(35)), invocation.Token).ConfigureAwait(false);
        await RequireCurrent(invocation.Token).ConfigureAwait(false);
        var result = await contract.CreateClient(scope, provider).SChild_2drun_2dexecutor.RunChildAsync(
            SubagentProviderValues.Convert<Wire.AgentChildRunRequest>(request), invocation.Token).ConfigureAwait(false);
        invocation.Token.ThrowIfCancellationRequested();
        await RequireCurrent(invocation.Token).ConfigureAwait(false);
        return SubagentProviderValues.Convert<AgentChildRunResult>(result);
    }
    private async ValueTask RequireCurrent(CancellationToken token)
    {
        var current = await contract.GetAsync(provider, token).ConfigureAwait(false);
        if (current is null || current.State != SunderRpcProviderState.Active || current.SessionGeneration != provider.SessionGeneration)
            throw new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "subagents.child-run.stale", "The child-run executor activation is stale."));
    }
}
