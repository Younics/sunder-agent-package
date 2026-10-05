using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Prompt_Context_Contributor;
using Wire = Sunder.Generated.Rpc.PromptContextContributor;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedPromptContextClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentPromptContextContributor, IAgentPromptContextAcknowledgmentSink, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.prompt-context.provider-stale");
    private Wire.PromptContextContributorDescription? _description;
    private Wire.PromptContextContributorDescription Description
    {
        get
        {
            _calls.ThrowIfStopped();
            return _description ??= _calls.Invoke((scope, token) => Client(scope).DescribeAsync(new(), token), default).AsTask().GetAwaiter().GetResult();
        }
    }

    public string ContributorId => Description.ContributorId;
    public string DisplayName => Description.DisplayName;
    public string AcknowledgmentSinkId => Description.AcknowledgmentSinkId ?? Description.ContributorId;

    public ValueTask<AgentPromptContextContribution?> ContributeContextAsync(AgentPromptContextRequest request, CancellationToken cancellationToken = default)
        => _calls.Invoke(async (scope, token) =>
        {
            var value = await Client(scope).ContributeAsync(AgentConsumerValues.Convert<Wire.PromptContextRequestWire>(new
            {
#pragma warning disable CS0618 // The frozen V1 prompt-context wire still carries the legacy turn summary.
                request.Session, request.Run, Turn = new { request.Turn.UserMessage, request.Turn.WorkingSummary },
#pragma warning restore CS0618
                request.Turns, request.RecentLiveBufferTurns, request.ContextPlan, request.Profile, request.Workspace,
                request.ExecutionBinding, request.AvailableTools, request.MemoryConsistencyBarrier, request.TranscriptEpoch,
                ExecutionTarget = request.ExecutionTargetReference?.ToHandle(),
            }), token).ConfigureAwait(false);
            return value is null ? null : AgentConsumerValues.Convert<AgentPromptContextContribution>(value);
        }, cancellationToken);

    public async ValueTask AcknowledgePromptContextAsync(AgentPromptContextReceipt receipt, CancellationToken cancellationToken = default)
        => _ = await _calls.Invoke((scope, token) => Client(scope).AcknowledgeAsync(
            AgentConsumerValues.Convert<Wire.AgentPromptContextReceipt>(receipt), token), cancellationToken).ConfigureAwait(false);

    private Wire.PromptContextContributorClient Client(ISunderRpcCallScope scope)
        => new PackageApi(scope).PromptContextContributor.CreateClient(scope, new Api.Provider(provider)).SPrompt_2dcontext_2dcontributor;

    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
