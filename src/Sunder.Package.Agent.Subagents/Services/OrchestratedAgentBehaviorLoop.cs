using Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Subagents.Services;

[SunderServiceProvider(typeof(IBehaviorLoopService), ProviderId = "subagents.behavior.loop")]
public sealed partial class OrchestratedAgentBehaviorLoop : IBehaviorLoopService
{
    public AgentBehaviorLoopDescriptor Descriptor { get; } = new()
    {
        LoopId = SubagentConstants.OrchestratedBehaviorLoopId,
        DisplayName = "Orchestrated",
        Description = "Enables task delegation to profile-selected subagents while preserving the base agent loop.",
        SourceId = SubagentConstants.PackageId,
        FeatureKinds = [SubagentConstants.FeatureKind],
        SettingsSchemaJson = null,
    };

    public ValueTask<AgentBehaviorLoopDescriptor> DescribeAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Descriptor); }

    public async ValueTask<AgentBehaviorLoopResult> RunAsync(CSunder_Agent_Behavior_Loop_BehaviorLoopRunRequest request,
        CancellationToken token = default)
    {
        // The reference fences the existing owner; InvocationId remains its separate run grant.
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, request.RunControl.RetirementToken);
        await request.RunControl.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
        var result = await request.RunControl.Service.RunDefaultAsync(new()
        { InvocationId = request.InvocationId, LoopContext = request.Context }, invocation.Token).ConfigureAwait(false);
        await request.RunControl.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
        return result;
    }
}
