using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IToolExecutionObservedService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Execution_Observed.IToolExecutionObservedService;
using Sunder.Sdk.Services;

namespace RegressionTestGeneratedProviders;

[SunderServiceProvider(typeof(IToolExecutionObservedService), ProviderId = "fixture.regression.observed.tool")]
public sealed partial class ObservedToolProvider(Func<string, Type, object?[], CancellationToken, object> invoke) : IToolExecutionObservedService
{
    private ValueTask<T> Reply<T>(string operation, object?[] arguments, CancellationToken token) => (ValueTask<T>)invoke(operation, typeof(T), arguments, token);
    private IAsyncEnumerable<T> Stream<T>(string operation, object?[] arguments, CancellationToken token) => (IAsyncEnumerable<T>)invoke(operation, typeof(T), arguments, token);
    public ValueTask<string> DescribeAsync(CancellationToken token = default) => Reply<string>(nameof(DescribeAsync), [], token);
    public IAsyncEnumerable<AgentObservedToolExecutionEvent> ExecuteAsync(CYounics_Ai_Agent_Tool_Execution_Observed_ToolExecutionObservedExecuteRequest request, CancellationToken token = default) => Stream<AgentObservedToolExecutionEvent>(nameof(ExecuteAsync), [request], token);
}
