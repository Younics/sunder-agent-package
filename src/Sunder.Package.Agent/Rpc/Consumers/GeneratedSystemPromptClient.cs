using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_System_Prompt_Contributor;
using Wire = Sunder.Generated.Rpc.SystemPromptContributor;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedSystemPromptClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentSystemPromptContributor, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.system-prompt.provider-stale");
    private Wire.AgentRpcIdentity? _identity;
    private Wire.AgentRpcIdentity Identity
    {
        get
        {
            _calls.ThrowIfStopped();
            return _identity ??= _calls.Invoke((scope, token) => Client(scope).DescribeAsync(new(), token), default).AsTask().GetAwaiter().GetResult();
        }
    }
    public string ContributorId => Identity.Id;
    public string DisplayName => Identity.DisplayName;

    public ValueTask<IReadOnlyList<AgentSystemPromptBlock>> ContributeAsync(AgentSystemPromptRequest request, CancellationToken cancellationToken = default)
        => _calls.Invoke<IReadOnlyList<AgentSystemPromptBlock>>(async (scope, token) =>
            (await Client(scope).ContributeAsync(AgentConsumerValues.Convert<Wire.AgentSystemPromptRequest>(request), token).ConfigureAwait(false))
                .Select(value => new AgentSystemPromptBlock(value.BlockId, value.Title, value.Content,
                    checked((int)value.Priority), value.Required, checked((int?)value.MaxChars), value.SourceId,
                    value.Metadata?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))).ToArray(), cancellationToken);

    private Wire.SystemPromptContributorClient Client(ISunderRpcCallScope scope)
        => new PackageApi(scope).SystemPromptContributor.CreateClient(scope, new Api.Provider(provider)).SSystem_2dprompt_2dcontributor;

    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
