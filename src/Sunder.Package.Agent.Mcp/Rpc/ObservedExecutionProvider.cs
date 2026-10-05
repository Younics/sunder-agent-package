using System.Runtime.CompilerServices;
using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Tool_Execution_Observed;
using Tools = Sunder.Generated.Rpc.Imports.CSunder_Agent_Tool_Source;

namespace Sunder.Package.Agent.Mcp.Rpc;

[SunderRpcProvider(typeof(IObservedExecutionService), ProviderId = "mcp.tools.observed")]
internal sealed partial class ObservedExecutionProvider(IAgentObservedToolExecution source) : IObservedExecutionService
{
    public ValueTask<Wire.ObservedToolExecutionDescription> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new Wire.ObservedToolExecutionDescription { SourceId = source.SourceId }); }
    public async IAsyncEnumerable<Wire.ObservedToolInvocationEvent> ExecuteAsync(SunderRpcInvocationContext invocation, Wire.ToolInvocationRequest request, [EnumeratorCancellation] CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signaled = 0;
        var context = ToolSourceProvider.ExecutionContext(McpProviderValues.Convert<Tools.AgentToolExecutionContextWire>(request.Context)) with
        {
            DispatchStarted = () =>
            {
                if (Interlocked.Exchange(ref signaled, 1) != 0) return;
                dispatched.TrySetResult();
                acknowledged.Task.WaitAsync(lifetime.Token).GetAwaiter().GetResult();
            },
        };
        var execution = ExecuteSourceAsync();
        try
        {
            if (!execution.IsCompleted) await Task.WhenAny(dispatched.Task, execution).ConfigureAwait(false);
            if (dispatched.Task.IsCompletedSuccessfully)
            {
                yield return new() { Kind = "dispatched" };
                acknowledged.TrySetResult();
            }
            yield return new() { Kind = "result", Result = McpProviderValues.Convert<Wire.AgentToolResult>(await execution.ConfigureAwait(false)) };
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            acknowledged.TrySetCanceled(lifetime.Token);
            try { await execution.ConfigureAwait(false); }
            catch (Exception) when (execution.IsCanceled || execution.IsFaulted) { /* Observed or abandoned execution is drained. */ }
        }
        async Task<Domain.AgentToolResult> ExecuteSourceAsync()
        {
            await Task.Yield();
            if (source is not IAgentToolDispatchAwareSource) context.DispatchStarted?.Invoke();
            return await source.ExecuteAsync(context, McpProviderValues.Convert<Domain.AgentToolRequest>(request.Request), lifetime.Token).ConfigureAwait(false);
        }
    }
}
