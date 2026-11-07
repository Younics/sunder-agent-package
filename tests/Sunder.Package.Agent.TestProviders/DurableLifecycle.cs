using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IDurableLifecycleObserverService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Durable_Lifecycle_Observer.IDurableLifecycleObserverService;
using Sunder.Sdk.Services;

namespace RegressionTestGeneratedProviders;

[SunderServiceProvider(typeof(IDurableLifecycleObserverService), ProviderId = "fixture.regression.durable.lifecycle")]
public sealed partial class DurableLifecycleProvider(Func<string, Type, object?[], CancellationToken, object> invoke) : IDurableLifecycleObserverService
{
    private ValueTask<T> Reply<T>(string operation, object?[] arguments, CancellationToken token) => (ValueTask<T>)invoke(operation, typeof(T), arguments, token);
    private ValueTask ReplyVoid(string operation, object?[] arguments, CancellationToken token) => (ValueTask)invoke(operation, typeof(void), arguments, token);
    public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken token = default) => Reply<AgentServiceIdentity>(nameof(DescribeAsync), [], token);
    public ValueTask HandleAsync(AgentDurableLifecycleEventEnvelope request, CancellationToken token = default) => ReplyVoid(nameof(HandleAsync), [request], token);
}
