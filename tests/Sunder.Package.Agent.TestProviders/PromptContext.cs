using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IPromptContextContributorService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Prompt_Context_Contributor.IPromptContextContributorService;
using Sunder.Sdk.Services;

namespace RegressionTestGeneratedProviders;

[SunderServiceProvider(typeof(IPromptContextContributorService), ProviderId = "fixture.regression.prompt.context")]
public sealed partial class PromptContextProvider(Func<string, Type, object?[], CancellationToken, object> invoke) : IPromptContextContributorService
{
    private ValueTask<T> Reply<T>(string operation, object?[] arguments, CancellationToken token) => (ValueTask<T>)invoke(operation, typeof(T), arguments, token);
    private ValueTask ReplyVoid(string operation, object?[] arguments, CancellationToken token) => (ValueTask)invoke(operation, typeof(void), arguments, token);
    public ValueTask<AgentPromptContextContributorDescriptor> DescribeAsync(CancellationToken token = default) => Reply<AgentPromptContextContributorDescriptor>(nameof(DescribeAsync), [], token);
    public ValueTask<AgentPromptContextContribution?> ContributeAsync(AgentPromptContextRequest request, CancellationToken token = default) => Reply<AgentPromptContextContribution?>(nameof(ContributeAsync), [request], token);
    public ValueTask AcknowledgeAsync(AgentPromptContextReceipt request, CancellationToken token = default) => ReplyVoid(nameof(AcknowledgeAsync), [request], token);
}
