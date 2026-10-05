using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Run_Control;
using Handle = Sunder.Generated.Rpc.Imports.CSunder_Agent_Behavior_Loop.AgentRpcProviderHandle;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Run_Control;

namespace Sunder.Package.Agent.Subagents.Rpc;

internal sealed class RunControlClient(ISunderRpcClient rpc, Guid invocationId, Handle selected, DateTimeOffset callerDeadlineUtc)
    : IAgentInnerBehaviorLoopRuntime
{
    public async ValueTask<AgentBehaviorLoopResult> RunDefaultLoopAsync(AgentBehaviorLoopContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(35);
        if (callerDeadlineUtc < deadline) deadline = callerDeadlineUtc;
        await using var scope = await rpc.CreateCallScopeAsync(new(deadline), cancellationToken).ConfigureAwait(false);
        var contract = new AgentPackageApi(scope).CSunder_Agent_Run_Control;
        var snapshot = await scope.GetProviderAsync(new(selected.EndpointReference), cancellationToken).ConfigureAwait(false);
        RequireSelected(snapshot);
        var provider = new Api.Provider(snapshot!);
        RequireSelected((await contract.GetAsync(provider, cancellationToken).ConfigureAwait(false))?.Snapshot);
        var value = await contract.CreateClient(scope, provider).SRun_2dcontrol.RunDefaultAsync(new Wire.RunControlDefaultLoopRequest
        {
            Invocation = new() { InvocationId = invocationId },
            Context = SubagentProviderValues.Convert<Wire.AgentBehaviorLoopContext>(context),
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RequireSelected((await contract.GetAsync(provider, cancellationToken).ConfigureAwait(false))?.Snapshot);
        return SubagentProviderValues.Convert<AgentBehaviorLoopResult>(value);
    }

    private void RequireSelected(SunderRpcProviderSnapshot? value)
    {
        if (value is not null && value.State == SunderRpcProviderState.Active && selected.EndpointReference == value.Endpoint.Value
            && selected.PackageId == value.PackageId && selected.PackageVersion == value.PackageVersion && selected.ProviderId == value.ProviderId
            && selected.ContractId == value.ContractId && selected.ContractVersion == value.ContractVersion
            && selected.DescriptorSha256 == value.DescriptorSha256 && selected.AbiSha256 == value.AbiSha256
            && selected.ActivationId == value.ActivationId && selected.ActivationEpoch == value.ActivationEpoch && selected.SessionGeneration == value.SessionGeneration) return;
        throw new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "agent.run-control.stale", "The Agent run-control provider activation is stale."));
    }
}
