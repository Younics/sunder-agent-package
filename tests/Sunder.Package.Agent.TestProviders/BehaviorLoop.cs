using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IBehaviorLoopService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Behavior_Loop.IBehaviorLoopService;
using Sunder.Sdk.Services;

namespace RegressionTestGeneratedProviders;

[SunderServiceProvider(typeof(IBehaviorLoopService), ProviderId = "fixture.regression.behavior.loop")]
public sealed partial class BehaviorLoopProvider(Func<string, Type, object?[], CancellationToken, object> invoke) : IBehaviorLoopService
{
    private ValueTask<T> Reply<T>(string operation, object?[] arguments, CancellationToken token) => (ValueTask<T>)invoke(operation, typeof(T), arguments, token);
    public ValueTask<AgentBehaviorLoopDescriptor> DescribeAsync(CancellationToken token = default) => Reply<AgentBehaviorLoopDescriptor>(nameof(DescribeAsync), [], token);
    public ValueTask<AgentBehaviorLoopResult> RunAsync(CYounics_Ai_Agent_Behavior_Loop_BehaviorLoopRunRequest request, CancellationToken token = default) => Reply<AgentBehaviorLoopResult>(nameof(RunAsync), [request], token);
}
