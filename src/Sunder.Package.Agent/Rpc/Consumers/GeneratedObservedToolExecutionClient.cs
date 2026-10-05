using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Tool_Execution_Observed;
using Wire = Sunder.Generated.Rpc.ToolExecutionObserved;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedObservedToolExecutionClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentObservedToolExecution, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.tool.observed-provider-stale");
    private string? _sourceId;
    public string SourceId
    {
        get
        {
            _calls.ThrowIfStopped();
            return _sourceId ??= _calls.Invoke(async (scope, token) => (await Client(scope).DescribeAsync(new(), token).ConfigureAwait(false)).SourceId,
                default).AsTask().GetAwaiter().GetResult();
        }
    }

    public async ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
    {
        await foreach (var item in _calls.Subscribe((scope, token) => Client(scope).ExecuteAsync(
                AgentToolValues.Invocation<Wire.ToolInvocationRequest>(context, request), token), cancellationToken, AgentToolValues.ExecutionOptions()).ConfigureAwait(false))
        {
            if (item.Kind == "dispatched")
            {
                context.DispatchStarted?.Invoke();
                continue;
            }
            if (item.Kind == "result")
                return item.Result is { } result ? AgentConsumerValues.Convert<AgentToolResult>(result)
                    : throw new InvalidOperationException("The observed tool execution returned an empty result event.");
            throw new InvalidOperationException($"The observed tool execution returned unknown event kind '{item.Kind}'.");
        }
        throw new InvalidOperationException("The observed tool execution ended without a result event.");
    }

    private Wire.ToolExecutionObservedClient Client(ISunderRpcCallScope scope)
        => new PackageApi(scope).ToolExecutionObserved.CreateClient(scope, new Api.Provider(provider)).STool_2dexecution_2dobserved;
    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
