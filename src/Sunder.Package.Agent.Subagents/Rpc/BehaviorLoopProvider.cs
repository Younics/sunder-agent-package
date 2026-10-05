using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Behavior_Loop;

namespace Sunder.Package.Agent.Subagents.Rpc;

[SunderRpcProvider(typeof(IBehaviorLoopService), ProviderId = "subagents.behavior.loop")]
public sealed partial class BehaviorLoopProvider(OrchestratedAgentBehaviorLoop loop, ISunderRpcClient rpc) : IBehaviorLoopService
{
    public ValueTask<Wire.AgentBehaviorLoopDescriptor> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(SubagentProviderValues.Convert<Wire.AgentBehaviorLoopDescriptor>(loop.Descriptor)); }

    public async ValueTask<Wire.AgentBehaviorLoopResult> RunAsync(SunderRpcInvocationContext context, Wire.BehaviorLoopRunRequest request, CancellationToken token)
        => SubagentProviderValues.Convert<Wire.AgentBehaviorLoopResult>(await loop.RunWithDefaultLoopAsync(SubagentProviderValues.Convert<Domain.AgentBehaviorLoopContext>(request.Context),
            new RunControlClient(rpc, request.InvocationId, request.RunControl, context.DeadlineUtc), token).ConfigureAwait(false));
}
