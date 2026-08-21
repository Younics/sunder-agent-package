using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Protocol;

internal static class AgentObservedToolExecutionRpc
{
    internal const string ServiceId = "tool-execution-observed";

    internal static ISunderRpcServiceHandler CreateHandler(
        IAgentObservedToolExecution source,
        AgentRpcCatalog? rpcCatalog = null)
        => AgentRpcServiceHandler.Create(AgentRpcContractDescriptors.Get(AgentRpcContractIds.ObservedToolExecution))
            .AddUnary<AgentRpcEmpty, ObservedToolExecutionDescription>(
                ServiceId,
                "describe",
                (_, _, _) => ValueTask.FromResult(new ObservedToolExecutionDescription(source.SourceId)))
            .AddStream<ToolInvocationRequest, ObservedToolInvocationEvent>(
                ServiceId,
                "execute",
                (_, request, token) => ExecuteAsync(source, request, rpcCatalog, token))
            .Build();

    private static async IAsyncEnumerable<ObservedToolInvocationEvent> ExecuteAsync(
        IAgentObservedToolExecution source,
        ToolInvocationRequest request,
        AgentRpcCatalog? rpcCatalog,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var dispatchRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchAcknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchSignaled = 0;
        var context = (await AgentToolSourceRpc.FromWireAsync(request.Context, rpcCatalog, cancellationToken)
                .ConfigureAwait(false)) with
        {
            DispatchStarted = () =>
            {
                if (Interlocked.Exchange(ref dispatchSignaled, 1) != 0)
                {
                    return;
                }

                dispatchRaised.TrySetResult();
                dispatchAcknowledged.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            },
        };
        var execution = ExecuteSourceAsync();
        try
        {
            if (!execution.IsCompleted)
            {
                _ = await Task.WhenAny(dispatchRaised.Task, execution).ConfigureAwait(false);
            }
            if (dispatchRaised.Task.IsCompletedSuccessfully)
            {
                yield return ObservedToolInvocationEvent.Dispatched();
                dispatchAcknowledged.TrySetResult();
            }

            yield return ObservedToolInvocationEvent.Completed(await execution.ConfigureAwait(false));
        }
        finally
        {
            dispatchAcknowledged.TrySetCanceled(cancellationToken);
        }

        async Task<AgentToolResult> ExecuteSourceAsync()
        {
            await Task.Yield();
            if (source is not IAgentToolDispatchAwareSource)
            {
                context.DispatchStarted();
            }
            return await source.ExecuteAsync(context, request.Request, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class AgentObservedToolExecutionRpcClient(
    ISunderRpcClient client,
    SunderRpcEndpointReference endpoint) : IAgentObservedToolExecution
{
    private ObservedToolExecutionDescription? _description;

    public string SourceId
        => (_description ??= DescribeAsync().AsTask().GetAwaiter().GetResult()).SourceId;

    internal ValueTask<ObservedToolExecutionDescription> DescribeAsync(
        CancellationToken cancellationToken = default)
        => AgentRpcServiceHandler.InvokeAsync<AgentRpcEmpty, ObservedToolExecutionDescription>(
            client,
            endpoint,
            AgentObservedToolExecutionRpc.ServiceId,
            "describe",
            new AgentRpcEmpty(),
            cancellationToken);

    public async ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        await foreach (var item in AgentRpcServiceHandler.SubscribeAsync<ToolInvocationRequest, ObservedToolInvocationEvent>(
                           client,
                           endpoint,
                           AgentObservedToolExecutionRpc.ServiceId,
                           "execute",
                           new ToolInvocationRequest(AgentToolSourceRpc.ToWire(context), request),
                           AgentRpcServiceHandler.CreateExtendedCallOptions(),
                           cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(item.Kind, ObservedToolInvocationEvent.DispatchKind, StringComparison.Ordinal))
            {
                context.DispatchStarted?.Invoke();
                continue;
            }
            if (string.Equals(item.Kind, ObservedToolInvocationEvent.ResultKind, StringComparison.Ordinal))
            {
                return item.Result
                       ?? throw new InvalidOperationException(
                           "The observed tool execution returned an empty result event.");
            }
            throw new InvalidOperationException(
                $"The observed tool execution returned unknown event kind '{item.Kind}'.");
        }

        throw new InvalidOperationException("The observed tool execution ended without a result event.");
    }
}

internal sealed record ObservedToolExecutionDescription(string SourceId);

internal sealed record ObservedToolInvocationEvent(string Kind, AgentToolResult? Result = null)
{
    internal const string DispatchKind = "dispatched";
    internal const string ResultKind = "result";

    internal static ObservedToolInvocationEvent Dispatched() => new(DispatchKind);

    internal static ObservedToolInvocationEvent Completed(AgentToolResult result)
        => new(ResultKind, result);
}
