using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IChildRunExecutorService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Child_Run_Executor.IChildRunExecutorService;
using Sunder.Sdk.Services;

namespace RegressionTestGeneratedProviders;

[SunderServiceProvider(typeof(IChildRunExecutorService), ProviderId = "fixture.regression.child.run")]
public sealed partial class ChildRunProvider(Func<string, Type, object?[], CancellationToken, object> invoke) : IChildRunExecutorService
{
    private ValueTask<T> Reply<T>(string operation, object?[] arguments, CancellationToken token) => (ValueTask<T>)invoke(operation, typeof(T), arguments, token);
    public ValueTask<AgentChildRunResult> RunChildAsync(AgentChildRunRequest request, CancellationToken token = default) => Reply<AgentChildRunResult>(nameof(RunChildAsync), [request], token);
}
