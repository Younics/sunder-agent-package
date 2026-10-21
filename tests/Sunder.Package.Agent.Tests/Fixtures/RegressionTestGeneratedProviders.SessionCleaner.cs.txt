using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using ISessionCleanerService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Session_Cleaner.ISessionCleanerService;
using Sunder.Sdk.Services;

namespace RegressionTestGeneratedProviders;

[SunderServiceProvider(typeof(ISessionCleanerService), ProviderId = "fixture.regression.session.cleaner")]
public sealed partial class SessionCleanerProvider(Func<string, Type, object?[], CancellationToken, object> invoke) : ISessionCleanerService
{
    private ValueTask<T> Reply<T>(string operation, object?[] arguments, CancellationToken token) => (ValueTask<T>)invoke(operation, typeof(T), arguments, token);
    private ValueTask ReplyVoid(string operation, object?[] arguments, CancellationToken token) => (ValueTask)invoke(operation, typeof(void), arguments, token);
    public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken token = default) => Reply<AgentServiceIdentity>(nameof(DescribeAsync), [], token);
    public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken token = default) => ReplyVoid(nameof(DeleteSessionAsync), [sessionId], token);
}
